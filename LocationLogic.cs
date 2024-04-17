using Newtonsoft.Json;
using System;
using System.Device.Location;
using System.Net.Http;
using System.Threading.Tasks;

namespace Personal_Assistant.LocationLogic
{
    public class GetLocation
    {
        public async Task<double> GetLatitude()
        {
            try
            {
                // Implement logic to retrieve latitude using GeoCoordinateWatcher
                GeoCoordinateWatcher watcher = new GeoCoordinateWatcher();
                watcher.Start();

                // Wait for a valid position fix
                while (watcher.Position.Location.IsUnknown)
                {
                    System.Threading.Thread.Sleep(100); // Short delay for position acquisition
                }
                return watcher.Position.Location.Latitude;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while retrieving latitude: {ex.Message}");
                return 0.0; // Return a placeholder value if an error occurs
            }
        }

        public async Task<double> GetLongitude()
        {
            try
            {
                // Implement logic to retrieve latitude using GeoCoordinateWatcher
                GeoCoordinateWatcher watcher = new GeoCoordinateWatcher();
                watcher.Start();

                // Wait for a valid position fix
                while (watcher.Position.Location.IsUnknown)
                {
                    System.Threading.Thread.Sleep(100); // Short delay for position acquisition
                }

                return watcher.Position.Location.Longitude;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while retrieving longitude: {ex.Message}");
                return 0.0; // Return a placeholder value if an error occurs
            }
        }

        public async Task<string> GetCity()
        {
            try
            {
                double latitude = await GetLatitude();
                double longitude = await GetLongitude();

                string url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={latitude}&lon={longitude}";

                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "MyPersonalAssistantApp/1.0");
                    using (var response = await httpClient.GetAsync(url))
                    {
                        if (response.IsSuccessStatusCode)
                        {

                            string jsonResponse = await response.Content.ReadAsStringAsync();
                            return ParseCityFromResponse(jsonResponse); // Parse city from JSON response
                        }
                        else
                        {
                            Console.WriteLine($"Error retrieving city information: {response.StatusCode}");
                            return ""; // Return empty string on error
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while retrieving city: {ex.Message}");
                return ""; // Return empty string on error
            }
        }

        public string ParseCityFromResponse(string jsonResponse)
        {
            try
            {
                // Parse the JSON response to extract city information (OpenStreetMap Nominatim format)
                dynamic jsonObject = JsonConvert.DeserializeObject(jsonResponse);

                // Assuming address is an object containing components
                if (jsonObject.address != null) // Check if address object exists
                {
                    var village = jsonObject.address.village; // Access village property
                    var city = jsonObject.address.city; // Access city property
                    var town = jsonObject.address.town; // Access town property
                    if (village != null) // Check if village property exists
                    {
                        return village; // Return village name
                    }
                    else if (city != null)
                    {
                        return city;
                    }
                    else if (town != null)
                    {
                        return town;
                    }
                    else
                    {
                        Console.WriteLine("Village/City/Town information not found in the response.");
                    }
                }
                else
                {
                    Console.WriteLine("Address object not found in the response.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing city information from response: {ex.Message}");
            }

            return ""; // Return empty string if city cannot be parsed
        }
    }
}