using System;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using Personal_Assistant.Geolocator;
using Personal_Assistant.SpeechManager;
using Newtonsoft.Json;
using RestSharp;


namespace Personal_Assistant.WeatherService
{
    public class GetWeather
    {
        private string apiKey;

        public GetWeather(string apiKey)
        {
            this.apiKey = apiKey;
        }

        public async Task GetWeatherData()
        {
            GetLocation location = new GetLocation();

            double latitude = location.GetLatitude().GetAwaiter().GetResult();
            double longitude = location.GetLongitude().GetAwaiter().GetResult();
            string city = location.GetCity().GetAwaiter().GetResult();

            int year = DateTime.Now.Year;
            int month = DateTime.Now.Month;
            int day = DateTime.Now.Day;

            SpeechService speechManager = new SpeechService();

            try
            {
                // Fetch weather data using OpenWeatherMap API client
                RestClient client = new RestClient("https://api.openweathermap.org/data/2.5/weather");
                RestRequest request = new RestRequest($"?lat={latitude}&lon={longitude}&appid={apiKey}&units=imperial");

                RestResponse response = client.Execute(request);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    try
                    {
                        OpenWeatherMapResponse weatherData = JsonConvert.DeserializeObject<OpenWeatherMapResponse>(response.Content);

                        DateTime sunriseDateTime = DateTimeOffset.FromUnixTimeSeconds((long)weatherData.Sys.Sunrise).ToLocalTime().DateTime;
                        DateTime sunsetDateTime = DateTimeOffset.FromUnixTimeSeconds((long)weatherData.Sys.Sunset).ToLocalTime().DateTime;

                        string weatherResponse = $"{weatherData.Weather[0].Main}. The temperature in {city}, {weatherData.Sys.Country} is currently {(int)weatherData.Main.Temp}°F and feels like {(int)weatherData.Main.Feels_Like}°F. The sun is setting at {sunsetDateTime.ToShortTimeString()} and rising tomorrow at {sunriseDateTime.ToShortTimeString()}";

                        speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", weatherResponse);
                        speechManager.SpeechBubble(Program.recognizedText, weatherResponse);
                    }
                    catch (JsonSerializationException ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                else
                {
                    Console.WriteLine($"Error: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        public class OpenWeatherMapResponse
        {
            public List<Weather> Weather { get; set; }
            public Main Main { get; set; }
            public Sys Sys { get; set; }
        }

        public class Main
        {
            public double Temp { get; set; }
            public double Feels_Like { get; set; }
            public int Humidity { get; set; }
        }

        public class Weather
        {
            public string Main { get; set; }
            public string Description { get; set; }
        }

        public class Sys
        {
            public string Country { get; set; }
            public double Sunrise { get; set; }
            public double Sunset { get; set; }
        }
    }
}