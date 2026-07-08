using WindowsInput;
using WindowsInput.Native;

namespace Personal_Assistant.MediaControl
{
    // Play/pause and track navigation for whatever app currently owns the
    // system media session (Spotify, a browser tab, a video player, ...).
    //
    // Uses the standard multimedia virtual keys. Windows routes these to the
    // active media session — the same session the System Media Transport
    // Controls expose — so this controls "whatever is playing" without needing
    // to know or target a specific app.
    public class MediaController
    {
        private readonly InputSimulator simulator = new InputSimulator();

        public void PlayPause() => Press(VirtualKeyCode.MEDIA_PLAY_PAUSE);

        public void Next() => Press(VirtualKeyCode.MEDIA_NEXT_TRACK);

        public void Previous() => Press(VirtualKeyCode.MEDIA_PREV_TRACK);

        public void Stop() => Press(VirtualKeyCode.MEDIA_STOP);

        private void Press(VirtualKeyCode key) => simulator.Keyboard.KeyPress(key);
    }
}
