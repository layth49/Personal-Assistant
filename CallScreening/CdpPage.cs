using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.CallScreening
{
    /// <summary>
    /// One Chrome DevTools Protocol conversation with one page: connect, evaluate
    /// JavaScript in it, and close.
    /// </summary>
    /// <remarks>
    /// <para>
    /// EXTRACTED FROM GoogleVoiceBrowserHost, which had all of this inline and
    /// worked. It moved because sending a text needs a SECOND page — see
    /// <see cref="OpenNewTabAsync"/> — and the alternative was a second copy of
    /// the frame reassembly, the id/reply matching and the exceptionDetails
    /// check. Those three are exactly the parts that fail quietly when they are
    /// subtly wrong, so one implementation of them is worth the move. The host's
    /// public surface is unchanged; it now holds one of these.
    /// </para>
    /// <para>
    /// CDP is request/response over a single socket with an id on every message,
    /// so replies come back out of order and have to be matched up by hand.
    /// </para>
    /// </remarks>
    public sealed class CdpPage : IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource pump = new CancellationTokenSource();

        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending =
            new ConcurrentDictionary<int, TaskCompletionSource<JsonElement>>();
        private int nextId;

        // One writer at a time: ClientWebSocket forbids overlapping SendAsync, and
        // the call poll runs on its own thread while other things evaluate on
        // another.
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

        // Set only for a tab this class created, and it is what tells Dispose it
        // has a tab to close. A page we merely attached to is not ours to close —
        // that one belongs to the browser host, and closing it would take call
        // detection down with it.
        private readonly string ownedTargetId;
        private readonly int ownedPort;

        private int disposed;

        private CdpPage(string ownedTargetId = null, int ownedPort = 0)
        {
            this.ownedTargetId = ownedTargetId;
            this.ownedPort = ownedPort;
        }

        /// <summary>Attaches to an existing page. The page is not ours to close.</summary>
        public static async Task<CdpPage> AttachAsync(
            string webSocketUrl, CancellationToken cancel = default)
        {
            var page = new CdpPage();
            try
            {
                await page.ConnectAsync(webSocketUrl, cancel).ConfigureAwait(false);
            }
            catch
            {
                page.Dispose();
                throw;
            }
            return page;
        }

        /// <summary>
        /// Opens a NEW tab in an already-running browser and attaches to it. The
        /// tab is closed on Dispose.
        /// </summary>
        /// <remarks>
        /// WHY A SEPARATE TAB AT ALL. The browser holds exactly one Google Voice
        /// page and a poll reads it twice a second to notice a ringing call.
        /// Driving that same page through the Messages UI to send a text would
        /// mean the one thing watching the phone is busy typing, and a call
        /// arriving mid-send is a call nobody answers — the failure this whole
        /// feature exists to prevent, reintroduced by the fix for it.
        ///
        /// A second tab costs one more renderer and keeps the two jobs from ever
        /// being the same job. The call tab is never navigated, never clicked,
        /// and cannot be left on the wrong view by a failed send.
        ///
        /// PUT, not GET. Chrome has required PUT on /json/new since 111; a GET
        /// returns 405 with a body explaining it, which reads like the endpoint
        /// is missing rather than like the verb is wrong.
        ///
        /// AND THE URL IS ESCAPED, because /json/new takes it as a QUERY VALUE
        /// and decodes it once. Passing it raw cost a measurement on 2026-08-23:
        /// a deep link ending "itemId=t.%2B15048810943" arrived at the tab as
        /// "t.%2015048810943" — the %2B decoded to a plus, which a query string
        /// then reads as a space — so Google Voice was handed a conversation id
        /// for nobody and opened an empty Messages view. It looked exactly like
        /// deep linking not being supported.
        /// </remarks>
        public static async Task<CdpPage> OpenNewTabAsync(
            int port, string url, CancellationToken cancel = default)
        {
            string targetId;
            string ws;

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            using (var request = new HttpRequestMessage(
                HttpMethod.Put,
                "http://127.0.0.1:" + port + "/json/new?" + Uri.EscapeDataString(url)))
            {
                HttpResponseMessage reply = await http.SendAsync(request, cancel).ConfigureAwait(false);
                string body = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!reply.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        "could not open a browser tab (" + (int)reply.StatusCode + "): " + Trim(body));

                using (JsonDocument doc = JsonDocument.Parse(body))
                {
                    targetId = Str(doc.RootElement, "id");
                    ws = Str(doc.RootElement, "webSocketDebuggerUrl");
                }
            }

            if (ws == null)
                throw new InvalidOperationException("the new tab came back with no debugger socket");

            var page = new CdpPage(targetId, port);
            try
            {
                await page.ConnectAsync(ws, cancel).ConfigureAwait(false);
            }
            catch
            {
                // The tab exists even though we never got to speak to it. Leaving
                // it open would leak one renderer per failed send, forever.
                page.Dispose();
                throw;
            }
            return page;
        }

        public bool IsOpen => socket.State == WebSocketState.Open;

        /// <summary>
        /// Brings a tab back to the front, by target id.
        /// </summary>
        /// <remarks>
        /// THE REASON THIS EXISTS, measured 2026-08-23 rather than assumed:
        /// opening a second tab puts the first one into visibilityState
        /// "hidden". The call tab went visible -> hidden -> visible across one
        /// scratch tab's lifetime.
        ///
        /// Hidden is very probably survivable — a real person's Voice tab is
        /// hidden almost all the time and still rings, and the browser is
        /// launched with the three anti-throttling flags — but "very probably"
        /// is not the standard this path is held to, and the fix is one HTTP
        /// call. Activating the call tab straight after opening the scratch tab
        /// leaves the WATCHER in the foreground and the TYPIST in the
        /// background, which is the right way round.
        ///
        /// Over HTTP, like the close: it is answered by the browser process, so
        /// it does not depend on either page being responsive.
        /// </remarks>
        public static void Activate(int port, string targetId)
        {
            if (string.IsNullOrEmpty(targetId)) return;

            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                {
                    http.GetStringAsync("http://127.0.0.1:" + port + "/json/activate/" + targetId)
                        .GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                // Worth a line, never worth failing a delivery over.
                Console.WriteLine("[call/gv] could not re-activate the call tab: " + ex.Message);
            }
        }

        /// <summary>
        /// The target id inside a page's debugger socket URL, which looks like
        /// ws://127.0.0.1:9333/devtools/page/&lt;id&gt;. Null when it does not.
        /// </summary>
        public static string TargetIdFrom(string webSocketUrl)
        {
            if (string.IsNullOrEmpty(webSocketUrl)) return null;
            int slash = webSocketUrl.LastIndexOf('/');
            if (slash < 0 || slash == webSocketUrl.Length - 1) return null;
            return webSocketUrl.Substring(slash + 1);
        }

        private async Task ConnectAsync(string webSocketUrl, CancellationToken cancel)
        {
            await socket.ConnectAsync(new Uri(webSocketUrl), cancel).ConfigureAwait(false);
            _ = Task.Run(() => ReceiveLoopAsync(pump.Token));
        }

        /// <summary>Runs JavaScript in the page and returns its value.</summary>
        public Task<JsonElement> EvalAsync(string js, CancellationToken cancel = default) =>
            CallAsync("Runtime.evaluate", new
            {
                expression = js,
                returnByValue = true,
                awaitPromise = true
            }, cancel);

        /// <summary>
        /// Sends any CDP command and returns its result.
        /// </summary>
        /// <remarks>
        /// Runtime.evaluate was the only command this needed until sending a text
        /// turned out to need REAL typing. Google Voice's recipient box is an
        /// Angular Material autocomplete, and a synthetic input event fills the
        /// field without opening the suggestion list — measured 2026-08-23, and
        /// the failure is silent: the number is in the box, no option appears,
        /// and Send stays disabled with nothing to say why. Input.insertText
        /// enters text at the browser level, below the page, where there is no
        /// difference between it and a keypress.
        /// </remarks>
        public async Task<JsonElement> CallAsync(
            string method, object parameters, CancellationToken cancel = default)
        {
            if (!IsOpen) throw new InvalidOperationException("not attached to a page");

            int id = Interlocked.Increment(ref nextId);
            var tcs = new TaskCompletionSource<JsonElement>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            pending[id] = tcs;

            string message = JsonSerializer.Serialize(new
            {
                id,
                method,
                @params = parameters
            });

            await sendGate.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)),
                    WebSocketMessageType.Text, true, cancel).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }

            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, timeout.Token))
            using (linked.Token.Register(() => tcs.TrySetCanceled()))
            {
                try { return await tcs.Task.ConfigureAwait(false); }
                finally { pending.TryRemove(id, out _); }
            }
        }

        /// <summary>
        /// Fails everything still waiting, because it belongs to a connection that
        /// is going away and will never be answered.
        /// </summary>
        public void AbandonPending(string why)
        {
            foreach (var key in pending.Keys)
            {
                if (pending.TryRemove(key, out TaskCompletionSource<JsonElement> orphan))
                    orphan.TrySetException(new InvalidOperationException(why));
            }
        }

        private async Task ReceiveLoopAsync(CancellationToken cancel)
        {
            var buffer = new byte[64 * 1024];

            try
            {
                while (!cancel.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    // CDP replies routinely exceed one frame, so accumulate until
                    // EndOfMessage rather than parsing whatever arrived first.
                    var whole = new MemoryStream();
                    WebSocketReceiveResult got;
                    do
                    {
                        got = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancel)
                                          .ConfigureAwait(false);
                        if (got.MessageType == WebSocketMessageType.Close) return;
                        whole.Write(buffer, 0, got.Count);
                    }
                    while (!got.EndOfMessage);

                    Dispatch(Encoding.UTF8.GetString(whole.ToArray()));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine("[call/gv] devtools socket closed: " + ex.Message);
            }
        }

        private void Dispatch(string raw)
        {
            try
            {
                using (JsonDocument doc = JsonDocument.Parse(raw))
                {
                    JsonElement root = doc.RootElement;

                    // No id means an event, and nothing here subscribes to any.
                    if (!root.TryGetProperty("id", out JsonElement idEl)) return;
                    if (!pending.TryRemove(idEl.GetInt32(), out TaskCompletionSource<JsonElement> tcs))
                        return;

                    if (root.TryGetProperty("error", out JsonElement err))
                    {
                        tcs.TrySetException(new InvalidOperationException(
                            "devtools error: " + err.ToString()));
                        return;
                    }

                    if (root.TryGetProperty("result", out JsonElement outer))
                    {
                        // An exception thrown by the page arrives as a NORMAL
                        // reply, not an error, so it has to be checked explicitly
                        // or a broken selector reads as "nothing is there".
                        if (outer.TryGetProperty("exceptionDetails", out JsonElement ex))
                        {
                            tcs.TrySetException(new InvalidOperationException(
                                "page threw: " + ex.ToString()));
                            return;
                        }

                        if (outer.TryGetProperty("result", out JsonElement inner) &&
                            inner.TryGetProperty("value", out JsonElement value))
                        {
                            tcs.TrySetResult(value.Clone());
                            return;
                        }
                    }

                    // Two things land here and both are fine. Runtime.evaluate on
                    // an expression returning undefined comes back with no
                    // `value` at all, which is a legitimate answer. And commands
                    // that simply do a thing — Input.insertText — have no result
                    // worth unwrapping; their callers want to know they did not
                    // throw, which is what returning at all means.
                    tcs.TrySetResult(default);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[call/gv] unreadable devtools message: " + ex.Message);
            }
        }

        private static string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? "(no detail)" :
            s.Length <= 200 ? s.Trim() : s.Substring(0, 200).Trim() + "...";

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;

            AbandonPending("the devtools connection is closing");

            try { pump.Cancel(); } catch { }

            // CLOSE THE TAB BEFORE DISPOSING THE SOCKET, and over HTTP rather than
            // over CDP. Target.closeTarget on your own socket races the reply
            // against the socket dying, and CloseAsync on a page that is going
            // away is the deadlock that used to kill call teardown outright (see
            // laith-gv-call-path-traps). The HTTP endpoint answers from the
            // browser process, which is not the thing being closed.
            if (ownedTargetId != null)
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        http.GetStringAsync(
                                "http://127.0.0.1:" + ownedPort + "/json/close/" + ownedTargetId)
                            .GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    // A leaked tab is worth a line but never worth a throw on the
                    // way out of a call.
                    Console.WriteLine("[call/gv] could not close the tab we opened: " + ex.Message);
                }
            }

            try { socket.Dispose(); } catch { }
            try { pump.Dispose(); } catch { }
            try { sendGate.Dispose(); } catch { }
        }
    }
}
