using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

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

        return addresses.FirstOrDefault(x =>
                   x.StartsWith("192.168.") ||
                   x.StartsWith("10.") ||
                   x.StartsWith("172."))
               ?? addresses.FirstOrDefault()
               ?? "127.0.0.1";
    }
}
