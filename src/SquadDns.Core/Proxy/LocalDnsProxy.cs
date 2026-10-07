using System.Net;
using System.Net.Sockets;
using SquadDns.Core.Testing;

namespace SquadDns.Core.Proxy;

// Relais DNS local (UDP + TCP sur 127.0.0.1) : le systeme pointe vers la boucle locale et ce
// proxy chiffre ensuite chaque requete en DoH vers le fournisseur choisi. C'est le chiffrement
// « apporte par l'application » pour Windows 10 / 11 < 22H2, qui n'ont aucun client DoH natif.
public sealed class LocalDnsProxy : IDisposable
{
    public const string LoopbackAddress = "127.0.0.1";
    public const int DefaultPort = 53;

    private readonly IDnsUpstream _primary;
    private readonly IDnsUpstream? _fallback;
    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;
    private TcpListener? _tcp;

    public LocalDnsProxy(IDnsUpstream primary, IDnsUpstream? fallback = null)
    {
        _primary = primary;
        _fallback = fallback;
    }

    public int Port { get; private set; }

    // Synchrone volontairement : une erreur de bind (port 53 occupe par une exclusion Hyper-V ou
    // un autre resolveur) doit remonter a l'appelant, pas mourir dans une boucle de tache.
    public void Start(int port = DefaultPort)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _tcp = new TcpListener(IPAddress.Loopback, Port);
        _tcp.Start();

        _ = Task.Run(UdpLoopAsync);
        _ = Task.Run(TcpLoopAsync);
    }

    public async Task<byte[]?> ResolveAsync(byte[] query, CancellationToken ct)
    {
        try
        {
            var response = await _primary.QueryAsync(query, ct);
            if (response is not null)
            {
                return response;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // timeout de l'upstream : on tente le repli
        }

        if (_fallback is null)
        {
            return null;
        }

        try
        {
            return await _fallback.QueryAsync(query, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // Sonde de bout en bout : une vraie requete DNS vers la boucle locale, comme le fera le
    // resolver systeme. Retente quelques fois : le proxy vient d'etre lance et a besoin d'etre
    // pret avant que l'appelant declare l'application reussie.
    public static async Task<bool> ProbeAsync(string domain, int port = DefaultPort, int attempts = 5, CancellationToken ct = default)
    {
        var query = DnsWire.BuildQuery(domain, DnsRecordType.A);

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var udp = new UdpClient();
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await udp.SendAsync(query, query.Length, new IPEndPoint(IPAddress.Loopback, port));
                var result = await udp.ReceiveAsync(timeout.Token);
                var parsed = DnsWire.Parse(result.Buffer);
                return !parsed.IsQuery && parsed.RCode == 0;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // pas encore pret : on retente
            }
            catch (SocketException)
            {
                // personne n'ecoute (encore) : on retente
            }

            await Task.Delay(400, ct);
        }

        return false;
    }

    // Reponse SERVFAIL minimale qui conserve l'identifiant de la question : le resolver systeme
    // echoue proprement au lieu d'attendre un timeout complet.
    public static byte[] BuildServFail(byte[] query)
    {
        var response = new byte[Math.Max(query.Length, 12)];
        Buffer.BlockCopy(query, 0, response, 0, Math.Min(query.Length, 12));
        response[2] = 0x81; // QR + RD
        response[3] = 0x82; // RA + RCODE SERVFAIL
        return response;
    }

    public void Dispose()
    {
        _stop.Cancel();

        try
        {
            _udp?.Close();
        }
        catch (SocketException)
        {
            // ignore
        }

        try
        {
            _tcp?.Stop();
        }
        catch (SocketException)
        {
            // ignore
        }

        _stop.Dispose();
    }

    private async Task UdpLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult packet;
            try
            {
                packet = await _udp!.ReceiveAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break; // socket ferme par Dispose
            }

            var copy = packet;
            _ = Task.Run(async () =>
            {
                byte[] response;
                try
                {
                    response = await ResolveAsync(copy.Buffer, _stop.Token) ?? BuildServFail(copy.Buffer);
                }
                catch (OperationCanceledException)
                {
                    return; // arret du proxy : rien a repondre
                }

                try
                {
                    await _udp.SendAsync(response, response.Length, copy.RemoteEndPoint);
                }
                catch (SocketException)
                {
                    // l'emetteur est parti entre-temps
                }
            });
        }
    }

    private async Task TcpLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcp!.AcceptTcpClientAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break; // listener arrete par Dispose
            }

            _ = Task.Run(() => HandleTcpAsync(client));
        }
    }

    private async Task HandleTcpAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var lengthBuffer = new byte[2];

            while (await ReadExactlyAsync(stream, lengthBuffer, 2, _stop.Token))
            {
                var length = (lengthBuffer[0] << 8) | lengthBuffer[1];
                if (length == 0)
                {
                    break;
                }

                var query = new byte[length];
                if (!await ReadExactlyAsync(stream, query, length, _stop.Token))
                {
                    break;
                }

                byte[] response;
                try
                {
                    response = await ResolveAsync(query, _stop.Token) ?? BuildServFail(query);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var header = new[] { (byte)(response.Length >> 8), (byte)response.Length };
                await stream.WriteAsync(header, _stop.Token);
                await stream.WriteAsync(response, _stop.Token);
                await stream.FlushAsync(_stop.Token);
            }
        }
    }

    private static async Task<bool> ReadExactlyAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }
}
