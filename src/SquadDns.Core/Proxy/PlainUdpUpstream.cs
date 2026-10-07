using System.Net;
using System.Net.Sockets;

namespace SquadDns.Core.Proxy;

// Repli en clair vers les adresses du resolveur (port 53). Reserve au mode « chiffre de
// preference » : le proxy local ne repond jamais SERVFAIL tant qu'un repli est possible.
public sealed class PlainUdpUpstream : IDnsUpstream
{
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyList<string> _addresses;

    public PlainUdpUpstream(IEnumerable<string> addresses) => _addresses = addresses.ToList();

    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct)
    {
        using var udp = new UdpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(AttemptTimeout);

        foreach (var address in _addresses)
        {
            if (!IPAddress.TryParse(address, out var ip))
            {
                continue;
            }

            try
            {
                await udp.SendAsync(query, query.Length, new IPEndPoint(ip, 53));
                var result = await udp.ReceiveAsync(timeout.Token);
                return result.Buffer;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // timeout sur cette adresse : on tente la suivante
            }
            catch (SocketException)
            {
                // injoignable : on tente la suivante
            }
        }

        return null;
    }
}
