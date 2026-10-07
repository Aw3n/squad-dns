using Microsoft.Win32;
using SquadDns.Core.Io;
using SquadDns.Core.Models;
using SquadDns.Core.Settings;
using SquadDns.Core.Shell;

namespace SquadDns.Core.Network;

public sealed record DohServerEntry(string ServerAddress, string DohTemplate, bool AllowFallbackToUdp, bool AutoUpgrade);

public sealed record EffectiveDnsState(
    int InterfaceIndex,
    string InterfaceAlias,
    IReadOnlyList<string> Servers,
    IReadOnlyList<DohServerEntry> DohEntries,
    int? PolicyValue,
    string PolicyText)
{
    public bool EncryptedNow => PolicyValue == 3 || (PolicyValue is null && DohEntries.Count > 0 && Servers.Count > 0);
    public bool FallsBackToPlain => PolicyValue == 2 || (PolicyValue is null && DohEntries.Any(e => e.AllowFallbackToUdp));
}

public sealed class DnsConfigurator
{
    public const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient";
    public const string PolicyValueName = "DoHPolicy";
    public const string WellKnownServersKey = @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DohWellKnownServers";

    private readonly IShell _shell;
    private readonly IRegistryAccess _registry;
    private bool? _dohCapable;

    public DnsConfigurator(IShell shell, IRegistryAccess registry)
    {
        _shell = shell;
        _registry = registry;
    }

    public async Task<bool> SupportsDoHConfigurationAsync(CancellationToken ct = default)
    {
        if (_dohCapable.HasValue)
        {
            return _dohCapable.Value;
        }

        var probe = await _shell.RunAsync(Shell.PowerShellShell.DohCmdletProbeScript, ct);
        _dohCapable = probe.StdOut.Contains("yes", StringComparison.OrdinalIgnoreCase);
        return _dohCapable.Value;
    }

    // « DoH exige » ne promet aucun repli en clair : si l'enregistrement DoH a echoue, ecrire quand meme
    // les serveurs et la politique laisserait le poste non chiffe en affirmant le contraire.
    public static bool AbortsAfterDohFailure(DnsSecurityMode mode, IReadOnlyList<ApplyStep> steps) =>
        mode == DnsSecurityMode.EncryptedOnly && steps.Any(s => s.Executed && !s.Success);

    public static int PolicyValueFor(DnsSecurityMode mode) => mode switch
    {
        DnsSecurityMode.EncryptedOnly => 3,
        DnsSecurityMode.EncryptedPreferred => 2,
        _ => 1
    };

    public static string PolicyTextFor(int? value) => value switch
    {
        3 => "policy.require",
        2 => "policy.auto",
        1 => "policy.disabled",
        _ => "policy.notConfigured"
    };

    public async Task<int?> ReadPolicyAsync(CancellationToken ct = default)
    {
        var result = await _shell.RunAsync(
            $"(Get-ItemProperty -Path 'HKLM:\\{PolicyKey}' -ErrorAction SilentlyContinue).{PolicyValueName}",
            ct);

        var text = result.StdOut.Trim();
        return int.TryParse(text, out var value) ? value : _registry.ReadDword(RegistryHive.LocalMachine, PolicyKey, PolicyValueName);
    }

    public async Task<IReadOnlyList<DohServerEntry>> ReadDohEntriesAsync(CancellationToken ct = default)
    {
        // Tout le pipeline sur une seule ligne : PowerShell clot une instruction pipeline des qu'une ligne
        // commence par « | » (« Un élément de canal vide n'est pas autorisé », ParserError). En multi-ligne,
        // cette lecture echouait silencieusement, l'appli ne voyait jamais les entrees DoH existantes,
        // choisissait toujours Add- (qui echoue si l'entree existe deja) et ne pouvait jamais se verifier.
        var script = "@(Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue | " +
                     "ForEach-Object { [pscustomobject]@{ ServerAddress=[string]$_.Name; DohTemplate=[string]$_.DohTemplate; " +
                     "AllowFallbackToUdp=[bool]$_.AllowFallbackToUdp; AutoUpgrade=[bool]$_.AutoUpgrade } }) " +
                     "| ConvertTo-Json -Compress -Depth 3";

        var result = await _shell.RunAsync(script, ct);
        return ParseDoh(result.StdOut);
    }

    private const string ReadServersScript = """
        $row = Get-DnsClientServerAddress -InterfaceIndex __INDEX__ -ErrorAction SilentlyContinue | Select-Object -First 1
        $servers = if ($row) { @($row.ServerAddresses) } else { @() }
        [pscustomobject]@{ Servers = $servers } | ConvertTo-Json -Compress -Depth 3
        """;

    public async Task<EffectiveDnsState> ReadStateAsync(int interfaceIndex, string interfaceAlias, CancellationToken ct = default)
    {
        var script = ReadServersScript.Replace("__INDEX__", interfaceIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var result = await _shell.RunAsync(script, ct);
        var servers = ParseServers(result.StdOut);
        var doh = await ReadDohEntriesAsync(ct);
        var policy = await ReadPolicyAsync(ct);

        var relevant = doh
            .Where(e => servers.Contains(e.ServerAddress, StringComparer.OrdinalIgnoreCase))
            .ToList();

        return new EffectiveDnsState(
            interfaceIndex,
            interfaceAlias,
            servers,
            relevant,
            policy,
            PolicyTextFor(policy));
    }

    public async Task<ApplyResult> ApplyAsync(
        DnsProfile profile,
        int interfaceIndex,
        string interfaceAlias,
        DnsSecurityMode mode,
        bool dryRun = false,
        Func<DnsProfile, CancellationToken, Task<bool>>? confirmEncryptionAsync = null,
        CancellationToken ct = default)
    {
        var steps = new List<ApplyStep>();

        if (profile.ResolverAddresses.Count == 0)
        {
            return new ApplyResult(ApplyStatus.Failed, "apply.noServers", steps, interfaceAlias);
        }

        // Windows 10 et Windows 11 avant 22H2 n'ont aucun client DoH/DoT natif : la lecture
        // revient vide, le plan choisit Add-, et la politique DoHPolicy y est inerte. « DoH
        // exige » reste un refus net. « Chiffre de preference » accepte le repli, donc on
        // applique les serveurs en clair sous politique desactivee en le disant dans le bilan.
        var encryptionUnavailable = mode != DnsSecurityMode.Unencrypted && !await SupportsDoHConfigurationAsync(ct);
        if (encryptionUnavailable && mode == DnsSecurityMode.EncryptedOnly)
        {
            return new ApplyResult(ApplyStatus.Failed, "apply.dohUnsupported", steps, interfaceAlias);
        }

        if (encryptionUnavailable)
        {
            mode = DnsSecurityMode.Unencrypted;
        }

        if (!dryRun && !Elevation.IsElevated())
        {
            return new ApplyResult(ApplyStatus.NeedsElevation, "apply.needsElevation", steps, interfaceAlias);
        }

        var addresses = profile.ResolverAddresses.Take(2).ToArray();
        var allowFallback = mode != DnsSecurityMode.EncryptedOnly;

        if (mode != DnsSecurityMode.Unencrypted)
        {
            var existing = await ReadDohEntriesAsync(ct);

            foreach (var address in addresses)
            {
                var known = existing.Any(e => string.Equals(e.ServerAddress, address, StringComparison.OrdinalIgnoreCase));
                var verb = known ? "Set-DnsClientDohServerAddress" : "Add-DnsClientDohServerAddress";

                var script = $"{verb} -ServerAddress {PowerShellShell.Quote(address)} -DohTemplate {PowerShellShell.Quote(profile.DoHTemplate)} " +
                             $"-AllowFallbackToUdp ${(allowFallback ? "True" : "False")} -AutoUpgrade $True";

                steps.Add(await RunStepAsync("registerDoh", script, dryRun, ct));
            }

            if (AbortsAfterDohFailure(mode, steps))
            {
                return new ApplyResult(ApplyStatus.Failed, "apply.dohRegistrationFailed", steps, interfaceAlias);
            }
        }

        var setServers = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses {PowerShellShell.QuoteArray(addresses)}";
        steps.Add(await RunStepAsync("setServers", setServers, dryRun, ct));

        var policy = PolicyValueFor(mode);
        var setPolicy = $"New-ItemProperty -Path 'HKLM:\\{PolicyKey}' -Name {PolicyValueName} -PropertyType DWord -Value {policy} -Force | Out-Null";
        steps.Add(await RunStepAsync("setPolicy", setPolicy, dryRun, ct));

        steps.Add(await RunStepAsync("flushCache", "Clear-DnsClientCache", dryRun, ct));

        if (dryRun)
        {
            return new ApplyResult(ApplyStatus.DryRun, "apply.dryRun", steps, interfaceAlias);
        }

        var verification = await VerifyAsync(profile, interfaceIndex, addresses, policy, ct);
        var failed = steps.Count(s => !s.Success);

        // Sans sonde fournie, on reste sur la coherence du registre : c'est ce que verifiait l'ancien code.
        var encryptionResponds = mode == DnsSecurityMode.Unencrypted ||
                                 confirmEncryptionAsync is null ||
                                 await confirmEncryptionAsync(profile, ct);

        var (status, summaryKey) = DecideOutcome(failed, verification, encryptionResponds, encryptionUnavailable);
        return new ApplyResult(status, summaryKey, steps, interfaceAlias);
    }

    // Le controle de coherence ne compare que des textes : un modele DoH enregistre mais mort
    // (Quad9 :5053, Verisign dns64) passait pour « applique et verifie ». L'ordre est volontaire :
    // une commande qui echoue prime, puis l'etat incoherent, puis le point qui ne repond pas.
    // encryptionUnavailable = repli volontaire sur Windows sans client DoH : applique en clair,
    // jamais annonce comme un succes chiffre.
    public static (ApplyStatus Status, string SummaryKey) DecideOutcome(int failedSteps, bool stateMatches, bool encryptionResponds, bool encryptionUnavailable = false) =>
        failedSteps > 0 ? (ApplyStatus.Failed, "apply.failed")
        : !stateMatches ? (ApplyStatus.PartialSuccess, "apply.verifyMismatch")
        : encryptionUnavailable ? (ApplyStatus.PartialSuccess, "apply.plainFallback")
        : !encryptionResponds ? (ApplyStatus.PartialSuccess, "apply.dohUnreachable")
        : (ApplyStatus.Success, "apply.success");

    public static (ApplyStatus Status, string SummaryKey) DecideRestoreOutcome(int failedSteps, bool stateMatches) =>
        failedSteps > 0 ? (ApplyStatus.Failed, "apply.failed")
        : !stateMatches ? (ApplyStatus.PartialSuccess, "apply.verifyMismatch")
        : (ApplyStatus.Success, "restore.success");

    public async Task<ApplyResult> RestoreAsync(DnsRestorePlan plan, bool dryRun = false, CancellationToken ct = default)
    {
        var steps = new List<ApplyStep>();

        if (!dryRun && !Elevation.IsElevated())
        {
            return new ApplyResult(ApplyStatus.NeedsElevation, "apply.needsElevation", steps, plan.InterfaceAlias);
        }

        if (plan.UseDhcp)
        {
            steps.Add(await RunStepAsync("resetDhcp",
                $"Set-DnsClientServerAddress -InterfaceIndex {plan.InterfaceIndex} -ResetServerAddresses", dryRun, ct));
        }
        else
        {
            steps.Add(await RunStepAsync("setServers",
                $"Set-DnsClientServerAddress -InterfaceIndex {plan.InterfaceIndex} -ServerAddresses {PowerShellShell.QuoteArray(plan.Servers)}", dryRun, ct));
        }

        var policyScript = plan.PreviousPolicy is null
            ? $"Remove-ItemProperty -Path 'HKLM:\\{PolicyKey}' -Name {PolicyValueName} -ErrorAction SilentlyContinue"
            : $"New-ItemProperty -Path 'HKLM:\\{PolicyKey}' -Name {PolicyValueName} -PropertyType DWord -Value {plan.PreviousPolicy.Value} -Force | Out-Null";

        steps.Add(await RunStepAsync("restorePolicy", policyScript, dryRun, ct));

        // Windows 10 n'a pas les applets DoH : rien n'a pu etre enregistre par cette application,
        // donc la suppression est un CommandNotFound garanti que -ErrorAction ne supprime pas.
        // Sans ce garde, chaque restauration echouait la-bas (regression de l'ajout removeDoh).
        if (await SupportsDoHConfigurationAsync(ct))
        {
            foreach (var address in plan.AddedDohAddresses.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                steps.Add(await RunStepAsync("removeDoh",
                    $"Remove-DnsClientDohServerAddress -ServerAddress {PowerShellShell.Quote(address)} -ErrorAction SilentlyContinue", dryRun, ct));
            }
        }

        steps.Add(await RunStepAsync("flushCache", "Clear-DnsClientCache", dryRun, ct));

        if (dryRun)
        {
            return new ApplyResult(ApplyStatus.DryRun, "apply.dryRun", steps, plan.InterfaceAlias);
        }

        var stateMatches = await RestoreMatchesAsync(plan, ct);
        var (status, summaryKey) = DecideRestoreOutcome(steps.Count(s => !s.Success), stateMatches);
        return new ApplyResult(status, summaryKey, steps, plan.InterfaceAlias);
    }

    private async Task<bool> VerifyAsync(DnsProfile profile, int interfaceIndex, string[] expectedServers, int expectedPolicy, CancellationToken ct)
    {
        var state = await ReadStateAsync(interfaceIndex, profile.Name, ct);
        var serversOk = expectedServers.All(a => state.Servers.Contains(a, StringComparer.OrdinalIgnoreCase));
        var policyOk = state.PolicyValue == expectedPolicy;

        if (expectedPolicy == 1)
        {
            return serversOk && policyOk;
        }

        var dohOk = expectedServers.All(a => state.DohEntries.Any(e =>
            string.Equals(e.ServerAddress, a, StringComparison.OrdinalIgnoreCase)
            && e.DohTemplate.Contains(HostOf(profile.DoHTemplate), StringComparison.OrdinalIgnoreCase)));

        return serversOk && policyOk && dohOk;
    }

    private async Task<bool> RestoreMatchesAsync(DnsRestorePlan plan, CancellationToken ct)
    {
        var state = await ReadStateAsync(plan.InterfaceIndex, plan.InterfaceAlias, ct);
        var allDohEntries = await ReadDohEntriesAsync(ct);
        var policyOk = state.PolicyValue == plan.PreviousPolicy;
        var serversOk = plan.UseDhcp || plan.Servers.All(address =>
            state.Servers.Contains(address, StringComparer.OrdinalIgnoreCase));
        var removedDoh = plan.AddedDohAddresses.All(address =>
            allDohEntries.All(entry => !string.Equals(entry.ServerAddress, address, StringComparison.OrdinalIgnoreCase)));

        return policyOk && serversOk && removedDoh;
    }

    private static string HostOf(string template)
    {
        if (Uri.TryCreate(template, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
        {
            return uri.Host;
        }

        return template;
    }

    private async Task<ApplyStep> RunStepAsync(string name, string script, bool dryRun, CancellationToken ct)
    {
        if (dryRun)
        {
            return new ApplyStep(name, script, Executed: false, Success: true, Error: null);
        }

        var result = await _shell.RunAsync(script, ct);
        return new ApplyStep(name, script, Executed: true, result.Success, result.Success ? null : FirstLine(result.StdErr));
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "unknown error";
        }

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[0].Trim() : text.Trim();
    }

    private static IReadOnlyList<string> ParseServers(string payload)
    {
        payload = payload.Trim();
        var list = new List<string>();
        if (!payload.StartsWith('{'))
        {
            return list;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            if (document.RootElement.TryGetProperty("Servers", out var servers))
            {
                if (servers.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var value in servers.EnumerateArray())
                    {
                        var text = value.GetString();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            list.Add(text);
                        }
                    }
                }
                else if (servers.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    list.Add(servers.GetString()!);
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // ignore
        }

        return list;
    }

    private static IReadOnlyList<DohServerEntry> ParseDoh(string payload)
    {
        payload = payload.Trim();
        var list = new List<DohServerEntry>();
        if (payload.Length == 0 || (!payload.StartsWith('[') && !payload.StartsWith('{')))
        {
            return list;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(payload);
            var root = document.RootElement;

            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var element in root.EnumerateArray())
                {
                    AddEntry(list, element);
                }
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                AddEntry(list, root);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // ignore
        }

        return list;
    }

    private static void AddEntry(List<DohServerEntry> list, System.Text.Json.JsonElement element)
    {
        var address = element.TryGetProperty("ServerAddress", out var a) ? a.GetString() : null;
        var template = element.TryGetProperty("DohTemplate", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(address) || string.IsNullOrWhiteSpace(template))
        {
            return;
        }

        list.Add(new DohServerEntry(
            address,
            template,
            element.TryGetProperty("AllowFallbackToUdp", out var f) && f.ValueKind == System.Text.Json.JsonValueKind.True,
            element.TryGetProperty("AutoUpgrade", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.True));
    }
}

public sealed record DnsRestorePlan(int InterfaceIndex, string InterfaceAlias, IReadOnlyList<string> Servers, bool UseDhcp, int? PreviousPolicy, IReadOnlyList<string> AddedDohAddresses);
