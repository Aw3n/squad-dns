using System.Net;
using System.Net.Http.Headers;

namespace SquadDns.Core.Proxy;

// Client RFC 8484 (POST application/dns-message) vers le modele DoH du profil choisi.
// Toute la surface reseau est neutralisee : null en cas d'echec, jamais d'exception.
public sealed class DohUpstream : IDnsUpstream
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly Uri _template;

    public DohUpstream(string dohTemplate, HttpClient? http = null)
    {
        _template = new Uri(dohTemplate);
        _http = http ?? CreateDefaultClient();
    }

    private static HttpClient CreateDefaultClient() =>
        new(new SocketsHttpHandler
        {
            // Un proxy systemique (WPAD, pare-feu transparent) ne doit jamais devier le trafic
            // DoH lui-meme : la requete partirait en clair vers un tiers.
            UseProxy = false
        });

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
