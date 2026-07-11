using System.Net;
using FluentAssertions;
using Structura.Web.Infrastructure.Errors;
using Structura.Web.Infrastructure.Http;
using Xunit;

namespace Structura.Tests.Unit;

public class SafeHttpTests
{
    private sealed class FakeResolver(Dictionary<string, IPAddress[]> map) : IDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) =>
            Task.FromResult(map.GetValueOrDefault(host, []));
    }

    private static SafeHttpClientFactory Factory(
        Dictionary<string, IPAddress[]>? dns = null, SafeHttpOptions? options = null) =>
        new(new FakeResolver(dns ?? []), options ?? new SafeHttpOptions());

    // --- IP classification matrix (docs/07 §7) ---

    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("10.1.2.3")]         // RFC1918
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]  // cloud metadata / link-local
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("0.0.0.0")]
    [InlineData("198.18.0.1")]       // benchmarking
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("fe80::1")]          // IPv6 link-local
    [InlineData("fc00::1")]          // IPv6 ULA
    [InlineData("fd12:3456::1")]
    public void Blocked_addresses_are_detected(string ip) =>
        SafeHttpClientFactory.IsBlockedAddress(IPAddress.Parse(ip)).Should().BeTrue();

    [Theory]
    [InlineData("93.184.216.34")]    // public IPv4
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]       // just outside 172.16/12
    [InlineData("100.128.0.1")]      // just outside CGNAT
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")] // public IPv6
    public void Public_addresses_are_allowed(string ip) =>
        SafeHttpClientFactory.IsBlockedAddress(IPAddress.Parse(ip)).Should().BeFalse();

    [Fact]
    public void IPv4_mapped_IPv6_loopback_is_blocked() =>
        SafeHttpClientFactory.IsBlockedAddress(IPAddress.Parse("::ffff:127.0.0.1")).Should().BeTrue();

    // --- URL validation ---

    [Fact]
    public async Task Http_scheme_is_rejected_by_default()
    {
        var act = () => Factory().ValidateAsync(new Uri("http://example.com/x"), SafeHttpProfile.Connector, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("unsafe_url");
    }

    [Fact]
    public async Task Http_scheme_is_allowed_when_dev_flag_is_on()
    {
        var factory = Factory(
            new Dictionary<string, IPAddress[]> { ["example.com"] = [IPAddress.Parse("93.184.216.34")] },
            new SafeHttpOptions { AllowInsecureHttp = true });
        var ip = await factory.ValidateAsync(new Uri("http://example.com/x"), SafeHttpProfile.Connector, default);
        ip.ToString().Should().Be("93.184.216.34");
    }

    [Fact]
    public async Task Hostname_resolving_to_private_address_is_rejected()
    {
        // The classic SSRF trick: a public-looking hostname pointing at an internal IP.
        var factory = Factory(new Dictionary<string, IPAddress[]>
        {
            ["evil.example.com"] = [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.5")],
        });
        var act = () => factory.ValidateAsync(new Uri("https://evil.example.com/"), SafeHttpProfile.Connector, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("unsafe_url");
    }

    [Fact]
    public async Task Private_ip_literal_is_rejected_for_connectors()
    {
        var act = () => Factory().ValidateAsync(new Uri("https://192.168.1.10/api"), SafeHttpProfile.Connector, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("unsafe_url");
    }

    [Fact]
    public async Task Metadata_endpoint_is_rejected_even_for_ai_profile()
    {
        var act = () => Factory().ValidateAsync(
            new Uri("https://169.254.169.254/latest/meta-data"), SafeHttpProfile.AiProvider, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("unsafe_url");
    }

    [Fact]
    public async Task Private_ai_endpoint_is_allowed_only_with_flag()
    {
        var options = new SafeHttpOptions { AllowPrivateAiEndpoints = true, AllowInsecureHttp = true };
        var ip = await Factory(options: options).ValidateAsync(
            new Uri("http://127.0.0.1:8080/v1"), SafeHttpProfile.AiProvider, default);
        ip.Should().Be(IPAddress.Parse("127.0.0.1"));

        // Connector profile stays blocked with the AI flag on.
        var act = () => Factory(options: options).ValidateAsync(
            new Uri("http://127.0.0.1:8080/v1"), SafeHttpProfile.Connector, default);
        await act.Should().ThrowAsync<AppException>();
    }

    [Fact]
    public async Task Unresolvable_host_is_rejected()
    {
        var act = () => Factory().ValidateAsync(new Uri("https://nope.invalid/"), SafeHttpProfile.Connector, default);
        (await act.Should().ThrowAsync<AppException>()).Which.Code.Should().Be("unsafe_url");
    }
}
