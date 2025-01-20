using System.Net.Sockets;
using System;
using System.Text;

namespace Personal_Assistant.Arduino
{
    public class ArduinoService
    {
        public void ArduinoCommunication(string input)
        {
            string ipAddress = Environment.GetEnvironmentVariable("IP_ADDRESS:ARDUINO");
            Console.WriteLine($"Arduino IP Address: {ipAddress}");
            int port = 80;

            if (string.IsNullOrEmpty(ipAddress))
            {
                throw new ArgumentNullException("hostname", "IP address for Arduino is not set.");
            }

            using (TcpClient client = new TcpClient())
            {
                client.Connect(ipAddress, port);
                NetworkStream stream = client.GetStream();
                byte[] data = Encoding.ASCII.GetBytes(input + "\n");
                stream.Write(data, 0, data.Length);
            }
        }

    }
}