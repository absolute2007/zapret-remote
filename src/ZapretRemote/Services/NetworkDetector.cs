using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ZapretRemote.Models;

namespace ZapretRemote.Services;

/// <summary>
/// Detects active LAN IPv4 addresses and filters out virtual/tunnel adapters.
/// </summary>
public static class NetworkDetector
{
    private static readonly string[] VirtualKeywords =
    [
        "wsl",
        "vethernet",
        "hyper-v",
        "docker",
        "virtualbox",
        "vbox",
        "vmware",
        "radmin",
        "zerotier",
        "tailscale",
        "wireguard",
        "wintun",
        "tap-",
        "tun",
        "openvpn",
        "hamachi",
        "proton",
        "nekoray",
        "sing-box",
        "clash",
        "vpn",
        "miniport",
        "lightweight filter",
        "teredo",
        "isatap"
    ];

    /// <summary>
    /// Determines the local IPv4 address used for outbound internet routing
    /// via a connected UDP socket (no packets are transmitted).
    /// </summary>
    public static string? GetOutboundIpv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("1.1.1.1", 80);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    public static string? GetGatewayIpv4(string? preferredIp)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;

                var props = nic.GetIPProperties();
                if (!string.IsNullOrWhiteSpace(preferredIp))
                {
                    var matchesIp = props.UnicastAddresses.Any(u =>
                        u.Address.AddressFamily == AddressFamily.InterNetwork &&
                        string.Equals(u.Address.ToString(), preferredIp, StringComparison.OrdinalIgnoreCase));
                    if (!matchesIp)
                        continue;
                }
                else if (IsVirtualAdapter(nic.Name, nic.Description))
                {
                    continue;
                }

                var gw = props.GatewayAddresses.FirstOrDefault(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.Any.Equals(g.Address));
                if (gw is not null)
                    return gw.Address.ToString();
            }
        }
        catch
        {
            // Ignore NIC enumeration errors.
        }

        return null;
    }

    public static IReadOnlyList<LanAdapterInfo> GetLanAdapters()
    {
        var result = new List<LanAdapterInfo>();

        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch
        {
            return result;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up)
                continue;

            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;

            IPInterfaceProperties props;
            try
            {
                props = nic.GetIPProperties();
            }
            catch
            {
                continue;
            }

            var hasGateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork &&
                !IPAddress.Any.Equals(g.Address));

            var isVirtual = IsVirtualAdapter(nic.Name, nic.Description);

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var bytes = unicast.Address.GetAddressBytes();
                if (IsExcludedIpv4(bytes))
                    continue;

                result.Add(new LanAdapterInfo(
                    nic.Name,
                    nic.Description,
                    unicast.Address.ToString(),
                    isVirtual,
                    hasGateway));
            }
        }

        return result
            .OrderBy(a => a.IsVirtual)
            .ThenByDescending(a => a.HasGateway)
            .ThenByDescending(a => IsRfc1918(a.IpAddress))
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static LanAdapterInfo? SelectBestAdapter(IReadOnlyList<LanAdapterInfo> adapters, string? preferredIp)
    {
        if (adapters.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredIp))
        {
            var preferred = adapters.FirstOrDefault(a =>
                string.Equals(a.IpAddress, preferredIp, StringComparison.OrdinalIgnoreCase));
            if (preferred is not null)
                return preferred;
        }

        var outboundIp = GetOutboundIpv4();
        if (!string.IsNullOrWhiteSpace(outboundIp))
        {
            var outboundMatch = adapters.FirstOrDefault(a =>
                !a.IsVirtual &&
                string.Equals(a.IpAddress, outboundIp, StringComparison.OrdinalIgnoreCase));
            if (outboundMatch is not null)
                return outboundMatch;
        }

        return adapters.FirstOrDefault(a => !a.IsVirtual && IsRfc1918(a.IpAddress))
            ?? adapters.FirstOrDefault(a => !a.IsVirtual)
            ?? adapters[0];
    }

    private static bool IsVirtualAdapter(string name, string description)
    {
        foreach (var keyword in VirtualKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                description.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcludedIpv4(byte[] bytes)
    {
        // Exclude 127.0.0.0/8 (loopback), 0.0.0.0/8 (unspecified), 169.254.0.0/16 (APIPA link-local)
        return bytes[0] == 127 || bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254);
    }

    private static bool IsRfc1918(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr))
            return false;

        var b = addr.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168);
    }
}
