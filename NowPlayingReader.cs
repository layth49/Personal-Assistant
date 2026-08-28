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
        // The one place that decides what "the current media session" means.
        // MediaController needs the same session to issue play/pause, and two
        // answers to that question is one too many — picking the wrong session
        // sends a command to the wrong app.
        internal static async Task<GlobalSystemMediaTransportControlsSession> TryGetCurrentSessionAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionManager manager =
                    await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                return manager?.GetCurrentSession();
            }
            catch (Exception ex)
            {
                // WinRT interop on .NET Framework: surface, don't throw.
                Console.WriteLine($"[media] could not reach the media session: {ex.Message}");
                return null;
            }
        }

        public async Task<NowPlaying> GetCurrentAsync()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession session =
                    await TryGetCurrentSessionAsync();
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