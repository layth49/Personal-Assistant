using System;
using System.Device.Location;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Personal_Assistant.Geolocator
{
    public class GetLocation
    {
        private static readonly HttpClient httpClient = CreateHttpClient();
        private static GeoCoordinate cachedCoordinate;
        private static readonly object coordinateLock = new object();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MyPersonalAssistantApp", "1.0"));
            return client;
        }

        public async Task<double> GetLatitude() => (await GetCoordinateAsync()).Latitude;

        public async Task<double> GetLongitude() => (await GetCoordinateAsync()).Longitude;

        private Task<GeoCoordinate> GetCoordinateAsync()
        {
            lock (coordinateLock)
            {
                if (cachedCoordinate != null && !cachedCoordinate.IsUnknown)
                {
                    return Task.FromResult(cachedCoordinate);
                }
            }

            var tcs = new TaskCompletionSource<GeoCoordinate>();
            var watcher = new GeoCoordinateWatcher(GeoPositionAccuracy.Default);

            EventHandler<GeoPositionChangedEventArgs<GeoCoordinate>> onChanged = null;
            EventHandler<GeoPositionStatusChangedEventArgs> onStatus = null;
            Timer timeoutTimer = null;

            void Cleanup()
            {
                watcher.PositionChanged -= onChanged;
                watcher.StatusChanged -= onStatus;
                timeoutTimer?.Dispose();
                try { watcher.Stop(); } catch { }
                watcher.Dispose();
            }

            onChanged = (s, e) =>
            {
                if (e.Position.Location.IsUnknown) return;
                lock (coordinateLock) { cachedCoordinate = e.Position.Location; }
                Cleanup();
                tcs.TrySetResult(e.Position.Location);
            };

            onStatus = (s, e) =>
            {
                // Disabled is the only terminal failure — it means no location
                // providers are available at all. NoData / Initializing are
                // expected transient states during startup; we just wait through
                // them for PositionChanged to fire.
                if (e.Status == GeoPositionStatus.Disabled)
                {
                    Cleanup();
                    tcs.TrySetException(new InvalidOperationException(
                        "Location service is disabled. Enable Windows Location Services in Settings."));
                }
            };

            watcher.PositionChanged += onChanged;
            watcher.StatusChanged += onStatus;
            watcher.Start();

            // Cap the wait so the assistant doesn't hang forever if GPS / network
            // location never produces a fix (e.g., no internet on a desktop).
            timeoutTimer = new Timer(_ =>
            {
                Cleanup();
                tcs.TrySetException(new InvalidOperationException(
                    "Timed out waiting for a location fix (15s)."));
            }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);

            return tcs.Task;
        }

        public async Task<string> GetCity()
        {
            try
            {
                var coord = await GetCoordinateAsync();
                string url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={coord.Latitude}&lon={coord.Longitude}";

                using (var response = await httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Error retrieving city information: {response.StatusCode}");
                        return string.Empty;
                    }
                    string json = await response.Content.ReadAsStringAsync();
                    return ParseCityFromResponse(json);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while retrieving city: {ex.Message}");
                return string.Empty;
            }
        }

        public string ParseCityFromResponse(string jsonResponse)
        {
            try
            {
                using (var doc = JsonDocument.Parse(jsonResponse))
                {
                    if (!doc.RootElement.TryGetProperty("address", out var address))
                    {
                        Console.WriteLine("Address object not found in the response.");
                        return string.Empty;
                    }

                    foreach (var key in new[] { "village", "city", "town" })
                    {
                        if (address.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            return value.GetString();
                        }
                    }
                    Console.WriteLine("Village/City/Town information not found in the response.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing city information from response: {ex.Message}");
            }
            return string.Empty;
        }
    }
}
