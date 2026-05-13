using System.Net;

namespace MavNet.Transport.Udp;

/// <summary>
/// Parses the URI form used throughout MavNet to describe a MAVLink endpoint:
/// <c>udp://localHost:localPort?rhost=remoteHost&amp;rport=remotePort</c>.
///
/// Defaults if not specified: <c>localHost=0.0.0.0</c>, <c>localPort=14550</c>,
/// <c>rhost=127.0.0.1</c>, <c>rport=18570</c>. Only <c>udp://</c> is supported today;
/// the scheme prefix is reserved for future TCP/serial transports.
/// </summary>
public static class ConnectionString
{
    /// <summary>Parses a connection string into local and remote <see cref="IPEndPoint"/> values.</summary>
    /// <exception cref="ArgumentException">Thrown if the scheme is not <c>udp</c>.</exception>
    public static (IPEndPoint Local, IPEndPoint Remote) Parse(string s)
    {
        var uri = new Uri(s.Contains("://") ? s : "udp://" + s);
        if (!string.Equals(uri.Scheme, "udp", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Only udp:// is supported (got '{uri.Scheme}').");

        var localPort = uri.Port > 0 ? uri.Port : 14550;
        var local = new IPEndPoint(
            string.IsNullOrWhiteSpace(uri.Host) ? IPAddress.Any : IPAddress.Parse(uri.Host),
            localPort);

        string? rhost = null;
        int rport = 18570;
        foreach (var kv in (uri.Query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = kv.IndexOf('=');
            if (eq < 0) continue;
            var key = kv[..eq]; var val = kv[(eq + 1)..];
            if (key.Equals("rhost", StringComparison.OrdinalIgnoreCase)) rhost = val;
            if (key.Equals("rport", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out rport);
        }
        var remote = new IPEndPoint(
            string.IsNullOrWhiteSpace(rhost) ? IPAddress.Loopback : IPAddress.Parse(rhost!),
            rport);
        return (local, remote);
    }
}
