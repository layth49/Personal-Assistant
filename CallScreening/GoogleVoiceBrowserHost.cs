using Personal_Assistant.Configuration;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// Owns the headless Chromium that holds the Google Voice client, and talks to
    /// it over the Chrome DevTools Protocol.
    /// </summary>
    /// <remarks>
    /// WHY A BROWSER AT ALL, given it is the least appealing dependency here: it
    /// is the only remaining way to receive Google Voice call audio. Checked
    /// 2026-08-21 — the third-party libraries (pygooglevoice, gv4j, the PHP one)
    /// are all pre-2018 web-UI scrapers, and even when they worked "place a call"
    /// meant click-to-call, which never carries audio. OBi hardware, the one real
    /// SIP bridge, had its provisioning shut down 2024-10-31 and the devices
    /// discontinued in January 2026. Google moved off XMPP in 2018 to a
    /// proprietary SIP flavour only its own clients speak. Driving the real web
    /// client is therefore not a shortcut, it is the whole menu — and it is the
    /// honest option too: this IS Google's client, merely automated, rather than
    /// something impersonating one against a real account.
    ///
    /// WHY HEADLESS IS SAFE HERE, when it usually is not. Headless Chromium is
    /// widely assumed to have no real audio backend, which would be fatal — the
    /// whole call path depends on real WASAPI endpoints. Measured 2026-08-21
    /// rather than assumed: --headless=new produced genuine render AND capture
    /// sessions on the DEFAULT endpoints, with a real microphone, verified against
    /// a headed control run of the same page. That is why CallAudioRouter's
    /// existing "move all three roles" behaviour redirects this unchanged.
    ///
    /// THE ONE FLAG THAT MUST NOT APPEAR: --use-fake-device-for-media-stream. It
    /// swaps in a synthetic audio device, so getUserMedia succeeds, the logs look
    /// healthy, and NO WASAPI capture session is ever created — the caller hears
    /// silence and nothing says why. --use-fake-ui-for-media-stream is the one we
    /// want: real device, permission prompt auto-accepted, because in headless
    /// there is nobody to click it.
    ///
    /// SIGNING IN IS A HEADED, MANUAL, ONE-TIME STEP. Google routinely refuses
    /// sign-in from automated browsers. The profile directory is therefore
    /// persistent and shared: sign in once with a visible window, and every
    /// headless run afterwards reuses that session. The per-profile Google Voice
    /// audio device settings are chosen in that same visit — in particular
    /// pinning RINGING to the real speakers, so the ringtone is not rendered into
    /// the virtual cable and captured as the caller's opening words.
    /// </remarks>
    public sealed class GoogleVoiceBrowserHost : IDisposable
    {
        public const string VoiceUrl = "https://voice.google.com";

        private readonly string chromePath;
        private readonly string profileDir;
        private readonly int port;
        private readonly bool headless;
        private readonly bool offScreen;

        private Process chrome;

        // The one page this host watches for calls. The CDP mechanics live in
        // CdpPage so that a second page — the one a text is sent from — cannot
        // end up with a second, subtly different copy of them.
        private CdpPage page;

        // The call page's CDP target id, kept so it can be pulled back to the
        // front after a scratch tab steals the foreground. See OpenScratchTabAsync.
        private string pageTargetId;

        /// <summary>
        /// Normally every setting comes from LaithConfig. The two overrides exist
        /// for the one-time signed-in setup visit, which needs a VISIBLE,
        /// ON-SCREEN window regardless of what the config says.
        /// </summary>
        /// <remarks>
        /// Explicit parameters rather than setting LAITH_* environment variables,
        /// which is what this first tried and which failed silently: LaithConfig
        /// maps "CallGvHeadless" to LAITH_CALL_GV_HEADLESS, not
        /// LAITH_CallGvHeadless, so the override was written to a name nothing
        /// reads and the browser launched headless while claiming it would not.
        /// A parameter cannot drift out of sync with that mapping.
        /// </remarks>
        public GoogleVoiceBrowserHost(bool? headlessOverride = null, bool? offScreenOverride = null)
        {
            chromePath = ResolveChrome(LaithConfig.Text("CallGvChromePath", ""));
            profileDir = ResolveProfile(LaithConfig.Text("CallGvProfileDir", ""));
            port = LaithConfig.Int("CallGvDebugPort", 9333, 1024, 65535);
            headless = headlessOverride ?? LaithConfig.Bool("CallGvHeadless", true);
            // Only meaningful when windowed.
            offScreen = offScreenOverride ?? LaithConfig.Bool("CallGvOffScreen", true);
        }

        /// <summary>Where the browser profile lives, so setup guidance can name it.</summary>
        public string ProfileDirectory => profileDir;
        public string ChromePath => chromePath;

        /// <summary>
        /// The DevTools port this browser listens on, so a second tab can be
        /// opened against the same instance.
        /// </summary>
        public int DebugPort => port;

        /// <summary>True once the browser is up and a Google Voice page is attached.</summary>
        public bool IsAttached => page != null && page.IsOpen;

        /// <summary>
        /// Launches the browser (if it is not already ours) and attaches to the
        /// Google Voice page. Safe to call again to re-attach after a crash.
        /// </summary>
        public async Task StartAsync(CancellationToken cancel = default)
        {
            if (IsAttached) return;

            // Re-attach is ordinary, not exceptional: the browser can be closed,
            // crash, or be restarted while screening is armed. Tear the previous
            // attempt down first — a dead socket left undisposed also leaves its
            // receive pump running, and two pumps racing on one `pending` map
            // resolves replies against the wrong request. Disposing also fails
            // everything still waiting on the dead connection, which beats a
            // ten-second timeout each while a call is ringing.
            try { page?.AbandonPending("devtools connection was replaced"); } catch { }
            try { page?.Dispose(); } catch { }
            page = null;

            if (chromePath == null)
                throw new InvalidOperationException(
                    "no Chrome/Edge found — set CallGvChromePath to a Chromium executable");

            if (chrome == null || chrome.HasExited) LaunchBrowser();

            string wsUrl = await FindVoiceTargetAsync(cancel).ConfigureAwait(false);
            if (wsUrl == null)
                throw new InvalidOperationException(
                    "browser started but no " + VoiceUrl + " page appeared on port " + port);

            page = await CdpPage.AttachAsync(wsUrl, cancel).ConfigureAwait(false);
            pageTargetId = CdpPage.TargetIdFrom(wsUrl);

            Console.WriteLine(
                "[call/gv] attached to " + (headless ? "headless" : "windowed") +
                " browser on port " + port);

            await EnsureOnVoiceAsync(cancel).ConfigureAwait(false);

            // Being on the page is not being ready to answer it — see
            // WaitUntilReadyAsync. Done here so that by the time StartAsync
            // returns, "attached" genuinely means "a call would ring".
            await WaitUntilReadyAsync(cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// Makes sure the attached page is actually Google Voice, and drives it
        /// there if it is not.
        /// </summary>
        /// <remarks>
        /// FindVoiceTargetAsync will settle for any page rather than none, so that
        /// a signed-out profile reports "signed out" instead of "no browser". That
        /// fallback has a failure mode of its own, and it bit on 2026-08-22: a
        /// stale browser on the same profile left a chrome://newtab page, the host
        /// attached to it, the poll ran happily against a tab with no call UI on
        /// it, and calls rang through to voicemail with NOTHING in the log to say
        /// why. Attaching is not the same as being in the right place.
        ///
        /// So the page is checked and, if necessary, navigated — and the URL is
        /// logged either way, because "which page am I actually watching" turned
        /// out to be the question nothing could answer.
        /// </remarks>
        public async Task EnsureOnVoiceAsync(CancellationToken cancel = default)
        {
            string href = await HrefAsync(cancel).ConfigureAwait(false);
            Console.WriteLine("[call/gv] page is " + (href ?? "unreadable"));

            if (OnVoice(href)) return;

            Console.WriteLine("[call/gv] that is not Google Voice — navigating there.");
            try
            {
                await EvalAsync("location.assign(" + JsonSerializer.Serialize(VoiceUrl) + ")", cancel)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Navigating destroys the execution context the reply would have
                // come back on, so a failure here says nothing about whether the
                // navigation took. The poll below is the real answer.
            }

            DateTime deadline = DateTime.Now.AddSeconds(
                LaithConfig.Int("CallGvAttachTimeoutSeconds", 30, 5, 180));

            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                await Task.Delay(500, cancel).ConfigureAwait(false);

                href = await HrefAsync(cancel).ConfigureAwait(false);
                if (OnVoice(href))
                {
                    Console.WriteLine("[call/gv] now on " + href);
                    return;
                }
            }

            // Loud, because everything downstream will look perfectly healthy:
            // the socket is up, the poll succeeds, and no call ever rings.
            Console.WriteLine(
                "[call/gv] WARNING: could not get the page onto Google Voice (still " +
                (href ?? "unreadable") + "). NOTHING WILL RING. " +
                "If it is a sign-in page, run 'GvProbe setup' and sign in again.");
        }

        /// <summary>
        /// Waits until the Google Voice app has actually BOOTED, not merely until
        /// the URL looks right.
        /// </summary>
        /// <remarks>
        /// These are not the same thing, and conflating them cost a call on
        /// 2026-08-22. The host found a stale chrome://new-tab-page, navigated it
        /// to Google Voice, logged "now on https://voice.google.com/u/0/" — and
        /// the call still rang through to voicemail. A page one instant after a JS
        /// navigation has a URL and nothing else: the Polymer shell is still
        /// booting and the client has not registered to receive calls, so there is
        /// nothing for Google to ring.
        ///
        /// "Call panel" is the aria-label of the dialpad container, measured on the
        /// live page (GvProbe watch, 2026-08-21). It appears once the app proper is
        /// up, which makes it a far better readiness signal than location.href.
        /// </remarks>
        public async Task<bool> WaitUntilReadyAsync(CancellationToken cancel = default)
        {
            const string probe =
                "(() => { const all = [];" +
                "  (function walk(r){ for (const el of r.querySelectorAll('*')) {" +
                "      all.push(el); if (el.shadowRoot) walk(el.shadowRoot); } })(document);" +
                "  return all.some(el => el.getAttribute &&" +
                "    el.getAttribute('aria-label') === 'Call panel'); })()";

            DateTime deadline = DateTime.Now.AddSeconds(
                LaithConfig.Int("CallGvReadyTimeoutSeconds", 45, 5, 300));

            while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
            {
                try
                {
                    JsonElement v = await EvalAsync(probe, cancel).ConfigureAwait(false);
                    if (v.ValueKind == JsonValueKind.True)
                    {
                        Console.WriteLine("[call/gv] client is up and able to receive calls.");
                        return true;
                    }
                }
                catch
                {
                    // Mid-navigation the execution context is destroyed and the
                    // eval throws. That is "not ready yet", not a failure.
                }

                await Task.Delay(1000, cancel).ConfigureAwait(false);
            }

            Console.WriteLine(
                "[call/gv] WARNING: the Google Voice client did not finish loading. " +
                "It may not ring. Check the profile is still signed in ('GvProbe attach').");
            return false;
        }

        private static bool OnVoice(string href) =>
            href != null && href.StartsWith(VoiceUrl, StringComparison.OrdinalIgnoreCase);

        private async Task<string> HrefAsync(CancellationToken cancel)
        {
            try
            {
                JsonElement v = await EvalAsync("location.href", cancel).ConfigureAwait(false);
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            catch { return null; }
        }

        private void LaunchBrowser()
        {
            Directory.CreateDirectory(profileDir);

            var args = new StringBuilder();
            if (headless) args.Append("--headless=new ");
            args.Append("--remote-debugging-port=" + port + " ");
            // Loopback only. This port can drive a signed-in Google session, so it
            // must never be reachable from anywhere but this machine.
            args.Append("--remote-debugging-address=127.0.0.1 ");
            args.Append("--user-data-dir=\"" + profileDir + "\" ");
            args.Append("--no-first-run --no-default-browser-check ");
            // Real microphone, prompt auto-accepted. NOT the fake-device flag —
            // see the class remarks; that one fails silently and expensively.
            args.Append("--use-fake-ui-for-media-stream ");
            // A ringtone and a greeting both have to start without a click.
            args.Append("--autoplay-policy=no-user-gesture-required ");
            // Windowed fallback keeps the window OFF-SCREEN rather than minimised.
            // A minimised window reports visibilityState "hidden" and invites the
            // throttling that would stop the client noticing a call; an off-screen
            // one stays "visible". The three backgrounding flags below cover the
            // same hazard coming from Windows' own occlusion tracking.
            if (!headless && offScreen) args.Append("--window-position=-32000,-32000 ");
            args.Append("--disable-background-timer-throttling ");
            args.Append("--disable-renderer-backgrounding ");
            args.Append("--disable-backgrounding-occluded-windows ");
            args.Append(VoiceUrl);

            chrome = Process.Start(new ProcessStartInfo
            {
                FileName = chromePath,
                Arguments = args.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        /// <summary>
        /// Polls the DevTools HTTP endpoint until a Google Voice page shows up. It
        /// is not there the instant the process starts, and a cold profile is much
        /// slower than later runs.
        /// </summary>
        private async Task<string> FindVoiceTargetAsync(CancellationToken cancel)
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
            {
                DateTime deadline = DateTime.Now.AddSeconds(
                    LaithConfig.Int("CallGvAttachTimeoutSeconds", 30, 5, 180));

                while (DateTime.Now < deadline && !cancel.IsCancellationRequested)
                {
                    try
                    {
                        string json = await http
                            .GetStringAsync("http://127.0.0.1:" + port + "/json/list")
                            .ConfigureAwait(false);

                        using (JsonDocument doc = JsonDocument.Parse(json))
                        {
                            // Preferred: a page actually sitting on Google Voice.
                            // Fallback: ANY page. A signed-out profile is bounced
                            // to workspace.google.com, and refusing to attach then
                            // would report "no browser" when the truth is "signed
                            // out" — the caller can only tell the difference if we
                            // attach and let it read location.href. Failing loudly
                            // about the right thing is worth one extra loop here.
                            string anyPage = null;

                            foreach (JsonElement t in doc.RootElement.EnumerateArray())
                            {
                                if (!t.TryGetProperty("type", out JsonElement type) ||
                                    type.GetString() != "page") continue;
                                if (!t.TryGetProperty("webSocketDebuggerUrl", out JsonElement ws))
                                    continue;

                                string sock = ws.GetString();
                                if (sock == null) continue;

                                string u = t.TryGetProperty("url", out JsonElement url)
                                    ? url.GetString() : null;

                                if (u != null &&
                                    u.StartsWith(VoiceUrl, StringComparison.OrdinalIgnoreCase))
                                    return sock;

                                // about:blank is the pre-navigation placeholder, not
                                // a real destination; never settle for it.
                                if (anyPage == null && u != null && u != "about:blank")
                                    anyPage = sock;
                            }

                            // Only after a full pass, so a real Voice page always wins.
                            if (anyPage != null && DateTime.Now > deadline.AddSeconds(-5))
                                return anyPage;
                        }
                    }
                    catch
                    {
                        // The port is simply not listening yet. Expected on a cold
                        // start; only the deadline is worth reporting.
                    }

                    await Task.Delay(500, cancel).ConfigureAwait(false);
                }
            }

            return null;
        }

        /// <summary>
        /// Runs JavaScript in the Google Voice page and returns its value.
        /// </summary>
        /// <remarks>
        /// Everything this class needs from CDP is one method. Detection and
        /// clicking are both "ask the page a question" or "tell the page to do a
        /// thing", and doing that in JS rather than through DOM.querySelector
        /// round-trips keeps each operation a single message — which matters when
        /// the poll runs twice a second and a call only rings for twenty.
        /// </remarks>
        public Task<JsonElement> EvalAsync(string js, CancellationToken cancel = default)
        {
            CdpPage attached = page;
            if (attached == null || !attached.IsOpen)
                throw new InvalidOperationException("not attached to a browser");

            return attached.EvalAsync(js, cancel);
        }

        /// <summary>
        /// Opens a SECOND tab in this same browser, for work that must not touch
        /// the page that watches for calls. The caller disposes it, which closes
        /// the tab.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Starts the browser first if it is not up. Sending a text is the only
        /// caller today, and it can be the thing that brings the browser up —
        /// screening may have been idle, and refusing to deliver a message
        /// because no call has rung recently would be exactly backwards.
        /// </para>
        /// <para>
        /// THE CALL TAB IS PULLED BACK TO THE FRONT IMMEDIATELY. Measured
        /// 2026-08-23: opening any second tab drops the first to
        /// visibilityState "hidden", and this class goes to real trouble
        /// elsewhere (off-screen rather than minimised, three anti-throttling
        /// flags) to keep the Google Voice client out of exactly that state. One
        /// activate puts the watcher back in the foreground and leaves the
        /// scratch tab hidden instead — which costs it nothing, since clicking
        /// and typing through CDP do not care whether a tab is on screen.
        /// </para>
        /// </remarks>
        public async Task<CdpPage> OpenScratchTabAsync(
            string url, CancellationToken cancel = default)
        {
            if (!IsAttached) await StartAsync(cancel).ConfigureAwait(false);

            CdpPage scratch = await CdpPage.OpenNewTabAsync(port, url, cancel).ConfigureAwait(false);
            CdpPage.Activate(port, pageTargetId);
            return scratch;
        }

        private static string ResolveChrome(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
                return File.Exists(configured) ? configured : null;

            // Chrome first: it is what the headless audio behaviour was measured
            // against. Edge is the same engine and ships with Windows, so it is a
            // reasonable fallback rather than a failure.
            string[] candidates =
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
                @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            };

            foreach (string c in candidates) if (File.Exists(c)) return c;
            return null;
        }

        private static string ResolveProfile(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            // Its own directory, never the real browser profile. Sharing one would
            // mean automation driving the browser he reads email in, and a stray
            // tab or a restart taking screening down with it.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LAITH", "gvprofile");
        }

        public void Dispose()
        {
            try { page?.Dispose(); } catch { }
            page = null;

            // Only ever kills the browser this class started. A signed-in window
            // the user opened by hand for setup is not ours to close.
            //
            // KILLED AS A TREE, not as one process. Chrome forks a renderer, a
            // GPU process and a handful of utilities; Process.Kill() on .NET
            // Framework has no entireProcessTree overload and takes only the
            // parent. Measured 2026-08-22: one probe run left TWELVE chrome
            // processes alive on this profile, and the next launch handed off to
            // that half-dead instance instead of starting cleanly — which is how
            // the app ended up attached to a chrome://new-tab-page and a real call
            // rang through to voicemail while every log line looked healthy.
            try
            {
                if (chrome != null && !chrome.HasExited)
                {
                    using (Process kill = Process.Start(new ProcessStartInfo
                    {
                        FileName = "taskkill",
                        Arguments = "/PID " + chrome.Id + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }))
                    {
                        kill?.WaitForExit(5000);
                    }
                }
            }
            catch
            {
                // Fall back to taking at least the parent. A stale child is worth
                // less than a crash on the way out.
                try { if (chrome != null && !chrome.HasExited) chrome.Kill(); } catch { }
            }

            try { chrome?.Dispose(); } catch { }
        }
    }
}
