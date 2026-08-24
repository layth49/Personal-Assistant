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
    // What a play/pause command managed to do.
    //
    // Three states rather than a bool because the caller speaks the difference:
    // "nothing is playing" and "the session refused" are different sentences, and
    // collapsing them meant the assistant announced "Nothing is playing right now"
    // about a session that was playing and had merely declined the command.
    public enum MediaCommandResult
    {
        /// <summary>The session is now in the requested state.</summary>
        Done,

        /// <summary>Nothing owns the system media session.</summary>
        NoSession,

        /// <summary>A session exists, but the command did not take.</summary>
        Refused,
    }

    public class MediaController
    {
        private readonly InputSimulator simulator = new InputSimulator();

        public void PlayPause() => Press(VirtualKeyCode.MEDIA_PLAY_PAUSE);

        public void Next() => Press(VirtualKeyCode.MEDIA_NEXT_TRACK);

        public void Previous() => Press(VirtualKeyCode.MEDIA_PREV_TRACK);

        public void Stop() => Press(VirtualKeyCode.MEDIA_STOP);

        /// <summary>
        /// Whether something is actually playing right now.
        /// </summary>
        /// <remarks>
        /// Exists because PauseAsync reports Done both for "I paused it" and for
        /// "it was already paused", and one caller needs to tell those apart: call
        /// screening pauses media for the duration of a call (the caller's audio
        /// arrives by loopback on the speakers, so anything else playing there goes
        /// down the phone line) and must put back exactly what it interrupted.
        /// Resuming playback that Layth had deliberately paused would be worse than
        /// not resuming at all.
        /// </remarks>
        public async Task<bool> IsPlayingAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession session =
                    await NowPlayingReader.TryGetCurrentSessionAsync();
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session?.GetPlaybackInfo();
                return info != null &&
                       info.PlaybackStatus ==
                       GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch (Exception ex)
            {
                // "Don't know" reads as "not playing", which costs a resume rather
                // than causing an unasked-for one.
                Console.WriteLine($"[media] playback state unreadable: {ex.Message}");
                return false;
            }
        }

        /// <summary>Ensures playback is running. Idempotent.</summary>
        public Task<MediaCommandResult> PlayAsync() => SetPlaybackAsync(play: true);

        /// <summary>Ensures playback is paused. Idempotent.</summary>
        public Task<MediaCommandResult> PauseAsync() => SetPlaybackAsync(play: false);

        private async Task<MediaCommandResult> SetPlaybackAsync(bool play)
        {
            string want = play ? "play" : "pause";
            try
            {
                GlobalSystemMediaTransportControlsSession session =
                    await NowPlayingReader.TryGetCurrentSessionAsync();
                if (session == null)
                {
                    // Nothing owns the media session, so there is nothing to play
                    // or pause. Pressing the key here would hand the command to
                    // whatever grabs the session next.
                    Console.WriteLine($"[media] no active media session — {want} ignored.");
                    return MediaCommandResult.NoSession;
                }

                // Whether the playback state was actually READ and found to be the
                // opposite of what was asked for. The toggle fallback below is only
                // safe when this is true.
                //
                // MEDIA_PLAY_PAUSE moves the session whichever way it is currently
                // facing, so firing it without having established that direction is
                // a coin flip — and this class exists precisely because that coin
                // flip turned "unpause my video" into a pause. Two paths used to
                // take it anyway: the refusal path justified itself with "the status
                // check above already established the state needs changing", which
                // holds only when info is non-null, and the catch block ran it
                // having read nothing at all.
                bool knownWrongState = false;

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
                        return MediaCommandResult.Done;
                    }

                    // Only Playing and Paused say which way the toggle would move
                    // it. Stopped, Changing, Closed and Opened do not.
                    knownWrongState = playing || paused;
                }

                bool ok = play
                    ? await session.TryPlayAsync()
                    : await session.TryPauseAsync();

                if (ok)
                {
                    Console.WriteLine($"[media] {want} via SMTC.");
                    return MediaCommandResult.Done;
                }

                // The session exists but refused the explicit control. Some apps
                // implement only the toggle, and here — and only here — the status
                // read above told us which way it is facing.
                if (knownWrongState)
                {
                    Console.WriteLine($"[media] SMTC refused {want}; toggling instead (state is known).");
                    PlayPause();
                    return MediaCommandResult.Done;
                }

                Console.WriteLine(
                    $"[media] SMTC refused {want} and the playback state is unknown — " +
                    "not guessing with the toggle key.");
                return MediaCommandResult.Refused;
            }
            catch (Exception ex)
            {
                // WinRT interop on .NET Framework does throw here. Surface it — but
                // do NOT fall back to the toggle: this path has read nothing, so a
                // toggle is as likely to be wrong as right, and "play" while already
                // playing would PAUSE the thing the user asked to resume. Reporting
                // the failure is worse than doing nothing only if the guess would
                // have been better than a coin flip, and it isn't.
                Console.WriteLine($"[media] {want} failed: {ex.Message}");
                return MediaCommandResult.Refused;
            }
        }

        private void Press(VirtualKeyCode key) => simulator.Keyboard.KeyPress(key);
    }
}
