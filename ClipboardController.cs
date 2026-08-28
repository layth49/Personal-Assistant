using System;
using System.Threading;
using System.Windows.Forms;

namespace Personal_Assistant.ClipboardControl
{
    // Reads and writes the Windows clipboard.
    //
    // Every call is marshalled onto a dedicated STA thread. This is not
    // defensive boilerplate: OLE requires an STA for clipboard access, and this
    // app has no STA to borrow — Program.Main is `async Task Main` with no
    // [STAThread], so the main thread is MTA and every tool handler runs on a
    // thread-pool thread after the first await (both measured MTA).
    //
    // The failure this avoids is worse than an exception. Measured on this
    // machine: Clipboard.GetText() called from an MTA thread does not throw, it
    // returns "" — and ContainsText() answers false. So a handler without this
    // marshalling would cheerfully announce "your clipboard is empty" every
    // single time, for a clipboard full of text, with nothing in any log to say
    // the clipboard was never actually consulted.
    public class ClipboardController
    {
        // The clipboard is a single shared OS resource and any process can hold
        // it open; when one does, OLE fails the call rather than waiting.
        // Retrying briefly turns "Chrome happened to be copying" from a failure
        // into a pause nobody notices.
        private const int Attempts = 3;
        private const int RetryDelayMs = 80;

        // What the clipboard currently holds, or null when it holds no text.
        // The distinction matters to the caller: null is "nothing to read",
        // not "the read failed" — a failure throws.
        public string GetText()
        {
            return RunOnStaThread(() =>
            {
                if (!Clipboard.ContainsText()) return null;
                string text = Clipboard.GetText();
                return string.IsNullOrEmpty(text) ? null : text;
            });
        }

        // Describes non-text clipboard content so the assistant can say what IS
        // there instead of a bare "nothing". Returns null when the clipboard is
        // genuinely empty.
        public string DescribeNonText()
        {
            return RunOnStaThread(() =>
            {
                if (Clipboard.ContainsImage()) return "an image";
                if (Clipboard.ContainsFileDropList())
                {
                    int count = Clipboard.GetFileDropList().Count;
                    return count == 1 ? "a file" : $"{count} files";
                }
                if (Clipboard.ContainsAudio()) return "audio";
                return null;
            });
        }

        public void SetText(string text)
        {
            // Clipboard.SetText throws on null/empty rather than clearing, so an
            // empty request is routed to Clear() instead of failing.
            RunOnStaThread<object>(() =>
            {
                if (string.IsNullOrEmpty(text)) Clipboard.Clear();
                else Clipboard.SetText(text);
                return null;
            });
        }

        // Runs `work` on a fresh STA thread and returns its result, rethrowing
        // whatever it threw on the calling thread so callers see real errors
        // rather than a silent default.
        private static T RunOnStaThread<T>(Func<T> work)
        {
            T result = default(T);
            Exception failure = null;

            var thread = new Thread(() =>
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        result = work();
                        failure = null;
                        return;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        if (attempt >= Attempts) return;
                        Thread.Sleep(RetryDelayMs);
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();

            if (failure != null) throw failure;
            return result;
        }
    }
}
