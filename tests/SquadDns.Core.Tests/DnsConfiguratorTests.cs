using SquadDns.Core.Catalog;
using SquadDns.Core.Models;
using SquadDns.Core.Network;

namespace SquadDns.Core.Tests;

public class DnsConfiguratorTests
{
    private const string NoDohEntries = "[]";

    private static readonly string[] MutatingVerbs = { "Set-", "Add-", "New-", "Clear-", "Remove-" };

    private static DnsProfile Cloudflare => ProviderCatalog.Find("cloudflare")!;

    private static void AssertNoMutatingScript(IEnumerable<string> scripts)
    {
        foreach (var script in scripts)
        {
            Assert.False(
                MutatingVerbs.Any(verb => script.StartsWith(verb, StringComparison.Ordinal)),
                $"une commande mutante a ete executee : {script}");
        }
    }

    [Fact]
    public async Task Preview_plans_the_documented_commands_without_running_any_of_them()
    {
        var shell = new FakeShell { DefaultOutput = NoDohEntries };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.EncryptedPreferred, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.Equal("apply.dryRun", result.SummaryKey);
        Assert.Equal(
            new[] { "registerDoh", "registerDoh", "setServers", "setPolicy", "flushCache" },
            result.Steps.Select(s => s.Name));
        Assert.All(result.Steps, step => Assert.False(step.Executed));

        Assert.Equal(
            "Add-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -DohTemplate 'https://cloudflare-dns.com/dns-query' -AllowFallbackToUdp $True -AutoUpgrade $True",
            result.Steps[0].Command);
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 4 -ServerAddresses ('1.1.1.1','1.0.0.1')", result.Steps[2].Command);
        Assert.Contains("-Name DoHPolicy -PropertyType DWord -Value 2 -Force", result.Steps[3].Command, StringComparison.Ordinal);
        Assert.Equal("Clear-DnsClientCache", result.Steps[4].Command);

        // La sonde de capacite precede la lecture des entrees DoH : deux appels, aucun mutatif.
        Assert.Equal(2, shell.Scripts.Count);
        AssertNoMutatingScript(shell.Scripts);
    }

    [Fact]
    public async Task EncryptedOnly_forbids_the_plain_fallback_and_forces_the_policy()
    {
        var configurator = new DnsConfigurator(new FakeShell { DefaultOutput = NoDohEntries }, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 7, "Wi-Fi", DnsSecurityMode.EncryptedOnly, dryRun: true);

        Assert.Contains("-AllowFallbackToUdp $False", result.Steps[0].Command, StringComparison.Ordinal);
        Assert.Contains("-Value 3", result.Steps.Single(s => s.Name == "setPolicy").Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unencrypted_skips_the_DoH_registration_steps()
    {
        var shell = new FakeShell();
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.Unencrypted, dryRun: true);

        Assert.Equal(new[] { "setServers", "setPolicy", "flushCache" }, result.Steps.Select(s => s.Name));
        Assert.Contains("-Value 1", result.Steps.Single(s => s.Name == "setPolicy").Command, StringComparison.Ordinal);
        Assert.Empty(shell.Scripts);
    }

    [Fact]
    public async Task A_known_server_is_updated_instead_of_being_added()
    {
        var shell = new FakeShell
        {
            DefaultOutput = """[{"ServerAddress":"1.1.1.1","DohTemplate":"https://old.example/dns-query","AllowFallbackToUdp":true,"AutoUpgrade":true}]"""
        };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.EncryptedPreferred, dryRun: true);

        var commands = result.Steps.Where(s => s.Name == "registerDoh").Select(s => s.Command).ToList();
        Assert.StartsWith("Set-DnsClientDohServerAddress", commands[0]);
        Assert.StartsWith("Add-DnsClientDohServerAddress", commands[1]);
    }

    [Fact]
    public async Task A_profile_without_resolver_addresses_fails_before_touching_the_system()
    {
        var shell = new FakeShell();
        var configurator = new DnsConfigurator(shell, new FakeRegistry());
        var broken = ProviderCatalog.FromCustom("Vide", "https://x.example/dns-query", Array.Empty<string>());

        var result = await configurator.ApplyAsync(broken, 4, "Ethernet", DnsSecurityMode.EncryptedPreferred, dryRun: true);

        Assert.Equal(ApplyStatus.Failed, result.Status);
        Assert.Equal("apply.noServers", result.SummaryKey);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public async Task ReadState_combines_servers_well_known_entries_and_the_group_policy()
    {
        var shell = new FakeShell();
        shell.Enqueue("""{"Servers":["1.1.1.1","1.0.0.1"]}""");
        shell.Enqueue("""
            [{"ServerAddress":"1.1.1.1","DohTemplate":"https://cloudflare-dns.com/dns-query","AllowFallbackToUdp":false,"AutoUpgrade":true},
             {"ServerAddress":"8.8.8.8","DohTemplate":"https://dns.google/dns-query","AllowFallbackToUdp":true,"AutoUpgrade":true}]
            """);
        shell.Enqueue("3");

        var state = await new DnsConfigurator(shell, new FakeRegistry()).ReadStateAsync(4, "Ethernet");

        Assert.Equal(new[] { "1.1.1.1", "1.0.0.1" }, state.Servers);
        Assert.Equal(3, state.PolicyValue);
        Assert.Equal("policy.require", state.PolicyText);
        Assert.True(state.EncryptedNow);
        Assert.False(state.FallsBackToPlain);
        Assert.Single(state.DohEntries);
        Assert.Equal("1.1.1.1", state.DohEntries[0].ServerAddress);
    }

    [Fact]
    public async Task ReadState_reports_not_configured_when_the_policy_value_is_absent()
    {
        var shell = new FakeShell();
        shell.Enqueue("{\"Servers\":[]}");
        shell.Enqueue("[]");
        shell.Enqueue(string.Empty);

        var state = await new DnsConfigurator(shell, new FakeRegistry()).ReadStateAsync(11, "Ethernet 3");

        Assert.Empty(state.Servers);
        Assert.Null(state.PolicyValue);
        Assert.Equal("policy.notConfigured", state.PolicyText);
        Assert.False(state.EncryptedNow);
    }

    [Fact]
    public async Task Restore_puts_back_dhcp_and_removes_the_policy_when_there_was_none()
    {
        var shell = new FakeShell();
        var configurator = new DnsConfigurator(shell, new FakeRegistry());
        var plan = new DnsRestorePlan(4, "Ethernet", new[] { "10.0.0.1" }, UseDhcp: true, PreviousPolicy: null, new[] { "1.1.1.1" });

        var result = await configurator.RestoreAsync(plan, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.Equal(new[] { "resetDhcp", "restorePolicy", "removeDoh", "flushCache" }, result.Steps.Select(s => s.Name));
        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 4 -ResetServerAddresses", result.Steps[0].Command);
        Assert.Contains("Remove-ItemProperty", result.Steps[1].Command, StringComparison.Ordinal);
        Assert.Equal("Remove-DnsClientDohServerAddress -ServerAddress '1.1.1.1' -ErrorAction SilentlyContinue", result.Steps[2].Command);
    }

    [Fact]
    public void Restore_outcome_never_claims_success_after_a_failed_step_or_mismatch()
    {
        Assert.Equal((ApplyStatus.Success, "restore.success"), DnsConfigurator.DecideRestoreOutcome(0, true));
        Assert.Equal((ApplyStatus.PartialSuccess, "apply.verifyMismatch"), DnsConfigurator.DecideRestoreOutcome(0, false));
        Assert.Equal((ApplyStatus.Failed, "apply.failed"), DnsConfigurator.DecideRestoreOutcome(1, true));
    }

    [Fact]
    public async Task Restore_reinstates_a_static_configuration_and_the_previous_policy()
    {
        var plan = new DnsRestorePlan(9, "Wi-Fi", new[] { "192.168.1.1", "fd12::1" }, UseDhcp: false, PreviousPolicy: 1, Array.Empty<string>());

        var result = await new DnsConfigurator(new FakeShell(), new FakeRegistry()).RestoreAsync(plan, dryRun: true);

        Assert.Equal("Set-DnsClientServerAddress -InterfaceIndex 9 -ServerAddresses ('192.168.1.1','fd12::1')",
            result.Steps.Single(s => s.Name == "setServers").Command);
        Assert.Contains("-Value 1", result.Steps.Single(s => s.Name == "restorePolicy").Command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Applying_for_real_refuses_to_run_without_elevation()
    {
        // The harness account is not elevated; if that ever changes this guard still proves
        // the check happens before a single mutating command is produced.
        if (Core.Settings.Elevation.IsElevated())
        {
            return;
        }

        var shell = new FakeShell();
        var result = await new DnsConfigurator(shell, new FakeRegistry())
            .ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.EncryptedPreferred, dryRun: false);

        Assert.Equal(ApplyStatus.NeedsElevation, result.Status);
        Assert.Equal("apply.needsElevation", result.SummaryKey);
        Assert.Empty(result.Steps);
        AssertNoMutatingScript(shell.Scripts);
    }

    [Fact]
    public async Task Restore_skips_the_doh_removal_when_the_cmdlets_are_missing()
    {
        // Windows 10 : la sauvegarde peut contenir des adresses DoH (cf. regression restauration
        // en CommandNotFound sur build 18363). Rien n'ayant pu etre enregistre, la restauration
        // ne doit planifier aucune applet DoH.
        var shell = new FakeShell { CapabilityProbeOutput = "no" };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());
        var plan = new DnsRestorePlan(4, "Ethernet", new[] { "10.0.0.1" }, UseDhcp: true, PreviousPolicy: null, new[] { "1.1.1.1" });

        var result = await configurator.RestoreAsync(plan, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.Equal(new[] { "resetDhcp", "restorePolicy", "flushCache" }, result.Steps.Select(s => s.Name));
        AssertNoMutatingScript(shell.Scripts);
    }

    [Fact]
    public async Task Unencrypted_still_applies_where_the_doh_cmdlets_are_missing()
    {
        var shell = new FakeShell { CapabilityProbeOutput = "no" };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.Unencrypted, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.Equal(new[] { "setServers", "setPolicy", "flushCache" }, result.Steps.Select(s => s.Name));
        Assert.Empty(shell.Scripts);
    }

    [Fact]
    public async Task Encrypted_preferred_uses_the_local_proxy_when_the_cmdlets_are_missing()
    {
        // Windows 10 : sans applets DoH, l'application apporte le chiffrement elle-meme
        // via le proxy local 127.0.0.1. Les serveurs systeme pointent vers la boucle
        // locale, la politique DoH native reste desactivee (valeur 1) pour que Windows
        // ne pretende jamais chiffrer, et aucune applet DoH ne doit apparaitre.
        var shell = new FakeShell { CapabilityProbeOutput = "no" };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.EncryptedPreferred, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.True(result.LocalProxy);
        Assert.Equal(new[] { "setServers", "setPolicy", "flushCache" }, result.Steps.Select(s => s.Name));
        Assert.Contains("127.0.0.1", result.Steps.Single(s => s.Name == "setServers").Command, StringComparison.Ordinal);
        Assert.Contains("-Value 1", result.Steps.Single(s => s.Name == "setPolicy").Command, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Steps, s => s.Name == "registerDoh");
        AssertNoMutatingScript(shell.Scripts);
    }

    [Fact]
    public async Task Encrypted_only_also_uses_the_local_proxy_when_the_cmdlets_are_missing()
    {
        // Windows 10 : « DoH exige » n'est plus un refus — le proxy local fournit le
        // chiffrement, donc le plan est identique au mode « chiffre de preference ».
        var shell = new FakeShell { CapabilityProbeOutput = "no", DefaultOutput = NoDohEntries };
        var configurator = new DnsConfigurator(shell, new FakeRegistry());

        var result = await configurator.ApplyAsync(Cloudflare, 4, "Ethernet", DnsSecurityMode.EncryptedOnly, dryRun: true);

        Assert.Equal(ApplyStatus.DryRun, result.Status);
        Assert.True(result.LocalProxy);
        Assert.Equal(new[] { "setServers", "setPolicy", "flushCache" }, result.Steps.Select(s => s.Name));
        Assert.Contains("127.0.0.1", result.Steps.Single(s => s.Name == "setServers").Command, StringComparison.Ordinal);
        AssertNoMutatingScript(shell.Scripts);
    }

    [Fact]
    public void Outcome_never_claims_success_when_the_doh_endpoint_does_not_answer()
    {
        Assert.Equal((ApplyStatus.Success, "apply.success"), DnsConfigurator.DecideOutcome(0, true, true));
        Assert.Equal((ApplyStatus.PartialSuccess, "apply.dohUnreachable"), DnsConfigurator.DecideOutcome(0, true, false));
        Assert.Equal((ApplyStatus.PartialSuccess, "apply.verifyMismatch"), DnsConfigurator.DecideOutcome(0, false, true));
        Assert.Equal((ApplyStatus.Failed, "apply.failed"), DnsConfigurator.DecideOutcome(2, true, true));
    }

    [Fact]
    public void Outcome_reports_the_local_proxy_when_it_brings_the_encryption()
    {
        Assert.Equal((ApplyStatus.Success, "apply.localProxy"), DnsConfigurator.DecideOutcome(0, true, true, localProxy: true));
        Assert.Equal((ApplyStatus.Failed, "apply.failed"), DnsConfigurator.DecideOutcome(1, true, true, localProxy: true));
        Assert.Equal((ApplyStatus.PartialSuccess, "apply.verifyMismatch"), DnsConfigurator.DecideOutcome(0, false, true, localProxy: true));
        Assert.Equal((ApplyStatus.PartialSuccess, "apply.dohUnreachable"), DnsConfigurator.DecideOutcome(0, true, false, localProxy: true));
    }

    [Fact]
    public void Proxy_mode_defers_the_doh_probe_to_the_interface()
    {
        // En mode proxy, la sonde DoH de l'apply tournerait contre un resolveur systeme deja
        // pointe vers 127.0.0.1 sans relais : c'est l'interface qui sonde a travers le proxy.
        Assert.False(DnsConfigurator.NeedsEncryptionProbe(DnsSecurityMode.EncryptedPreferred, localProxy: true));
        Assert.False(DnsConfigurator.NeedsEncryptionProbe(DnsSecurityMode.EncryptedOnly, localProxy: true));
        Assert.False(DnsConfigurator.NeedsEncryptionProbe(DnsSecurityMode.Unencrypted, localProxy: false));
        Assert.True(DnsConfigurator.NeedsEncryptionProbe(DnsSecurityMode.EncryptedPreferred, localProxy: false));
        Assert.True(DnsConfigurator.NeedsEncryptionProbe(DnsSecurityMode.EncryptedOnly, localProxy: false));
    }

    [Fact]
    public void EncryptedOnly_never_writes_servers_after_a_failed_doh_registration()
    {
        var failed = new ApplyStep("registerDoh", "Add-DnsClientDohServerAddress ...", Executed: true, Success: false, Error: "not recognized");
        var ok = new ApplyStep("registerDoh", "Set-DnsClientDohServerAddress ...", Executed: true, Success: true, Error: null);
        var planned = new ApplyStep("registerDoh", "Set-DnsClientDohServerAddress ...", Executed: false, Success: true, Error: null);

        Assert.True(DnsConfigurator.AbortsAfterDohFailure(DnsSecurityMode.EncryptedOnly, new[] { failed, ok }));
        Assert.False(DnsConfigurator.AbortsAfterDohFailure(DnsSecurityMode.EncryptedPreferred, new[] { failed }));
        Assert.False(DnsConfigurator.AbortsAfterDohFailure(DnsSecurityMode.EncryptedOnly, new[] { ok }));
        Assert.False(DnsConfigurator.AbortsAfterDohFailure(DnsSecurityMode.EncryptedOnly, new[] { planned }));
    }
}
