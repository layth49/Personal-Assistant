using System;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;        // MQTTnet v5: delete this line (types moved to MQTTnet)
using MQTTnet.Protocol;

namespace Personal_Assistant.Arduino
{
    // Door control now goes through the MQTT broker instead of a raw socket.
    // The Arduino is subscribed to "door_opener/door/set" and acts on the
    // OPEN / CLOSE / STOP payloads. Because the firmware publishes its own
    // state on every action, Home Assistant stays in sync with whatever
    // LAITH sends — the broker is the single source of truth.
    public class ArduinoService
    {
        private static readonly string Host = Environment.GetEnvironmentVariable("MQTT:HOST");
        private static readonly string User = Environment.GetEnvironmentVariable("MQTT:USER");
        private static readonly string Pass = Environment.GetEnvironmentVariable("MQTT:PASS");
        private const int Port = 1883;
        private const string CommandTopic = "door_opener/door/set";

        // Drop-in replacement for the old method. Pass "OPEN", "CLOSE", or "STOP".
        public async Task ArduinoCommunication(string input)
        {
            if (string.IsNullOrEmpty(Host))
                throw new InvalidOperationException("MQTT broker host is not set (MQTT:HOST).");

            var factory = new MqttFactory();              // MQTTnet v5: new MqttClientFactory()
            using var client = factory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(Host, Port)
                .WithCredentials(User, Pass)
                .WithClientId("laith-assistant")
                .Build();

            await client.ConnectAsync(options);

            var message = new MqttApplicationMessageBuilder()
                .WithTopic(CommandTopic)
                .WithPayload(input)
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .WithRetainFlag(false)                    // never retain a command
                .Build();

            await client.PublishAsync(message);
            await client.DisconnectAsync();
        }
    }
}