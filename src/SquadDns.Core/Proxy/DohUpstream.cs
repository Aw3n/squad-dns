using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using SquadDns.Core.Testing;

namespace SquadDns.Core.Proxy;

// Client RFC 8484 (POST application/dns-message) vers le modele DoH du profil choisi.
// Toute la surface reseau est neutralisee : null en cas d'echec, jamais d'exception.
public sealed class DohUpstream : IDnsUpstream
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Uri _template;
    private readonly IDnsUpstream _bootstrap;
    private readonly List<IPAddress> _resolved = new();
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    public DohUpstream(
        string dohTemplate,
        IReadOnlyList<string> bootstrapAddresses,
        HttpClient? http = null,
        IDnsUpstream? bootstrapResolver = null)
    {
        _template = new Uri(dohTemplate);
        // Le resolveur systeme peut pointer vers le proxy local (127.0.0.1) : y resoudre le nom
        // d'hote DoH bouclerait sur nous-memes. Le bootstrap passe donc par les adresses en clair
        // du fournisseur, en UDP direct, pour la seule adresse IP du point DoH — les requetes
        // des utilisateurs, elles, restent chiffrees.
        _bootstrap = bootstrapResolver ?? new PlainUdpUpstream(bootstrapAddresses);
        _http = http ?? CreateDefaultClient();
    }

    private HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler
        {
            // Un proxy systemique (WPAD, pare-feu transparent) ne doit jamais devier le trafic
            // DoH lui-meme : la requete partirait en clair vers un tiers.
            UseProxy = false,
            ConnectCallback = ConnectAsync
        });

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoints = await ResolveEndpointsAsync(context.DnsEndPoint, ct);
        Exception? last = null;

        foreach (var endpoint in endpoints)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endpoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException($"no route to {context.DnsEndPoint.Host}");
    }

    // Adresses IP du point DoH : litterales si le modele porte deja une IP, sinon recherche
    // bootstrap (UDP direct vers le fournisseur) mise en cache. Jamais le resolveur systeme :
    // en mode proxy, c'est nous. Le TLS garde le nom d'hote comme SNI, le certificat reste
    // valide normalement.
    public async Task<IReadOnlyList<IPEndPoint>> ResolveEndpointsAsync(DnsEndPoint endpoint, CancellationToken ct)
    {
        if (IPAddress.TryParse(endpoint.Host, out var literal))
        {
            return new[] { new IPEndPoint(literal, endpoint.Port) };
        }

        await _resolveLock.WaitAsync(ct);
        try
        {
            if (_resolved.Count == 0)
            {
                var response = await _bootstrap.QueryAsync(DnsWire.BuildQuery(endpoint.Host, DnsRecordType.A), ct);
                foreach (var answer in response is null ? Array.Empty<string>() : DnsWire.Parse(response).Answers)
                {
                    if (IPAddress.TryParse(answer, out var ip))
                    {
                        _resolved.Add(ip);
                    }
                }
            }

            if (_resolved.Count == 0)
            {
                throw new InvalidOperationException($"no bootstrap route for {endpoint.Host}");
            }

            return _resolved.Select(ip => new IPEndPoint(ip, endpoint.Port)).ToList();
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _template)
            {
                Content = new ByteArrayContent(query)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.ParseAdd("application/dns-message");
            request.Headers.UserAgent.ParseAdd("SquadDns/1.0");
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;

            using var response = await _http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsByteArrayAsync(timeout.Token)
                : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
