using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Personal_Assistant.Geolocator;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.WeatherService
{
    public class GetWeather
    {
        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private readonly string apiKey;

        public GetWeather(string apiKey)
        {
            this.apiKey = apiKey;
        }

        public async Task GetWeatherData()
        {
            var location = new GetLocation();
            var speechManager = new SpeechService();

            try
            {
                var latTask = location.GetLatitude();
                var lonTask = location.GetLongitude();
                var cityTask = location.GetCity();
                await Task.WhenAll(latTask, lonTask, cityTask);

                double latitude = latTask.Result;
                double longitude = lonTask.Result;
                string city = cityTask.Result;

                string url = $"https://api.openweathermap.org/data/2.5/weather?lat={latitude}&lon={longitude}&appid={apiKey}&units=imperial";

                using (var response = await httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Error: {response.StatusCode}");
                        return;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<OpenWeatherMapResponse>(json);

                    DateTime sunrise = DateTimeOffset.FromUnixTimeSeconds((long)data.Sys.Sunrise).ToLocalTime().DateTime;
                    DateTime sunset = DateTimeOffset.FromUnixTimeSeconds((long)data.Sys.Sunset).ToLocalTime().DateTime;

                    string text =
                        $"{data.Weather[0].Main}. The temperature in {city}, {data.Sys.Country} is currently " +
                        $"{(int)data.Main.Temp}°F and feels like {(int)data.Main.FeelsLike}°F. " +
                        $"The sun is setting at {sunset.ToShortTimeString()} and rising tomorrow at {sunrise.ToShortTimeString()}";

                    await speechManager.Say(Program.recognizedText, text);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        public class OpenWeatherMapResponse
        {
            [JsonPropertyName("weather")] public Weather[] Weather { get; set; }
            [JsonPropertyName("main")] public Main Main { get; set; }
            [JsonPropertyName("sys")] public Sys Sys { get; set; }
        }

        public class Main
        {
            [JsonPropertyName("temp")] public double Temp { get; set; }
            [JsonPropertyName("feels_like")] public double FeelsLike { get; set; }
            [JsonPropertyName("humidity")] public int Humidity { get; set; }
        }

        public class Weather
        {
            [JsonPropertyName("main")] public string Main { get; set; }
            [JsonPropertyName("description")] public string Description { get; set; }
        }

        public class Sys
        {
            [JsonPropertyName("country")] public string Country { get; set; }
            [JsonPropertyName("sunrise")] public double Sunrise { get; set; }
            [JsonPropertyName("sunset")] public double Sunset { get; set; }
        }
    }
}
