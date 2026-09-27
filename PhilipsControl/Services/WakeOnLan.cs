using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;

namespace PhilipsControl.Services;

public static partial class WakeOnLan
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(uint destinationIp, uint sourceIp, IntPtr macAddress, ref int physicalAddressLength);

    [GeneratedRegex("^([0-9A-F]{2}:){5}[0-9A-F]{2}$")]
    private static partial Regex MacPattern();

    /// <summary>Normalizes AA-BB-CC..., aabbccddeeff or AA:BB:... into AA:BB:CC:DD:EE:FF, or returns null when invalid.</summary>
    public static string? Normalize(string value)
    {
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12 || value.Any(c => !Uri.IsHexDigit(c) && c is not (':' or '-' or '.' or ' '))) return null;
        var mac = string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
        return MacPattern().IsMatch(mac) ? mac : null;
    }

    /// <summary>Resolves the MAC through ARP. Blocks for up to a few seconds, so call it off the UI thread.</summary>
    public static string TryResolveMac(string host)
    {
        if (!IPAddress.TryParse(host, out var address) || address.AddressFamily != AddressFamily.InterNetwork) return "";
        var buffer = Marshal.AllocHGlobal(8);
        var length = 8;
        try
        {
            var rawIp = BitConverter.ToUInt32(address.GetAddressBytes());
            if (SendARP(rawIp, 0, buffer, ref length) != 0 || length < 6) return "";
            var mac = new byte[length];
            Marshal.Copy(buffer, mac, 0, length);
            if (mac.Take(6).All(b => b == 0)) return "";
            return string.Join(":", mac.Take(6).Select(b => b.ToString("X2")));
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static Task<string> ResolveMacAsync(string host) => Task.Run(() => TryResolveMac(host));

    /// <summary>
    /// Sends the magic packet several times to the global broadcast, every local subnet's directed broadcast
    /// and the TV's last known address, on ports 9 and 7. Multi-adapter PCs otherwise often send it out of the wrong interface.
    /// </summary>
    public static async Task SendAsync(string macAddress, string? lastKnownHost = null, CancellationToken ct = default)
    {
        var normalized = Normalize(macAddress) ?? throw new InvalidOperationException("Enter a valid MAC address in the format AA:BB:CC:DD:EE:FF.");
        var mac = normalized.Split(':').Select(part => byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);

        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var (local, broadcast) in LocalSubnets())
            targets.Add(broadcast);
        if (lastKnownHost is not null && IPAddress.TryParse(lastKnownHost, out var known) && known.AddressFamily == AddressFamily.InterNetwork)
            targets.Add(known);

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        for (var round = 0; round < 3; round++)
        {
            foreach (var target in targets)
                foreach (var port in new[] { 9, 7 })
                {
                    try { await udp.SendAsync(packet.AsMemory(), new IPEndPoint(target, port), ct); }
                    catch (SocketException) { }
                }
            await Task.Delay(120, ct);
        }
    }

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> LocalSubnets()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            IPInterfaceProperties props;
            try { props = ni.GetIPProperties(); } catch { continue; }
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
                var ip = unicast.Address.GetAddressBytes();
                var mask = unicast.IPv4Mask.GetAddressBytes();
                if (ip[0] == 169 && ip[1] == 254) continue;
                var broadcast = new byte[4];
                for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return (unicast.Address, new IPAddress(broadcast));
            }
        }
    }
}
