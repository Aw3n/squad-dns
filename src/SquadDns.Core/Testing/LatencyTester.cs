using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using SquadDns.Core.Models;

namespace SquadDns.Core.Testing;

public sealed record TestSample(
    TransportKind Transport,
    string Target,
    bool Success,
    double LatencyMs,
    string? Error,
    IReadOnlyList<string> Answers,
    double? TlsHandshakeMs = null)
{
    public string Detail => Success
        ? Answers.Count == 0 ? "no answer" : string.Join(", ", Answers.Take(2))
        : Error ?? "failed";
}

public sealed record ProviderTestResult(
    DnsProfile Profile,
    IReadOnlyList<TestSample> Samples,
    double? MedianDoH,
    double? MedianDoT,
    double? MedianPlain,
    double? TlsHandshakeMs,
    string? Note)
{
    public int SuccessCount => Samples.Count(s => s.Success);
    public int TotalCount => Samples.Count;
    public double? Best => MedianDoH ?? MedianPlain;
    public bool DoHWorks => MedianDoH.HasValue;
    public bool DoTWorks => MedianDoT.HasValue;
}

public sealed class LatencyTester
{
    private static readonly HttpClient Http = CreateClient();

    public TimeSpan PerAttemptTimeout { get; set; } = TimeSpan.FromSeconds(4);

    private static HttpClient CreateClient() =>
        new(new HttpClientHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false
        })
        { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    public async Task<ProviderTestResult> RunAsync(
        DnsProfile profile,
        string domain,
        int samples,
        bool includeTransportTests = true,
        CancellationToken ct = default)
    {
        var collected = new List<TestSample>();
        var count = Math.Max(1, Math.Min(samples, 20));

        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            collected.Add(await TestDoHAsync(profile, domain, ct));
            if (i < count - 1)
            {
                await Task.Delay(120, ct);
            }
        }

        if (includeTransportTests && !ct.IsCancellationRequested)
        {
            collected.Add(await TestDoTAsync(profile, domain, ct));
            collected.Add(await TestPlainUdpAsync(profile, domain, ct));
        }

        var dotSample = collected.FirstOrDefault(s => s.Transport == TransportKind.DoT);
        var plainSample = collected.FirstOrDefault(s => s.Transport == TransportKind.Plain);
        var tlsHandshake = dotSample?.TlsHandshakeMs;

        var dohSamples = collected.Where(s => s.Transport == TransportKind.DoH).ToList();
        var usedAlternate = false;

        if (dohSamples.All(s => !s.Success) && !string.IsNullOrWhiteSpace(profile.SecondaryDoHTemplate) && !ct.IsCancellationRequested)
        {
            var alternate = await TestDoHAsync(profile with { DoHTemplate = profile.SecondaryDoHTemplate! }, domain, ct);
            collected.Add(alternate);
            usedAlternate = alternate.Success;
        }

        return new ProviderTestResult(
            profile,
            collected,
            Median(collected, TransportKind.DoH),
            dotSample is { Success: true } ? Math.Round(dotSample.LatencyMs, 1) : null,
            plainSample is { Success: true } ? Math.Round(plainSample.LatencyMs, 1) : null,
            tlsHandshake is null ? null : Math.Round(tlsHandshake.Value, 1),
            BuildNote(profile, dotSample, dohSamples, usedAlternate));
    }

    private static double? Median(List<TestSample> samples, TransportKind kind)
    {
        var values = samples
            .Where(s => s.Transport == kind && s.Success)
            .Select(s => s.LatencyMs)
            .OrderBy(v => v)
            .ToList();

        if (values.Count == 0)
        {
            return null;
        }

        var middle = values.Count / 2;
        return values.Count % 2 == 1
            ? Math.Round(values[middle], 1)
            : Math.Round((values[middle - 1] + values[middle]) / 2.0, 1);
    }

    private static string? BuildNote(
        DnsProfile profile,
        TestSample? dotSample,
        IReadOnlyList<TestSample> dohSamples,
        bool usedAlternate)
    {
        if (profile.RequiresConfigurationId)
        {
            return "note.requiresConfigId";
        }

        if (profile.Sunset is { } sunset)
        {
            return DateTimeOffset.UtcNow <= sunset ? "note.sunsetSoon" : "note.sunsetPassed";
        }

        if (usedAlternate)
        {
            return "note.altEndpoint";
        }

        if (dohSamples.Count > 0 && dohSamples.All(s => !s.Success))
        {
            return "note.dohUnreachable";
        }

        if (dotSample is { Success: false })
        {
            return "note.dotUnreachable";
        }

        return null;
    }

    public async Task<TestSample> TestDoHAsync(DnsProfile profile, string domain, CancellationToken ct = default)
    {
        var query = DnsWire.BuildQuery(domain, DnsRecordType.A);
        var baseUri = CleanTemplate(profile.DoHTemplate);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUri)
            {
                Content = new ByteArrayContent(query)
            };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Headers.UserAgent.ParseAdd("SquadDns/1.0");
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            using var response = await Http.SendAsync(request, timeout.Token);
            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return await TryGetAsync(profile, baseUri, query, ct);
            }

            var parsed = payload.Length > 0 ? DnsWire.Parse(payload) : null;
            var success = parsed is { AnswerCount: > 0 };

            return new TestSample(
                TransportKind.DoH,
                profile.DoHTemplate,
                success,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                parsed is null ? "error.emptyBody" : success ? null : $"error.rcode{parsed.RCode}",
                parsed?.Answers ?? Array.Empty<string>());
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Fail(profile, "error.timeout", stopwatch);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Fail(profile, Shorten(ex), stopwatch);
        }
    }

    private static TestSample Fail(DnsProfile profile, string error, Stopwatch stopwatch) =>
        new(TransportKind.DoH, profile.DoHTemplate, false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), error, Array.Empty<string>());

    private async Task<TestSample> TryGetAsync(DnsProfile profile, Uri baseUri, byte[] query, CancellationToken ct)
    {
        var getUrl = new Uri($"{baseUri.AbsoluteUri}?dns={DnsWire.Base64UrlEncode(query)}");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, getUrl);
            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            using var response = await Http.SendAsync(request, timeout.Token);
            var payload = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return new TestSample(TransportKind.DoH, profile.DoHTemplate, false,
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), $"error.http{(int)response.StatusCode}", Array.Empty<string>());
            }

            var parsed = DnsWire.Parse(payload);
            return new TestSample(TransportKind.DoH, profile.DoHTemplate, parsed.AnswerCount > 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), null, parsed.Answers);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TestSample(TransportKind.DoH, profile.DoHTemplate, false,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), $"error.postThenGet:{Shorten(ex)}", Array.Empty<string>());
        }
    }

    public async Task<TestSample> TestDoTAsync(DnsProfile profile, string domain, CancellationToken ct = default)
    {
        var target = $"{profile.DoTServer}:{profile.DoTPort}";
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(profile.DoTServer, profile.DoTPort, timeout.Token);

            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            var handshake = Stopwatch.StartNew();
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = profile.DoTSni,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            }, timeout.Token);
            handshake.Stop();

            var query = DnsWire.BuildQuery(domain, DnsRecordType.A);
            var prefixed = new byte[query.Length + 2];
            prefixed[0] = (byte)(query.Length >> 8);
            prefixed[1] = (byte)(query.Length & 0xFF);
            Buffer.BlockCopy(query, 0, prefixed, 2, query.Length);

            await ssl.WriteAsync(prefixed, timeout.Token);
            await ssl.FlushAsync(timeout.Token);

            var lengthPrefix = new byte[2];
            await ReadExactAsync(ssl, lengthPrefix, timeout.Token);
            var length = (lengthPrefix[0] << 8) | lengthPrefix[1];

            if (length is 0 or > 4096)
            {
                throw new IOException($"invalid DoT response length {length}");
            }

            var payload = new byte[length];
            await ReadExactAsync(ssl, payload, timeout.Token);
            stopwatch.Stop();

            var parsed = DnsWire.Parse(payload);
            return new TestSample(TransportKind.DoT, target, parsed.AnswerCount > 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                parsed.AnswerCount > 0 ? null : $"error.rcode{parsed.RCode}",
                parsed.Answers,
                Math.Round(handshake.Elapsed.TotalMilliseconds, 1));
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new TestSample(TransportKind.DoT, target, false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                "error.timeout", Array.Empty<string>());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var tcpOpen = await TcpProbeAsync(profile.DoTServer, profile.DoTPort, TimeSpan.FromMilliseconds(900), ct);
            var error = ex is AuthenticationException ? "error.tlsRejected" : tcpOpen ? "error.queryFailed" : "error.portClosed";
            return new TestSample(TransportKind.DoT, target, false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                $"{error}:{Shorten(ex)}", Array.Empty<string>());
        }
    }

    public async Task<TestSample> TestPlainUdpAsync(DnsProfile profile, string domain, CancellationToken ct = default)
    {
        var server = profile.ResolverAddresses.FirstOrDefault(a => a.Contains('.')) ?? profile.DoTServer;
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var endpoint = ResolveEndpoint(server);
            await udp.Client.ConnectAsync(endpoint, timeout.Token);
            await udp.SendAsync(DnsWire.BuildQuery(domain, DnsRecordType.A), timeout.Token);

            var receive = await udp.ReceiveAsync(timeout.Token);
            stopwatch.Stop();

            var parsed = DnsWire.Parse(receive.Buffer);
            return new TestSample(TransportKind.Plain, server, parsed.AnswerCount > 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                parsed.AnswerCount > 0 ? null : $"error.rcode{parsed.RCode}",
                parsed.Answers);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TestSample(TransportKind.Plain, server, false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                Shorten(ex), Array.Empty<string>());
        }
    }

    public async Task<TestSample> TestSystemResolverAsync(string domain, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(PerAttemptTimeout);

        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain, AddressFamily.InterNetwork, timeout.Token);
            stopwatch.Stop();
            return new TestSample(TransportKind.SystemResolver, "system", addresses.Length > 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), null,
                addresses.Select(a => a.ToString()).ToList());
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TestSample(TransportKind.SystemResolver, "system", false, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                Shorten(ex), Array.Empty<string>());
        }
    }

    private static System.Net.IPEndPoint ResolveEndpoint(string server)
    {
        if (System.Net.IPAddress.TryParse(server, out var address))
        {
            return new System.Net.IPEndPoint(address, 53);
        }

        var resolved = System.Net.Dns.GetHostAddresses(server);
        if (resolved.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return new System.Net.IPEndPoint(resolved[0], 53);
    }

    private static async Task<bool> TcpProbeAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(timeout);
            await client.ConnectAsync(host, port, probe.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (chunk == 0)
            {
                throw new IOException("connection closed while reading response");
            }

            read += chunk;
        }
    }

    private static Uri CleanTemplate(string template)
    {
        var trimmed = template.Trim();
        var questionMark = trimmed.IndexOf('?');
        if (questionMark > 0)
        {
            trimmed = trimmed.Substring(0, questionMark);
        }

        // The path must stay byte-for-byte as published: /dns-query/ and /dns-query are
        // different resources and several resolvers answer the wrong one with 301 or 404.
        return new Uri(trimmed);
    }

    private static string Shorten(Exception ex)
    {
        var message = ex is AggregateException { InnerException: not null } aggregate
            ? aggregate.InnerException!.Message
            : ex.Message;

        message = message.Replace('\n', ' ').Replace('\r', ' ');
        return message.Length > 120 ? message[..120] + "..." : message;
    }
}
