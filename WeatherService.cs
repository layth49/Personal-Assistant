using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        // Multi-day forecast using OpenWeatherMap's free 5-day / 3-hour endpoint
        // (/data/2.5/forecast — same API key tier as current weather). The
        // 3-hourly entries are grouped into local days; each day is summarised
        // by its high/low and the condition around midday.
        public async Task GetForecastData(int days = 3)
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

                string url = $"https://api.openweathermap.org/data/2.5/forecast?lat={latitude}&lon={longitude}&appid={apiKey}&units=imperial";

                using (var response = await httpClient.GetAsync(url))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Error: {response.StatusCode}");
                        await speechManager.Say(Program.recognizedText,
                            "Sorry, I couldn't get the forecast right now.");
                        return;
                    }

                    string json = await response.Content.ReadAsStringAsync();
                    var data = JsonSerializer.Deserialize<ForecastResponse>(json);

                    string text = BuildForecastSummary(data, city, days);
                    await speechManager.Say(Program.recognizedText, text);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
                await speechManager.Say(Program.recognizedText,
                    "Sorry, I ran into a problem getting the forecast.");
            }
        }

        // Groups the 3-hourly entries into local days and produces a short spoken
        // summary of the next `days` days (clamped to what the API returned).
        private static string BuildForecastSummary(ForecastResponse data, string city, int days)
        {
            if (data?.List == null || data.List.Length == 0)
            {
                return "I couldn't read any forecast data.";
            }

            var today = DateTime.Now.Date;

            var byDay = data.List
                .Select(e => new
                {
                    Local = DateTimeOffset.FromUnixTimeSeconds((long)e.Dt).ToLocalTime(),
                    Entry = e
                })
                .GroupBy(x => x.Local.Date)
                // Skip today — "forecast" means the days ahead; current weather
                // is handled by GetWeatherData.
                .Where(g => g.Key > today)
                .OrderBy(g => g.Key)
                .Take(Math.Max(1, days))
                .ToList();

            if (byDay.Count == 0)
            {
                return "I couldn't build a forecast from the available data.";
            }

            var sb = new StringBuilder();
            sb.Append($"Here's the forecast for {city}. ");

            foreach (var day in byDay)
            {
                double high = day.Max(x => x.Entry.Main.TempMax);
                double low = day.Min(x => x.Entry.Main.TempMin);

                // Representative condition: the entry closest to 1 PM local.
                var midday = day
                    .OrderBy(x => Math.Abs((x.Local.TimeOfDay - TimeSpan.FromHours(13)).TotalMinutes))
                    .First();
                string condition = midday.Entry.Weather != null && midday.Entry.Weather.Length > 0
                    ? midday.Entry.Weather[0].Main
                    : "unclear conditions";

                string label = DayLabel(day.Key, today);
                sb.Append($"{label}, {condition.ToLower()} with a high of {(int)Math.Round(high)} " +
                          $"and a low of {(int)Math.Round(low)}. ");
            }

            return sb.ToString().TrimEnd();
        }

        private static string DayLabel(DateTime date, DateTime today)
        {
            if (date == today.AddDays(1)) return "Tomorrow";
            return date.DayOfWeek.ToString();
        }

        public class ForecastResponse
        {
            [JsonPropertyName("list")] public ForecastEntry[] List { get; set; }
            [JsonPropertyName("city")] public City City { get; set; }
        }

        public class ForecastEntry
        {
            [JsonPropertyName("dt")] public double Dt { get; set; }
            [JsonPropertyName("main")] public Main Main { get; set; }
            [JsonPropertyName("weather")] public Weather[] Weather { get; set; }
        }

        public class City
        {
            [JsonPropertyName("name")] public string Name { get; set; }
            [JsonPropertyName("country")] public string Country { get; set; }
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
            [JsonPropertyName("temp_min")] public double TempMin { get; set; }
            [JsonPropertyName("temp_max")] public double TempMax { get; set; }
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
