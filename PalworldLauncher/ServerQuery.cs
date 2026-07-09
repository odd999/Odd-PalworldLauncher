using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace PalworldLauncher
{
    public static class ServerQuery
    {
        public static async Task<bool> CheckServerStatusAsync(string ip, int port, int authPort = 8000, int timeoutMs = 2000)
        {
            if (string.IsNullOrEmpty(ip)) return false;

            IPAddress address = null;
            try
            {
                // Resolve IP
                if (!IPAddress.TryParse(ip, out address))
                {
                    var addresses = await Dns.GetHostAddressesAsync(ip);
                    if (addresses.Length == 0) return false;
                    address = addresses[0];
                }

                // Palworld server listens on UDP. We send a standard Source Engine query packet (A2S_INFO).
                // Bytes: FF FF FF FF 54 53 6F 75 72 63 65 20 45 6E 67 69 6E 65 20 51 75 65 72 79 00
                // Hex representation: \xFF\xFF\xFF\xFFTSource Engine Query\x00
                byte[] request = new byte[] {
                    0xFF, 0xFF, 0xFF, 0xFF, // Header
                    0x54,                   // A2S_INFO type
                    0x53, 0x6F, 0x75, 0x72, 0x63, 0x65, 0x20, 0x45, 0x6E, 0x67, 0x69, 0x6E, 0x65, 0x20, 0x51, 0x75, 0x65, 0x72, 0x79, 0x00 // "Source Engine Query\0"
                };

                using (var udpClient = new UdpClient())
                {
                    udpClient.Client.SendTimeout = timeoutMs;
                    udpClient.Client.ReceiveTimeout = timeoutMs;
                    
                    var endPoint = new IPEndPoint(address, port);
                    udpClient.Connect(endPoint);

                    await udpClient.SendAsync(request, request.Length);

                    // Wait for response with timeout
                    var receiveTask = udpClient.ReceiveAsync();
                    var delayTask = Task.Delay(timeoutMs);

                    var completedTask = await Task.WhenAny(receiveTask, delayTask);
                    if (completedTask == receiveTask)
                    {
                        var result = receiveTask.Result;
                        if (result.Buffer != null && result.Buffer.Length > 0)
                        {
                            return true; // Received a response, server is online!
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Server query failed: {ex.Message}");
            }

            // Fallback 1: Try TCP connection to the Auth API Port (default 8000)
            if (authPort > 0)
            {
                try
                {
                    using (var tcpClient = new TcpClient())
                    {
                        var connectTask = tcpClient.ConnectAsync(ip, authPort);
                        var delayTask = Task.Delay(timeoutMs);
                        var completedTask = await Task.WhenAny(connectTask, delayTask);
                        if (completedTask == connectTask)
                        {
                            return true; // TCP connection succeeded!
                        }
                    }
                }
                catch { }
            }

            // Smart Fallback 2: If A2S UDP query and TCP checks failed, check via ICMP Ping
            // but ONLY if the address is a remote IP (to prevent local loopback false positives).
            if (address != null && !IPAddress.IsLoopback(address))
            {
                try
                {
                    using (var ping = new System.Net.NetworkInformation.Ping())
                    {
                        var reply = await ping.SendPingAsync(address, timeoutMs);
                        if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                        {
                            return true; // Remote server machine is online
                        }
                    }
                }
                catch { }
            }

            return false;
        }
    }
}
