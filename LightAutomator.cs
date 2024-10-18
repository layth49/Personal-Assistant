using System;
using System.Diagnostics;
using Personal_Assistant.SpeechManager;

namespace Personal_Assistant.LightAutomator
{
    public class LightControl
    {
        SpeechService speechManager = new SpeechService();

        public void TurnOnLights(string lightName, string ipAddress)
        {
            Process cmd = new Process();
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.FileName = "cmd.exe";
            cmd.StartInfo.Arguments = $"/c kasa --host {ipAddress} on";
            cmd.Start();

            Console.WriteLine($"Assistant: Ok! Turning your {lightName} lights on now.\n");
            speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Turning your {lightName} lights on now.");
        }

        public void TurnOffLights(string lightName, string ipAddress)
        {
            Process cmd = new Process();
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.FileName = "cmd.exe";
            cmd.StartInfo.Arguments = $"/c kasa --host {ipAddress} off";
            cmd.Start();

            Console.WriteLine($"Assistant: Ok! Turning your {lightName} lights off now.\n");
            speechManager.SynthesizeTextToSpeech("en-US-AndrewNeural", $"Okay! Turning your {lightName} lights off now.");
        }
    }
}