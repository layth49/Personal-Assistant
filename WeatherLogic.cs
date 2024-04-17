using System;
using System.Net;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.CognitiveServices.Speech; // Library for text-to-speech functionality
using Newtonsoft.Json;
using RestSharp;
using Personal_Assistant.LocationLogic;


namespace Personal_Assistant.WeatherLogic
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

            double latitude = await location.GetLatitude();
            double longitude = await location.GetLongitude();
            string city = await location.GetCity();

            try
            {
                // Fetch weather data using OpenWeatherMap API client
                RestClient client = new RestClient("https://api.openweathermap.org/data/2.5/weather");
                RestRequest request = new RestRequest($"?lat={latitude}&lon={longitude}&appid={apiKey}&units=imperial");

                RestResponse json = client.Execute(request);

                if (json.StatusCode == HttpStatusCode.OK)
                {
                    try
                    {                        
                        OpenWeatherMapResponse weatherData = JsonConvert.DeserializeObject<OpenWeatherMapResponse>(json.Content);

                        DateTime sunriseDateTime = new DateTime(1970, 1, 1, 12, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)weatherData.Sys.Sunrise);
                        DateTime sunsetDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds((long)weatherData.Sys.Sunset);

                        Console.WriteLine(weatherData.Weather[0].Description.ToUpperInvariant());
                        await SynthesizeTextToSpeech("en-US-AndrewNeural", weatherData.Weather[0].Description);
                        string response = $"The temperature in {city}, {weatherData.Sys.Country} is currently {(int)weatherData.Main.Temp}°F and feels like {(int)weatherData.Main.Feels_Like}°F. The sun is setting at {sunsetDateTime.ToShortTimeString()} and rising tomorrow at {sunriseDateTime.ToShortTimeString()}\n";
                        
                        Console.WriteLine($"Assistant: {response}");
                        await SynthesizeTextToSpeech("en-US-AndrewNeural", response);
                    }
                    catch (JsonSerializationException ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
                else
                {
                    Console.WriteLine($"Error: {json.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        public static async Task SynthesizeTextToSpeech(string voiceName, string textToSynthesize)
        {
            string speechKey = Environment.GetEnvironmentVariable("SPEECH_KEY");
            string speechRegion = Environment.GetEnvironmentVariable("SPEECH_REGION");

            // Creates an instance of a speech config with specified subscription key and service region. Replace this with the same style as other comments
            SpeechConfig config = SpeechConfig.FromSubscription(speechKey, speechRegion);

            // I liked this voice but you can look for others on https://bit.ly/3ttEGuH
            config.SpeechSynthesisVoiceName = voiceName;

            // Use the default speaker as audio output
            using (SpeechSynthesizer synthesizer = new SpeechSynthesizer(config))
            {
                using (SpeechSynthesisResult result = await synthesizer.SpeakTextAsync(textToSynthesize))
                {
                    if (result.Reason == ResultReason.Canceled)
                    {
                        SpeechSynthesisCancellationDetails cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
                        Console.WriteLine($"CANCELED: Reason={cancellation.Reason}");

                        if (cancellation.Reason == CancellationReason.Error)
                        {
                            Console.WriteLine($"CANCELED: ErrorCode={cancellation.ErrorCode}");
                            Console.WriteLine($"CANCELED: ErrorDetails=[{cancellation.ErrorDetails}]");
                            Console.WriteLine($"CANCELED: Did you update the subscription info?");
                        }
                    }
                }
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
