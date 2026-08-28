using System;
using System.Diagnostics;
using Personal_Assistant.Dispatch;

namespace Personal_Assistant.LightAutomator
{
    public class LightControl
    {
        public ToolResult TurnOnLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: true);

        public ToolResult TurnOffLights(string lightName, string ipAddress) => Toggle(lightName, ipAddress, on: false);

        // Reports what it did instead of announcing it. The SpeechService this
        // used to reach for was the app's shared one, so nothing was broken — but
        // a handler that speaks its own answer leaves a model consuming the tool
        // result with nothing to speak from, which is the whole point of the
        // ToolResult channel.
        private ToolResult Toggle(string lightName, string ipAddress, bool on)
        {
            string verb = on ? "on" : "off";

            try
            {
                // `kasa` directly rather than through cmd.exe: the wrapper bought
                // nothing, hid the exit code, and made a missing kasa look like a
                // silent success.
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
