using System;
using System.Threading.Tasks;
using Windows.Media.Control;

namespace Personal_Assistant.MediaControl
{
    public sealed class NowPlaying
    {
        public string Title { get; set; }
        public string Artist { get; set; }

        // Spoken form: "Title by Artist", gracefully dropping either half if the
        // source app didn't report it.
        public string Spoken()
        {
            bool hasTitle = !string.IsNullOrWhiteSpace(Title);
            bool hasArtist = !string.IsNullOrWhiteSpace(Artist);
            if (hasTitle && hasArtist) return $"{Title} by {Artist}";
            if (hasTitle) return Title;
            if (hasArtist) return Artist;
            return null;
        }
    }

    // Reads the currently playing track from the Windows System Media Transport
    // Controls (SMTC) — the same session Spotify, browsers, and video players
    // report to. WinRT interop on .NET Framework, so failures are swallowed and
    // surfaced as null (no current session / metadata unavailable).
    public class NowPlayingReader
    {
        public async Task<NowPlaying> GetCurrentAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionManager manager =
                    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

                GlobalSystemMediaTransportControlsSession session = manager?.GetCurrentSession();
                if (session == null) return null;

                GlobalSystemMediaTransportControlsSessionMediaProperties props =
                    await session.TryGetMediaPropertiesAsync();
                if (props == null) return null;

                return new NowPlaying { Title = props.Title, Artist = props.Artist };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[media] now-playing read failed: {ex.Message}");
                return null;
            }
        }
    }
}
