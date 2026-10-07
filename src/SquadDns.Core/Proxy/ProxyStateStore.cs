using System.Text.Json;
using SquadDns.Core.Models;

namespace SquadDns.Core.Proxy;

public sealed record LocalProxyState(string ProfileId, string Mode, int ProcessId)
{
    public DnsSecurityMode ModeValue =>
        Enum.TryParse<DnsSecurityMode>(Mode, true, out var parsed)
            ? parsed
            : DnsSecurityMode.EncryptedPreferred;
}

// Fichier d'etat du proxy detache (proxy-state.json dans le dossier de l'application) : permet
// de retrouver quel profil est relaye et par quel processus, y compris apres un redemarrage.
// Le pid n'est qu'une indication : tout lecteur doit verifier qu'il est encore vivant, car un
// processus tue (taskkill / fermeture de session) ne passe jamais par le nettoyage de sortie.
public static class ProxyStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static LocalProxyState? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var state = JsonSerializer.Deserialize<LocalProxyState>(File.ReadAllText(path));
            return state is { ProcessId: > 0 } ? state : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(string path, LocalProxyState state)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // l'etat est une commodite : son absence se traite comme « pas de proxy »
        }
    }

    public static void Clear(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }

    public static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
