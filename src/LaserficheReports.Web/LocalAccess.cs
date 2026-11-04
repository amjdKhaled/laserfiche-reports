using System.Net;
using System.Net.NetworkInformation;

namespace LaserficheReports.Web;

/// <summary>Limits the app to browsers running on this machine, including when IIS serves it by host name.</summary>
internal static class LocalAccess
{
    /// <summary>
    /// True for loopback and for this machine's own interface addresses. A browser on the
    /// server that opens https://{machine-name} connects from the machine's LAN address.
    /// </summary>
    internal static bool IsLocalMachine(IPAddress? address, Func<IEnumerable<IPAddress>>? localAddresses = null)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        // Compare bytes only: IIS may report a link-local client address without its scope ID.
        return (localAddresses ?? MachineAddresses)().Any(local =>
            (local.IsIPv4MappedToIPv6 ? local.MapToIPv4() : local).GetAddressBytes().AsSpan().SequenceEqual(bytes));
    }

    /// <summary>
    /// Same-machine browser origins only: loopback names or this machine's name, with the
    /// request's own scheme and port. Host filtering (AllowedHosts) rejects other Host headers.
    /// </summary>
    internal static bool IsTrustedOrigin(string origin, string requestScheme, int? requestPort, string machineName)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var source)) return false;
        var host = source.Host.Trim('[', ']');
        var localHost = host is "127.0.0.1" or "localhost" or "::1" ||
            string.Equals(host, machineName, StringComparison.OrdinalIgnoreCase);
        var requestPortOrDefault = requestPort ?? (requestScheme == Uri.UriSchemeHttps ? 443 : 80);
        return localHost && source.Scheme == requestScheme && source.Port == requestPortOrDefault;
    }

    private static IEnumerable<IPAddress> MachineAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address);
}
