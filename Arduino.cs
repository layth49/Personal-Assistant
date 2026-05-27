using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace Personal_Assistant.Arduino
{
    public class ArduinoService
    {
        public async Task ArduinoCommunication(string input)
        {
            string ipAddress = Environment.GetEnvironmentVariable("IP_ADDRESS:ARDUINO");
            int port = 80;

            if (string.IsNullOrEmpty(ipAddress))
            {
                throw new ArgumentNullException("hostname", "IP address for Arduino is not set.");
            }

            using (TcpClient client = new TcpClient())
            {
                await client.ConnectAsync(ipAddress, port);
                using (NetworkStream stream = client.GetStream())
                {
                    byte[] data = Encoding.ASCII.GetBytes(input + "\n");
                    await stream.WriteAsync(data, 0, data.Length);
                }
            }
        }
    }
}
