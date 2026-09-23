using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

Console.Title = "Remote Screen Desktop V2.1";

using var server = new RemoteServer();
server.Start();

Console.WriteLine("==================================================");
Console.WriteLine(" Remote Screen Desktop V2.1");
Console.WriteLine("==================================================");
Console.WriteLine($" PC Address : {server.Address}");
Console.WriteLine($" Session    : {server.SessionCode}");
Console.WriteLine($" Screen     : READY");
Console.WriteLine($" Audio      : {(server.AudioReady ? "READY" : "UNAVAILABLE")}");
Console.WriteLine();
Console.WriteLine(" Keep this window open while using the mobile app.");
Console.WriteLine(" Press ENTER to stop.");
Console.WriteLine("==================================================");

Console.ReadLine();

static class NetworkInfo
{
    public static string GetLanIPv4()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(a.Address))
            .Select(a => a.Address.ToString())
            .ToList();

        return addresses.FirstOrDefault(x => x.StartsWith("192.168.") || x.StartsWith("10.") || x.StartsWith("172."))
               ?? addresses.FirstOrDefault()
               ?? "127.0.0.1";
    }
}