using System;
using System.Threading.Tasks;
using Windows.Media.Control;
using WindowsInput;
using WindowsInput.Native;

namespace Personal_Assistant.MediaControl
{
    // Play/pause and track navigation for whatever app currently owns the
    // system media session (Spotify, a browser tab, a video player, ...).
    //
    // Track navigation uses the standard multimedia virtual keys, which Windows
    // routes to the active media session without needing to target an app.
    //
    // Play and pause do NOT use those keys. MEDIA_PLAY_PAUSE is a toggle, and a
    // toggle is the wrong shape for a voice command: "unpause my video" sent
    // while something is already playing pauses it instead, and asking again
    // toggles straight back. That is what happened in practice — three
    // "unpause" requests in a row, each flipping the state the last one set,
    // and the video never resumed.
    //
    // SMTC exposes explicit TryPlayAsync/TryPauseAsync, so play means play and
    // pause means pause however many times you ask. The key toggle stays only
    // as a fallback for sessions that don't implement the explicit controls,
    // and there it is guarded by the reported playback status so it still
    // cannot flip a session that is already in the requested state.
    public class MediaController
    {
        private readonly InputSimulator simulator = new InputSimulator();

        public void PlayPause() => Press(VirtualKeyCode.MEDIA_PLAY_PAUSE);

        public void Next() => Press(VirtualKeyCode.MEDIA_NEXT_TRACK);

        public void Previous() => Press(VirtualKeyCode.MEDIA_PREV_TRACK);

        public void Stop() => Press(VirtualKeyCode.MEDIA_STOP);

        /// <summary>Ensures playback is running. Idempotent.</summary>
        public Task<bool> PlayAsync() => SetPlaybackAsync(play: true);

        /// <summary>Ensures playback is paused. Idempotent.</summary>
        public Task<bool> PauseAsync() => SetPlaybackAsync(play: false);

        private async Task<bool> SetPlaybackAsync(bool play)
        {
            string want = play ? "play" : "pause";
            try
            {
                GlobalSystemMediaTransportControlsSessionManager manager =
                    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                GlobalSystemMediaTransportControlsSession session = manager?.GetCurrentSession();
                if (session == null)
                {
                    // Nothing owns the media session, so there is nothing to play
                    // or pause. Pressing the key here would hand the command to
                    // whatever grabs the session next.
                    Console.WriteLine($"[media] no active media session — {want} ignored.");
                    return false;
                }

                // Already in the requested state: report success without touching
                // anything. This is what makes the command idempotent.
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                if (info != null)
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus status = info.PlaybackStatus;
                    bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    bool paused = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;

                    if ((play && playing) || (!play && paused))
                    {
                        Console.WriteLine($"[media] already {(play ? "playing" : "paused")}.");
                        return true;
                    }
                }

                bool ok = play
                    ? await session.TryPlayAsync()
                    : await session.TryPauseAsync();

                if (ok)
                {
                    Console.WriteLine($"[media] {want} via SMTC.");
                    return true;
                }

                // The session exists but refused the explicit control. Some apps
                // implement only the toggle, and the status check above already
                // established the state needs changing, so a toggle is safe here.
                Console.WriteLine($"[media] SMTC refused {want}; falling back to the toggle key.");
                PlayPause();
                return true;
            }
            catch (Exception ex)
            {
                // WinRT interop on .NET Framework: surface and fall back rather
                // than take down the turn.
                Console.WriteLine($"[media] {want} failed ({ex.Message}); falling back to the toggle key.");
                PlayPause();
                return true;
            }
        }

        private void Press(VirtualKeyCode key) => simulator.Keyboard.KeyPress(key);
    }
}
