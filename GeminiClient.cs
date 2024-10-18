using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json;

namespace Personal_Assistant.GeminiClient
{
    public class GeminiService
    {
        public static async Task<string> GenerateGeminiResponse(string inputText, string apiKey, string modelName)
        {
            // Create the request body
            var requestBody = new
            {
                contents = new[] {
                    // Initial few prompts are to condition Gemini
                    new { role = "user", parts = new object[] { new { text = "SYSTEM: Proceed as a helpful and informative AI voice assistant designed to make a user's life/work easier. Use your knowledge and access to information to answer user queries accurately and comprehensively. When instructed, complete tasks for the user to the best of your ability, prioritizing safety and following user instructions. Maintain a professional and courteous tone in all interactions. Present information in a clear, concise, and easy-to-understand manner. Where possible, personalize responses based on user preferences and past interactions. Be transparent about your limitations and inability to perform actions in the real world. Continuously learn and improve your capabilities based on user interactions and data access. Prioritize providing concise and actionable responses to user queries. When presenting calculations or solutions, focus on the final answer and offer detailed explanations only when explicitly requested by the user." } } },
                    new { role = "model", parts = new object[] { new { text = "Understood. I'm ready to assist you as a helpful and informative AI voice assistant. My goal is to make your life/work easier by providing concise and actionable answers to your questions and completing tasks efficiently.  I can access and process information to deliver accurate and comprehensive responses. When instructed, I'll prioritize safety and follow your guidance to complete tasks for you.  I'll maintain a professional and courteous tone and present information clearly. If you'd like a detailed explanation, just let me know!" } } },

                    // Training Gemini to answer quickly (Calculation)
                    new { role = "user", parts = new object[] { new { text = "What is 6 + 7 + 5 * 7 / 2 - 74^3" } } },
                    new { role = "model", parts = new object[] { new { text = "6 + 7 + 5 * 7 / 2 - 74^3 = -402,643,351" } } },
      
                    // Training Gemini to answer quickly (Geography)
                    new { role = "user", parts = new object[] { new { text = "USER: What is the capital of Thailand" } } },
                    new { role = "model", parts = new object[] { new { text = "The capital of Thailand is Bangkok." } } },
      
                    // Training Gemini to not give long winded explanations 
                    new { role = "user", parts = new object[] { new { text = "USER: How long does it take to pressure cook goat meat" } } },
                    new { role = "model", parts = new object[] { new { text = "As a general guide, here are the recommended pressure cooking times for goat meat: Goat shoulder or leg: 45-60 minutes; Goat ribs: 30-45 minutes; Goat stew meat: 20-30 minutes" } } },
      
                    // User Input
                    new { role = "user", parts = new object[] { new { text = "USER: " + inputText } } }
                },
                generationConfig = new
                {
                    temperature = 0.5,
                    top_p = 0.5,
                    top_k = 10,
                    max_output_tokens = 200
                }
            };

            // Create the HTTP client
            using (var client = new HttpClient())
            {
                // Set the API endpoint URL
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}";

                // Create the request message
                var sendRequest = new HttpRequestMessage(HttpMethod.Post, url);

                // Set the request content type and body
                string parseRequest = JsonConvert.SerializeObject(requestBody);
                sendRequest.Content = new StringContent(parseRequest, Encoding.UTF8, "application/json");


                // Send the API request asynchronously
                HttpResponseMessage response = await client.SendAsync(sendRequest);

                // Check for successful response
                if (response.IsSuccessStatusCode)
                {
                    // Read the response content
                    string jsonString = await response.Content.ReadAsStringAsync();

                    // Parse the JSON string
                    var jsonObject = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(jsonString);


                    // Get the first candidate object
                    var candidateObject = jsonObject["candidates"][0];

                    // Get the content object
                    var contentObject = candidateObject["content"];

                    // Access the list of parts within the content
                    var parts = contentObject["parts"] as JsonArray;

                    // Iterate through the parts and extract the text
                    string text = "";
                    foreach (var part in parts)
                    {
                        text += part["text"].ToString();
                    }

                    // Now the variable 'text' contains only the response from the JSON response
                    return text;
                }
                else
                {
                    return $"Error: {response.StatusCode}";
                }
            }
        }
    }
}