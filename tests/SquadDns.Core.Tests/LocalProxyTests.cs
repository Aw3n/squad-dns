using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SquadDns.Core.Proxy;
using SquadDns.Core.Testing;

namespace SquadDns.Core.Tests;

public class LocalProxyTests
{
    // Repond en echo avec le bit QR positionne : une reponse minimale valide, sans toucher le reseau.
    private sealed class EchoUpstream : IDnsUpstream
    {
        public Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct)
        {
            var response = (byte[])query.Clone();
            response[2] |= 0x80; // QR
            return Task.FromResult<byte[]?>(response);
        }
    }

    private sealed class NullUpstream : IDnsUpstream
    {
        public Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct) => Task.FromResult<byte[]?>(null);
    }

    private sealed class FixedUpstream : IDnsUpstream
    {
        private readonly byte[] _response;

        public FixedUpstream(byte[] response) => _response = response;

        public Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct) => Task.FromResult<byte[]?>(_response);
    }

    // Reponse A minimale : la question en echo, drapeaux de reponse, une adresse en section
    // reponse pointee vers le nom de la question (0xC00C).
    private static byte[] BuildAResponse(string host, string address)
    {
        var response = DnsWire.BuildQuery(host, DnsRecordType.A);
        response[2] = 0x81;
        response[3] = 0x80;
        response[7] = 1; // ancount

        var ip = IPAddress.Parse(address).GetAddressBytes();
        var answer = new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3C, 0x00, 0x04, ip[0], ip[1], ip[2], ip[3] };
        var message = new byte[response.Length + answer.Length];
        Buffer.BlockCopy(response, 0, message, 0, response.Length);
        Buffer.BlockCopy(answer, 0, message, response.Length, answer.Length);
        return message;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<byte[]> Bodies { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content!.ReadAsByteArrayAsync(cancellationToken).Result);
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    [Fact]
    public async Task DohUpstream_posts_a_dns_message_over_h2()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var upstream = new DohUpstream("https://dns.example/dns-query", Array.Empty<string>(), new HttpClient(handler));

        var result = await upstream.QueryAsync(new byte[] { 9, 8, 7 }, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, result);
        var request = Assert.Single(handler.Bodies);
        Assert.Equal(new byte[] { 9, 8, 7 }, request);
    }

    [Fact]
    public async Task DohUpstream_returns_null_when_the_endpoint_refuses()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));
        var upstream = new DohUpstream("https://dns.example/dns-query", Array.Empty<string>(), new HttpClient(handler));

        Assert.Null(await upstream.QueryAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task DohUpstream_never_throws_on_network_failure()
    {
        var upstream = new DohUpstream("https://dns.example/dns-query", Array.Empty<string>(), new HttpClient(new ThrowingHandler()));

        Assert.Null(await upstream.QueryAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task DohUpstream_resolves_its_endpoint_through_the_bootstrap_resolver()
    {
        // Le resolveur systeme peut etre le proxy lui-meme (127.0.0.1) : l'adresse du point
        // DoH doit venir du bootstrap UDP direct, jamais de la resolution systeme.
        var bootstrap = new FixedUpstream(BuildAResponse("dns.example", "93.184.216.34"));
        var upstream = new DohUpstream("https://dns.example/dns-query", Array.Empty<string>(), bootstrapResolver: bootstrap);

        var endpoints = await upstream.ResolveEndpointsAsync(new DnsEndPoint("dns.example", 443), CancellationToken.None);

        Assert.Equal(IPAddress.Parse("93.184.216.34"), Assert.Single(endpoints).Address);
    }

    [Fact]
    public async Task DohUpstream_uses_a_literal_address_without_any_bootstrap()
    {
        var upstream = new DohUpstream("https://1.1.1.1/dns-query", Array.Empty<string>(), bootstrapResolver: new NullUpstream());

        var endpoints = await upstream.ResolveEndpointsAsync(new DnsEndPoint("1.1.1.1", 443), CancellationToken.None);

        Assert.Equal(IPAddress.Parse("1.1.1.1"), Assert.Single(endpoints).Address);
    }

    [Fact]
    public async Task DohUpstream_returns_null_when_no_bootstrap_route_exists()
    {
        // Bootstrap muet et aucun client injecte : la connexion doit echouer proprement
        // (pas de repli sur le resolveur systeme, qui bouclerait sur le proxy).
        var upstream = new DohUpstream("https://dns.example/dns-query", Array.Empty<string>(), bootstrapResolver: new NullUpstream());

        Assert.Null(await upstream.QueryAsync(new byte[] { 1 }, CancellationToken.None));
    }

    [Fact]
    public async Task Proxy_answers_udp_queries_through_the_primary_upstream()
    {
        using var proxy = new LocalDnsProxy(new EchoUpstream());
        proxy.Start(0);

        var response = await RoundTripUdp(proxy.Port, DnsWire.BuildQuery("example.com", DnsRecordType.A));
        var parsed = DnsWire.Parse(response);

        Assert.False(parsed.IsQuery);
        Assert.Equal(0, parsed.RCode);
    }

    [Fact]
    public async Task Proxy_serves_fail_when_no_upstream_answers_and_no_fallback_exists()
    {
        using var proxy = new LocalDnsProxy(new NullUpstream());
        proxy.Start(0);

        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A, id: 0x1234);
        var response = await RoundTripUdp(proxy.Port, query);
        var parsed = DnsWire.Parse(response);

        Assert.Equal(0x1234, parsed.Id);
        Assert.False(parsed.IsQuery);
        Assert.Equal(2, parsed.RCode); // SERVFAIL
    }

    [Fact]
    public async Task Proxy_falls_back_to_plain_udp_when_the_primary_fails()
    {
        using var proxy = new LocalDnsProxy(new NullUpstream(), new EchoUpstream());
        proxy.Start(0);

        var response = await RoundTripUdp(proxy.Port, DnsWire.BuildQuery("example.com", DnsRecordType.A));
        var parsed = DnsWire.Parse(response);

        Assert.False(parsed.IsQuery);
        Assert.Equal(0, parsed.RCode);
    }

    [Fact]
    public async Task Proxy_answers_tcp_queries_with_length_prefix_framing()
    {
        using var proxy = new LocalDnsProxy(new EchoUpstream());
        proxy.Start(0);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        var stream = client.GetStream();

        var query = DnsWire.BuildQuery("example.com", DnsRecordType.A);
        await stream.WriteAsync(new[] { (byte)(query.Length >> 8), (byte)query.Length });
        await stream.WriteAsync(query);
        await stream.FlushAsync();

        var header = await ReadExactlyAsync(stream, 2);
        var length = (header[0] << 8) | header[1];
        var response = await ReadExactlyAsync(stream, length);
        var parsed = DnsWire.Parse(response);

        Assert.False(parsed.IsQuery);
        Assert.Equal(0, parsed.RCode);
    }

    [Fact]
    public async Task Probe_succeeds_against_a_live_proxy()
    {
        using var proxy = new LocalDnsProxy(new EchoUpstream());
        proxy.Start(0);

        Assert.True(await LocalDnsProxy.ProbeAsync("example.com", proxy.Port, attempts: 3));
    }

    [Fact]
    public async Task Probe_gives_up_when_nothing_listens()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var deadPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        udp.Close(); // personne n'ecoute sur ce port desormais

        Assert.False(await LocalDnsProxy.ProbeAsync("example.com", deadPort, attempts: 1));
    }

    private static async Task<byte[]> RoundTripUdp(int port, byte[] query)
    {
        using var udp = new UdpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Loopback, port));
        var result = await udp.ReceiveAsync(timeout.Token);
        return result.Buffer;
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset));
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return buffer;
    }
}
