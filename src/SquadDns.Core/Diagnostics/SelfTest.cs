using System.Text;
using System.Text.Json;
using SquadDns.Core.Settings;

namespace SquadDns.Core.Diagnostics;

public sealed class AppLog
{
    private static readonly object Gate = new();
    private readonly string _directory;

    public AppLog(AppPaths paths) => _directory = paths.Logs;

    public void Write(string category, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}";
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"squadns-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // logging must never break the app
        }
        catch (UnauthorizedAccessException)
        {
            // logging must never break the app
        }
    }

    public string LatestFilePath() => Path.Combine(_directory, $"squadns-{DateTime.Now:yyyyMMdd}.log");
}

public sealed record SelfTestReport(
    string GeneratedAt,
    string ApplicationVersion,
    string OsLine,
    bool Elevated,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<string> Catalog,
    IReadOnlyList<string> ActiveState,
    IReadOnlyList<string> PlannedCommands,
    IReadOnlyList<string> Latency,
    IReadOnlyList<string> Warnings);

public sealed class SelfTest
{
    private readonly AppPaths _paths;
    private readonly Io.IRegistryAccess _registry;
    private readonly Shell.IShell _shell;

    public SelfTest(AppPaths paths, Io.IRegistryAccess registry, Shell.IShell shell)
    {
        _paths = paths;
        _registry = registry;
        _shell = shell;
    }

    public async Task<SelfTestReport> RunAsync(string domain, int samples, bool probeLatency, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var registry = _registry;
        var shell = _shell;
        var browser = new Network.InterfaceBrowser(shell);
        var configurator = new Network.DnsConfigurator(shell, registry);
        var catalog = Catalog.ProviderCatalog.BuiltIn;

        var os = await Shell.OsInfo.DetectAsync(registry, shell, ct);
        var interfaces = await browser.ListAsync(ct);
        var target = interfaces.FirstOrDefault(i => i.Family == "IPv4" && i.HasDefaultRoute && i.IsConnected)
            ?? interfaces.FirstOrDefault(i => i.Family == "IPv4")
            ?? interfaces.FirstOrDefault();

        var activeState = new List<string>();
        var planned = new List<string>();

        if (target is not null)
        {
            var state = await configurator.ReadStateAsync(target.Index, target.Alias, ct);
            activeState.Add($"interface={target.Alias}#{target.Index}");
            activeState.Add($"servers=[{string.Join(", ", state.Servers)}]");
            activeState.Add($"dohPolicy={state.PolicyText}");
            foreach (var entry in state.DohEntries)
            {
                activeState.Add($"dohEntry={entry.ServerAddress} -> {entry.DohTemplate} (fallback={(entry.AllowFallbackToUdp ? "yes" : "no")})");
            }

            var profile = catalog[0];
            var result = await configurator.ApplyAsync(profile, target.Index, target.Alias, Models.DnsSecurityMode.EncryptedPreferred, dryRun: true, ct: ct);
            foreach (var step in result.Steps)
            {
                planned.Add($"{step.Name}: {step.Command}");
            }

            warnings.Add($"elevated={Elevation.IsElevated()} (dry-run only in selftest)");
        }
        else
        {
            warnings.Add("no network adapter found");
        }

        var latency = new List<string>();
        if (probeLatency)
        {
            var tester = new Testing.LatencyTester();
            foreach (var profile in catalog)
            {
                try
                {
                    var result = await tester.RunAsync(profile, domain, samples, includeTransportTests: true, ct: ct);
                    latency.Add($"{profile.Name}: doH={(result.MedianDoH?.ToString() ?? "n/a")}ms doT={(result.MedianDoT?.ToString() ?? "n/a")}ms udp={(result.MedianPlain?.ToString() ?? "n/a")}ms ok={result.SuccessCount}/{result.TotalCount} note={result.Note ?? "-"} sample={result.Samples.FirstOrDefault()?.Detail ?? "-"}");
                }
                catch (Exception ex)
                {
                    latency.Add($"{profile.Name}: error {ex.GetType().Name}");
                }
            }
        }

        var report = new SelfTestReport(
            DateTimeOffset.Now.ToString("o"),
            Updates.AppInfo.Version,
            os.FriendlyName,
            Elevation.IsElevated(),
            interfaces.Select(i => $"{i.Alias}#{i.Index} [{i.Family}] dhcp={i.Dhcp} state={i.State} route={i.HasDefaultRoute} dns=[{string.Join(",", i.Servers)}]").ToList(),
            catalog.Select(p => $"{p.SortOrder}. {p.Name} doh={p.DoHTemplate} dot={p.DoTServer}:{p.DoTPort} ips=[{string.Join(",", p.ResolverAddresses)}]").ToList(),
            activeState,
            planned,
            latency,
            warnings);

        var file = Path.Combine(_paths.Logs, $"selftest-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(file, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        }));

        return report;
    }
}
