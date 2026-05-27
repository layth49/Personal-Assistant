using System.Diagnostics;
using System.Threading.Tasks;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.LightAutomator
{
    public class LightControl
    {
        private readonly SpeechService speechManager = new SpeechService();

        public async Task TurnOnLights(string lightName, string ipAddress)
        {
            RunKasa(ipAddress, "on");
            await speechManager.Say(Program.recognizedText, $"Okay! Turning your {lightName} lights on now.");
        }

        public async Task TurnOffLights(string lightName, string ipAddress)
        {
            RunKasa(ipAddress, "off");
            await speechManager.Say(Program.recognizedText, $"Okay! Turning your {lightName} lights off now.");
        }

        private static void RunKasa(string ipAddress, string action)
        {
            var cmd = new Process();
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.FileName = "cmd.exe";
            cmd.StartInfo.Arguments = $"/c kasa --host {ipAddress} {action}";
            cmd.Start();
        }
    }
}
