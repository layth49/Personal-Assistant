using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.LightAutomator
{
    public class LightControl
    {
        private readonly SpeechService speechManager = new SpeechService();

        public Task TurnOnLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: true);

        public Task TurnOffLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: false);

        private async Task Toggle(string lightName, string ipAddress, bool on)
        {
            string verb = on ? "on" : "off";

            try
            {
                var psi = new ProcessStartInfo("kasa", $"--host {ipAddress} {verb}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error toggling {lightName} lights: {ex.Message}");
                return;
            }

            await speechManager.Say(Program.recognizedText, $"Okay! Turning your {lightName} lights {verb} now.");
        }
    }
}
