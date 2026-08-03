using System;
using System.Diagnostics;
using Personal_Assistant.Dispatch;

namespace Personal_Assistant.LightAutomator
{
    public class LightControl
    {
        public ToolResult TurnOnLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: true);
        // The one running instance — never `new SpeechService()`. A second one
        // owns none of the audio state the rest of the app reads, so everything
        // it says escapes the echo gate and everything it hears times out.
        // A property, not a field, so there is no construction-order trap.
        private static SpeechService speechManager => SpeechService.Current;

        public ToolResult TurnOffLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: false);

        // Reports what it did instead of announcing it. The SpeechService this
        // used to own was a second instance — silently useless in both directions
        // and the class of bug that once sent a real empty SMS.
        private ToolResult Toggle(string lightName, string ipAddress, bool on)
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
                return ToolResult.Failed(
                    $"Sorry, I couldn't reach your {lightName} lights.", ex.Message);
            }

            return ToolResult
                .Speak($"Okay! Turning your {lightName} lights {verb} now.")
                .With("light", lightName)
                .With("state", verb);
        }
    }
}
