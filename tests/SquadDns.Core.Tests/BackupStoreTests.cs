using Microsoft.Win32;
using SquadDns.Core.Backup;
using SquadDns.Core.Catalog;
using SquadDns.Core.Models;
using SquadDns.Core.Network;
using SquadDns.Core.Settings;

namespace SquadDns.Core.Tests;

public class BackupStoreTests : IDisposable
{
    private readonly TempDirectory _root = new();
    private readonly FakeRegistry _registry = new();
    private readonly BackupStore _store;

    public BackupStoreTests() => _store = new BackupStore(new AppPaths(_root.Path), _registry);

    private static NetworkAdapter Adapter() =>
        new("Ethernet", 4, "IPv4", new[] { "10.0.0.1", "fd00::1" }, "Disabled", "Connected", 25, HasDefaultRoute: true);

    private static EffectiveDnsState State() =>
        new(4, "Ethernet", new[] { "10.0.0.1", "fd00::1" }, Array.Empty<DohServerEntry>(), 2, "policy.auto");

    [Fact]
    public void Capture_persists_a_restore_point_and_reads_it_back()
    {
        var backup = _store.Capture(Adapter(), State(), ProviderCatalog.Find("cloudflare")!, new[] { "1.1.1.1", "1.0.0.1" });

        var listed = Assert.Single(_store.List());
        Assert.Equal(backup.Id, listed.Id);
        Assert.Equal("Cloudflare / Ethernet", listed.Label);
        Assert.Equal(4, listed.InterfaceIndex);
        Assert.Equal(new[] { "10.0.0.1", "fd00::1" }, listed.Servers);
        Assert.False(listed.UseDhcp);
        Assert.Equal(2, listed.PreviousPolicy);
        Assert.Equal(new[] { "1.1.1.1", "1.0.0.1" }, listed.AddedDohAddresses);
        Assert.Equal("cloudflare", listed.ProfileId);

        Assert.Equal(backup.Id, _store.Load(backup.Id)!.Id);
        Assert.Null(_store.Load("does-not-exist"));
    }

    [Fact]
    public void Capture_is_mirrored_in_the_registry_as_the_specification_asks()
    {
        var backup = _store.Capture(Adapter(), State(), ProviderCatalog.Find("adguard")!, Array.Empty<string>());

        Assert.True(_registry.KeyExists(RegistryHive.CurrentUser, $@"{BackupStore.RegistryPath}\{backup.Id}"));
        Assert.NotNull(_registry.ReadString(RegistryHive.CurrentUser, $@"{BackupStore.RegistryPath}\{backup.Id}", "Json"));
        Assert.NotNull(_registry.ReadString(RegistryHive.CurrentUser, $@"{BackupStore.RegistryPath}\{backup.Id}", "CreatedAt"));
        Assert.True(File.Exists(Path.Combine(_root.Path, "backups", $"backup-{backup.Id}.json")));
    }

    [Fact]
    public void An_adapter_with_no_static_servers_is_restored_through_dhcp()
    {
        var dhcpAdapter = new NetworkAdapter("Wi-Fi", 11, "IPv4", Array.Empty<string>(), "Enabled", "Connected", 25, true);
        var emptyState = new EffectiveDnsState(11, "Wi-Fi", Array.Empty<string>(), Array.Empty<DohServerEntry>(), null, "policy.notConfigured");

        var backup = _store.Capture(dhcpAdapter, emptyState, ProviderCatalog.Find("quad9")!, Array.Empty<string>());

        Assert.True(backup.UseDhcp);
        Assert.Null(backup.PreviousPolicy);

        var plan = backup.ToPlan();
        Assert.Equal(11, plan.InterfaceIndex);
        Assert.True(plan.UseDhcp);
        Assert.Null(plan.PreviousPolicy);
    }

    [Fact]
    public void Export_and_import_move_a_restore_point_between_machines()
    {
        var backup = _store.Capture(Adapter(), State(), ProviderCatalog.Find("dnsforge")!, new[] { "49.12.67.122" });
        var file = Path.Combine(_root.Path, "portable.json");

        Assert.Equal(file, _store.Export(backup.Id, file));

        var otherRegistry = new FakeRegistry();
        var other = new BackupStore(new AppPaths(_root.Path), otherRegistry);
        other.Delete(backup.Id);

        var imported = other.Import(file);

        Assert.NotNull(imported);
        Assert.Equal(backup.Id, imported!.Id);
        Assert.Equal(backup.Servers, imported.Servers);
        Assert.Equal(backup.AddedDohAddresses, imported.AddedDohAddresses);
    }

    [Fact]
    public void Import_ignores_a_corrupt_file()
    {
        var file = Path.Combine(_root.Path, "broken.json");
        File.WriteAllText(file, "{ this is not json");

        Assert.Null(_store.Import(file));
        Assert.Null(_store.Import(Path.Combine(_root.Path, "missing.json")));
    }

    [Fact]
    public void List_recovers_a_backup_whose_file_was_deleted()
    {
        var backup = _store.Capture(Adapter(), State(), ProviderCatalog.Find("dnssb")!, Array.Empty<string>());
        File.Delete(Path.Combine(_root.Path, "backups", $"backup-{backup.Id}.json"));

        var recovered = Assert.Single(_store.List());
        Assert.Equal(backup.Id, recovered.Id);
        Assert.Equal(new[] { "10.0.0.1", "fd00::1" }, recovered.Servers);
    }

    [Fact]
    public void Delete_removes_the_file_of_a_restore_point()
    {
        var backup = _store.Capture(Adapter(), State(), ProviderCatalog.Find("nextdns")!, Array.Empty<string>());
        var path = Path.Combine(_root.Path, "backups", $"backup-{backup.Id}.json");
        Assert.True(File.Exists(path));

        // A store whose registry mirror is unknown to it must not reach for the real registry.
        new BackupStore(new AppPaths(_root.Path), new FakeRegistry()).Delete(backup.Id);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Listing_is_newest_first_and_labels_a_custom_profile()
    {
        var custom = ProviderCatalog.FromCustom("Labo interne", "https://dns.labo/dns-query", new[] { "10.20.30.1" });

        var first = _store.Capture(Adapter(), State(), custom, Array.Empty<string>());
        var second = _store.Capture(Adapter(), State(), custom, Array.Empty<string>());

        var listed = _store.List();

        Assert.Equal(2, listed.Count);
        Assert.True(listed[0].CreatedAt >= listed[1].CreatedAt);
        Assert.Equal(second.Id, listed[0].Id);
        Assert.Contains("Labo interne", listed[0].Label);
        Assert.Equal(first.ProfileId, second.ProfileId);
    }

    public void Dispose() => _root.Dispose();
}
