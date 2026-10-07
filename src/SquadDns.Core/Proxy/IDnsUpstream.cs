namespace SquadDns.Core.Proxy;

public interface IDnsUpstream
{
    // null = pas de reponse exploitable (HTTP ko, timeout, wire invalide) : l'appelant
    // bascule alors sur le repli ou renvoie un SERVFAIL.
    Task<byte[]?> QueryAsync(byte[] query, CancellationToken ct);
}
