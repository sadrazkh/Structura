using System.Net;
using System.Net.Sockets;
using Structura.Web.Infrastructure.Errors;

namespace Structura.Web.Infrastructure.Http;

public enum SafeHttpProfile
{
    /// <summary>Input/output connectors: strict SSRF rules, 10 MB response cap.</summary>
    Connector,
    /// <summary>AI providers: same rules unless ALLOW_PRIVATE_AI_ENDPOINTS=true; 50 MB cap.</summary>
    AiProvider,
}

public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct);
}

public sealed class SystemDnsResolver : IDnsResolver
{
    public async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
        await Dns.GetHostAddressesAsync(host, ct);
}

public sealed class SafeHttpOptions
{
    public bool AllowInsecureHttp { get; set; }
    public bool AllowPrivateAiEndpoints { get; set; }
    /// <summary>Dev/test escape hatch only — production keeps connector targets public.</summary>
    public bool AllowPrivateConnectorTargets { get; set; }
    public string? OutboundProxyUrl { get; set; }
}

/// <summary>
/// The single gate for all outbound HTTP (docs/07 §7): scheme allowlist, DNS resolution with
/// private/reserved IP blocking, connection pinned to the vetted IP (defeats DNS rebinding),
/// redirects disabled, response size caps, timeouts.
/// </summary>
public sealed class SafeHttpClientFactory(IDnsResolver resolver, SafeHttpOptions options)
{
    public const long ConnectorResponseCap = 10 * 1024 * 1024;
    public const long AiResponseCap = 50 * 1024 * 1024;

    public SafeHttpOptions Options => options;

    /// <summary>Validates scheme + resolves and vets the host. Throws 400 `unsafe_url` problems.</summary>
    public async Task<IPAddress> ValidateAsync(Uri url, SafeHttpProfile profile, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps && !(url.Scheme == Uri.UriSchemeHttp && options.AllowInsecureHttp))
            throw new AppException(StatusCodes.Status400BadRequest, "unsafe_url",
                "Only https:// URLs are allowed.");

        var allowPrivate = profile switch
        {
            SafeHttpProfile.AiProvider => options.AllowPrivateAiEndpoints,
            SafeHttpProfile.Connector => options.AllowPrivateConnectorTargets,
            _ => false,
        };

        IPAddress[] addresses;
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(url.Host, ct);
            }
            catch (SocketException)
            {
                throw new AppException(StatusCodes.Status400BadRequest, "unsafe_url",
                    $"Host '{url.Host}' could not be resolved.");
            }
            if (addresses.Length == 0)
                throw new AppException(StatusCodes.Status400BadRequest, "unsafe_url",
                    $"Host '{url.Host}' could not be resolved.");
        }

        foreach (var address in addresses)
        {
            if (!allowPrivate && IsBlockedAddress(address))
                throw new AppException(StatusCodes.Status400BadRequest, "unsafe_url",
                    "The URL resolves to a private or reserved network address, which is not allowed.");
        }
        return addresses[0];
    }

    /// <summary>Creates a client whose connections are pinned to the pre-vetted IP.</summary>
    public HttpClient CreateClient(IPAddress pinnedAddress, SafeHttpProfile profile, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                // Reconnects go to the vetted IP regardless of what DNS says now (anti-rebinding).
                var socket = new Socket(pinnedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                await socket.ConnectAsync(new IPEndPoint(pinnedAddress, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        if (!string.IsNullOrWhiteSpace(options.OutboundProxyUrl))
        {
            // With a proxy, the proxy connects outward; pinning is not applicable.
            handler.ConnectCallback = null;
            handler.Proxy = new WebProxy(options.OutboundProxyUrl);
            handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = timeout };
    }

    /// <summary>One-call helper: validate, pin, send. TLS SNI/Host stay on the original hostname.</summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, SafeHttpProfile profile, TimeSpan timeout, CancellationToken ct)
    {
        var address = await ValidateAsync(request.RequestUri!, profile, ct);
        using var client = CreateClient(address, profile, timeout);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>Reads a response body enforcing the profile's byte cap.</summary>
    public static async Task<string> ReadBodyCappedAsync(
        HttpResponseMessage response, SafeHttpProfile profile, CancellationToken ct)
    {
        var cap = profile == SafeHttpProfile.Connector ? ConnectorResponseCap : AiResponseCap;
        if (response.Content.Headers.ContentLength > cap)
            throw new AppException(StatusCodes.Status502BadGateway, "response_too_large",
                "The remote response exceeds the allowed size.");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > cap)
                throw new AppException(StatusCodes.Status502BadGateway, "response_too_large",
                    "The remote response exceeds the allowed size.");
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address)) return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,                                   // 0.0.0.0/8
                10 => true,                                  // 10.0.0.0/8
                100 when bytes[1] >= 64 && bytes[1] <= 127 => true, // 100.64.0.0/10 (CGNAT)
                127 => true,                                 // loopback
                169 when bytes[1] == 254 => true,            // link-local incl. 169.254.169.254 metadata
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,  // 172.16.0.0/12
                192 when bytes[1] == 168 => true,            // 192.168.0.0/16
                192 when bytes[1] == 0 && bytes[2] == 0 => true,    // 192.0.0.0/24
                198 when bytes[1] is 18 or 19 => true,       // 198.18.0.0/15 (benchmarking)
                >= 224 => true,                              // multicast + reserved
                _ => false,
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;      // fc00::/7 (ULA)
            if (address.Equals(IPAddress.IPv6Any)) return true;
            return false;
        }

        return true; // unknown families are blocked
    }
}
