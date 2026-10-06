using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using SquadDns.Core.Io;
using SquadDns.Core.Models;
using SquadDns.Core.Network;
using SquadDns.Core.Settings;

namespace SquadDns.Core.Backup;

public sealed record DnsBackup(
    string Id,
    DateTimeOffset CreatedAt,
    string Label,
    int InterfaceIndex,
    string InterfaceAlias,
    IReadOnlyList<string> Servers,
    bool UseDhcp,
    int? PreviousPolicy,
    IReadOnlyList<string> AddedDohAddresses,
    string ProfileId,
    string? ProfileName)
{
    public DnsRestorePlan ToPlan() =>
        new(InterfaceIndex, InterfaceAlias, Servers, UseDhcp, PreviousPolicy, AddedDohAddresses);

    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class BackupStore
{
    public const string RegistryPath = @"Software\SquadDns\Backups";
    private const int MaxBackups = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;
    private readonly IRegistryAccess _registry;

    public BackupStore(AppPaths paths, IRegistryAccess registry)
    {
        _paths = paths;
        _registry = registry;
    }

    public DnsBackup Capture(
        NetworkAdapter adapter,
        EffectiveDnsState state,
        DnsProfile profile,
        IReadOnlyList<string> addedDohAddresses,
        string? label = null)
    {
        var backup = new DnsBackup(
            Guid.NewGuid().ToString("N")[..12],
            DateTimeOffset.Now,
            label ?? $"{profile.Name} / {adapter.Alias}",
            adapter.Index,
            adapter.Alias,
            state.Servers.ToList(),
            adapter.UsesDhcp || state.Servers.Count == 0,
            state.PolicyValue,
            addedDohAddresses.ToList(),
            profile.Id,
            profile.Name);

        Write(backup);
        Prune();
        return backup;
    }

    public void Write(DnsBackup backup)
    {
        var json = JsonSerializer.Serialize(backup, JsonOptions);
        File.WriteAllText(PathOf(backup.Id), json);

        try
        {
            _registry.WriteString(RegistryHive.CurrentUser, $@"{RegistryPath}\{backup.Id}", "Json", json);
            _registry.WriteString(RegistryHive.CurrentUser, $@"{RegistryPath}\{backup.Id}", "CreatedAt", backup.CreatedAt.ToString("o"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // file copy is authoritative, registry mirror is best effort
        }
    }

    public IReadOnlyList<DnsBackup> List()
    {
        var results = new List<DnsBackup>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in EnumerateFiles())
        {
            var backup = TryReadFile(file);
            if (backup is not null && seen.Add(backup.Id))
            {
                results.Add(backup);
            }
        }

        foreach (var id in SafeSubKeyNames())
        {
            if (seen.Contains(id))
            {
                continue;
            }

            var json = _registry.ReadString(RegistryHive.CurrentUser, $@"{RegistryPath}\{id}", "Json");
            var backup = TryParse(json);
            if (backup is not null && seen.Add(backup.Id))
            {
                results.Add(backup);
            }
        }

        return results.OrderByDescending(b => b.CreatedAt).ToList();
    }

    public DnsBackup? Load(string id) =>
        TryReadFile(PathOf(id)) ?? TryParse(_registry.ReadString(RegistryHive.CurrentUser, $@"{RegistryPath}\{id}", "Json"));

    public void Delete(string id)
    {
        try
        {
            if (File.Exists(PathOf(id)))
            {
                File.Delete(PathOf(id));
            }
        }
        catch (IOException)
        {
            // ignore
        }

        try
        {
            if (_registry.KeyExists(RegistryHive.CurrentUser, $@"{RegistryPath}\{id}"))
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
                using var parent = baseKey.OpenSubKey(RegistryPath, writable: true);
                parent?.DeleteSubKeyTree(id, throwOnMissingSubKey: false);
            }
        }
        catch (IOException)
        {
            // ignore
        }
    }

    public string Export(string id, string destinationPath)
    {
        var backup = Load(id) ?? throw new FileNotFoundException($"backup {id} not found");
        File.WriteAllText(destinationPath, JsonSerializer.Serialize(backup, JsonOptions));
        return destinationPath;
    }

    public DnsBackup? Import(string sourcePath)
    {
        if (!File.Exists(sourcePath))
        {
            return null;
        }

        var backup = TryParse(File.ReadAllText(sourcePath));
        if (backup is null)
        {
            return null;
        }

        Write(backup);
        return backup;
    }

    private void Prune()
    {
        var backups = List();
        foreach (var stale in backups.Skip(MaxBackups))
        {
            Delete(stale.Id);
        }
    }

    private IEnumerable<string> EnumerateFiles()
    {
        try
        {
            return Directory.Exists(_paths.Backups)
                ? Directory.GetFiles(_paths.Backups, "backup-*.json")
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private IEnumerable<string> SafeSubKeyNames()
    {
        try
        {
            return _registry.ReadSubKeyNames(RegistryHive.CurrentUser, RegistryPath);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private string PathOf(string id) => Path.Combine(_paths.Backups, $"backup-{id}.json");

    private static DnsBackup? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? TryParse(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DnsBackup? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DnsBackup>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
