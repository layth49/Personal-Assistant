using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using Personal_Assistant.Dispatch;
using Personal_Assistant.Live;

namespace Personal_Assistant.VoiceClips
{
    // Pre-rendered audio for the handful of lines the assistant says outside a
    // Live conversation — the wake greeting and the goodbye.
    //
    // Those two are spoken by Azure TTS because they happen either side of the
    // socket, so the assistant used to answer in one voice and greet in another.
    // Rendering them ONCE through the Live API and playing the cached file fixes
    // that without giving up the instant greeting: the greeting is what covers
    // socket setup, so it cannot wait for the model to produce it.
    //
    // The Live API is unmetered on this project's free tier (unlimited RPM/RPD,
    // measured 2026-08-04), so rendering is free. The TTS model is NOT — it is
    // 3/min and 10/day — which is exactly why this renders through Live instead.
    public static class VoiceClipCache
    {
        // Next to the exe, like keyword.table, so it survives both the bin\Debug
        // and the deploy-folder launch layouts.
        public static string Root =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "voiceclips");

        // Keyed by voice AND text: changing either must miss, or switching voices
        // would silently keep playing the old one. Hash rather than the sentence
        // itself because these contain punctuation that is not path-legal.
        public static string PathFor(string voice, string text)
        {
            if (string.IsNullOrEmpty(voice) || string.IsNullOrEmpty(text)) return null;

            using (var sha = SHA1.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text.Trim()));
                var name = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) name.Append(b.ToString("x2"));
                return Path.Combine(Root, voice, name.ToString() + ".wav");
            }
        }

        public static bool TryGet(string voice, string text, out string path)
        {
            path = PathFor(voice, text);
            // A truncated render (header only, or an interrupted write) is worse
            // than a miss, because it plays as silence and looks like a dead app.
            return path != null && File.Exists(path) && new FileInfo(path).Length > 1024;
        }

        // Plays to completion. The greeting has to finish before the Live session
        // opens the microphone, or the model hears it as the user's first
        // utterance — so this genuinely needs to be awaitable, not fire-and-forget.
        public static async Task PlayAsync(string path)
        {
            var done = new TaskCompletionSource<bool>();

            // Disposed inside the handler rather than by a using block: the method
            // returns at the first await, and disposing the device then would cut
            // the clip off after a few milliseconds.
            var reader = new WaveFileReader(path);
            var output = new WaveOutEvent();

            output.PlaybackStopped += (s, e) =>
            {
                try { reader.Dispose(); } catch { }
                try { output.Dispose(); } catch { }
                if (e.Exception != null) done.TrySetException(e.Exception);
                else done.TrySetResult(true);
            };

            output.Init(reader);
            output.Play();
            await done.Task.ConfigureAwait(false);
        }

        public static void WriteWav(string path, byte[] pcm, int sampleRate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            // Written beside the target and moved into place, so a crash mid-write
            // can never leave a half-file that TryGet would then treat as a hit.
            string temp = path + ".tmp";
            using (var w = new WaveFileWriter(temp, new WaveFormat(sampleRate, 16, 1)))
            {
                w.Write(pcm, 0, pcm.Length);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(temp, path);
        }
    }

    // Renders lines to the clip cache using the Live API, in the same voice the
    // conversation itself uses. Run from Program's --render-clips switch.
    public static class VoiceClipRenderer
    {
        // The model is conversational and will happily answer a line instead of
        // reading it, so the instruction has to be unambiguous.
        private const string RenderInstruction =
            "You are a text-to-speech renderer. Speak the user's message back " +
            "word for word, in a natural tone. Never answer it, never comment on " +
            "it, never add or omit a single word.";

        public static async Task<int> RenderAsync(IReadOnlyList<string> lines, string voice)
        {
            if (string.IsNullOrEmpty(voice))
            {
                Console.WriteLine(
                    "[clips] no voice configured — set LiveVoice in " +
                    "'Personal Assistant.exe.config' first");
                return 0;
            }

            int written = 0, skipped = 0, failed = 0;
            Console.WriteLine($"[clips] rendering {lines.Count} line(s) as '{voice}' into {VoiceClipCache.Root}");

            foreach (string line in lines)
            {
                if (VoiceClipCache.TryGet(voice, line, out _))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var sw = Stopwatch.StartNew();
                    await RenderOneAsync(line, voice).ConfigureAwait(false);
                    Console.WriteLine($"[clips] ok {sw.ElapsedMilliseconds,5}ms  {Preview(line)}");
                    written++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[clips] FAIL {Preview(line)} -- {ex.Message}");
                    failed++;
                }
            }

            Console.WriteLine($"[clips] wrote {written}, skipped {skipped}, failed {failed}");
            return failed;
        }

        // Renders a line that was not pre-rendered, and caches it, so dynamic text
        // (a labelled reminder, say) is still spoken in the Live voice. The first
        // utterance of a given line pays the render; every later one is a cache hit.
        //
        // Returns false rather than throwing: the caller's fallback is Azure TTS,
        // and a late announcement in the wrong voice beats no announcement at all.
        public static async Task<bool> TryEnsureAsync(string voice, string line)
        {
            if (string.IsNullOrEmpty(voice) || string.IsNullOrWhiteSpace(line)) return false;
            if (VoiceClipCache.TryGet(voice, line, out _)) return true;

            var sw = Stopwatch.StartNew();
            try
            {
                await RenderOneAsync(line, voice).ConfigureAwait(false);
                Console.WriteLine($"[clips] rendered on demand in {sw.ElapsedMilliseconds}ms: {Preview(line)}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[clips] on-demand render failed after {sw.ElapsedMilliseconds}ms: {ex.Message}");
                return false;
            }
        }

        private static async Task RenderOneAsync(string line, string voice)
        {
            var options = new LiveSessionOptions
            {
                Voice = voice,
                SystemInstruction = RenderInstruction,
                // No tools and no grounding: this session only ever narrates, and
                // a toolCall here would be a bug rather than something to handle.
                Tools = new List<ToolDefinition>(),
                EnableGoogleSearch = false,
                // Server-side VAD is irrelevant with no microphone attached, and
                // leaving manual activity detection on would mean sending activity
                // markers this path has no reason to send.
                ManualActivityDetection = false,
                InputAudioTranscription = false,
                OutputAudioTranscription = false,
            };

            var audio = new List<byte>();
            var complete = new TaskCompletionSource<bool>();

            using (var client = new LiveClient(options))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                client.AudioReceived += pcm => audio.AddRange(pcm);
                client.TurnComplete += () => complete.TrySetResult(true);
                client.Closed += (status, reason) =>
                    complete.TrySetException(new Exception($"socket closed: {reason}"));

                await client.ConnectAsync(cts.Token).ConfigureAwait(false);
                await client.SendTextAsync(line, cts.Token).ConfigureAwait(false);

                using (cts.Token.Register(() => complete.TrySetCanceled()))
                {
                    await complete.Task.ConfigureAwait(false);
                }

                // turnComplete means the model stopped generating, not that every
                // chunk has arrived. A short drain keeps the tail of the line.
                await Task.Delay(250).ConfigureAwait(false);

                if (audio.Count == 0) throw new Exception("no audio returned");
                VoiceClipCache.WriteWav(
                    VoiceClipCache.PathFor(voice, line), audio.ToArray(), client.OutputSampleRate);

                await client.CloseAsync("render complete").ConfigureAwait(false);
            }
        }

        private static string Preview(string line) =>
            line.Length <= 48 ? line : line.Substring(0, 45) + "...";
    }
}
