using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Personal_Assistant.Arduino
{
    public class ArduinoService
    {
        private static readonly string ipAddress = Environment.GetEnvironmentVariable("IP_ADDRESS:ARDUINO");
        private const int Port = 80;

        public async Task ArduinoCommunication(string input)
        {
            if (string.IsNullOrEmpty(ipAddress))
            {
                throw new InvalidOperationException("IP address for Arduino is not set (IP_ADDRESS:ARDUINO).");
            }

            using (var client = new TcpClient())
            {
                await client.ConnectAsync(ipAddress, Port);
                using (var stream = client.GetStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes(input + "\n");
                    await stream.WriteAsync(data, 0, data.Length);
                }
            }
        }
    }
}
