﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RestSharp;
using OpenWeatherAPI;
using Microsoft.CognitiveServices.Speech;
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


            var latitude = await location.GetLatitude();
            var longitude = await location.GetLongitude();
            var city = await location.GetCity();


            try
            {
                // Fetch weather data using OpenWeatherMap API client
                OpenWeatherApiClient openWeatherAPI = new OpenWeatherApiClient(apiKey);
                QueryResponse query = await openWeatherAPI.QueryAsync(city);

                // Additional request for sunrise/sunset data
                RestClient client = new RestClient("https://api.openweathermap.org/data/2.5/weather");
                RestRequest request = new RestRequest($"?lat={latitude}&lon={longitude}&appid={apiKey}&units=imperial");

                RestResponse json = client.Execute(request);

                if (json.StatusCode == HttpStatusCode.OK)
                {
                    OpenWeatherMapResponse weatherData = JsonConvert.DeserializeObject<OpenWeatherMapResponse>(json.Content);

                    // Process and announce weather information
                    Console.WriteLine(weatherData.weather[0].description.ToUpper()); // Use weather.description from OpenWeatherMapResponse
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", weatherData.weather[0].description.ToUpper());
                    string response = $"The temperature in {query.Name}, {query.Sys.Country} is currently {(int)query.Main.Temperature.FahrenheitCurrent}°F and feels like {(int)weatherData.main.feels_like}°F. The sun is setting at {query.Sys.Sunset.ToShortTimeString()} and rising tomorrow at {query.Sys.Sunrise.ToShortTimeString()}\n";
                    Console.WriteLine($"Assistant: {response}");
                    await SynthesizeTextToSpeech("en-US-AndrewNeural", response);
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
                    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
                    {
                        // This was for testing but I decided to still keep it as commented anyway in case anyone else wanted it
                        // Console.WriteLine($"Speech synthesized for text [{textToSynthesize}]");
                    }
                    else if (result.Reason == ResultReason.Canceled)
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
            public Main2 main { get; set; }
            public List<Weather> weather { get; set; }
        }

        public class Main2
        {
            public double feels_like { get; set; }
        }

        public class Weather
        {
            public string description { get; set; }
        }

    }
}