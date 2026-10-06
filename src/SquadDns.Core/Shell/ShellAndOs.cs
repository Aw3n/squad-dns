using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SquadDns.Core.Io;

namespace SquadDns.Core.Shell;

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;

    public T? Json<T>()
    {
        var payload = StdOut.Trim();
        if (payload.Length == 0)
        {
            return default;
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ReadCommentHandling = JsonCommentHandling.Skip;
        options.AllowTrailingCommas = true;

        try
        {
            return JsonSerializer.Deserialize<T>(payload, options);
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

public interface IShell
{
    Task<ShellResult> RunAsync(string script, CancellationToken ct = default);
}

public sealed class PowerShellShell : IShell
{
    private const string Prefix = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; ";
    private readonly IProcessRunner _runner;
    private readonly TimeSpan _timeout;

    public PowerShellShell(IProcessRunner runner, TimeSpan? timeout = null)
    {
        _runner = runner;
        _timeout = timeout ?? TimeSpan.FromSeconds(75);
    }

    public async Task<ShellResult> RunAsync(string script, CancellationToken ct = default)
    {
        var wrapped = string.Concat(
            Prefix,
            "\ntry {\n",
            script,
            "\nexit 0\n} catch {\n[Console]::Error.WriteLine($_.Exception.Message)\nexit 1\n}\n");

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var result = await _runner.RunAsync(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded },
            _timeout,
            ct);

        return new ShellResult(result.ExitCode, result.StdOut, result.StdErr);
    }

    public static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    public static string QuoteArray(IEnumerable<string> values) =>
        "(" + string.Join(",", values.Select(Quote).ToArray()) + ")";
}

public sealed record OsInfo(
    string ProductName,
    string BuildString,
    int Build,
    string? DisplayVersion,
    bool IsWindows11,
    bool IsServer,
    bool DohCmdletsAvailable,
    bool DotSupportedBySystemResolver)
{
    public string FriendlyName
    {
        get
        {
            var version = IsServer ? "Windows Server" : IsWindows11 ? "Windows 11" : "Windows 10";
            return DisplayVersion is null ? $"{version} {Build}" : $"{version} {DisplayVersion} (build {Build})";
        }
    }

    public bool CanConfigureDoH => Build >= 22000 || DohCmdletsAvailable;

    public static OsInfo Detect(IRegistryAccess registry)
    {
        var version = Environment.OSVersion.Version;
        var build = version.Build > 0 ? version.Build : ParseBuild(EnvironmentExtensions.ReleaseId());

        var productName = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName")
            ?? "Windows";
        var displayVersion = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var installationType = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType");

        var isServer = productName.Contains("Server", StringComparison.OrdinalIgnoreCase)
            || string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase);

        var cmdlets = build >= 22000;

        return new OsInfo(
            productName,
            version.ToString(),
            build,
            string.IsNullOrWhiteSpace(displayVersion) ? null : displayVersion,
            build >= 22000 && !isServer,
            isServer,
            cmdlets,
            DotSupportedBySystemResolver: false);
    }

    public static async Task<OsInfo> DetectAsync(IRegistryAccess registry, IShell shell, CancellationToken ct = default)
    {
        var version = Environment.OSVersion.Version;
        var build = version.Build > 0 ? version.Build : ParseBuild(EnvironmentExtensions.ReleaseId());
        var productName = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "Windows";
        var displayVersion = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion");
        var installationType = registry.ReadString(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "InstallationType");

        var isServer = productName.Contains("Server", StringComparison.OrdinalIgnoreCase)
            || string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase);

        var cmdlets = await ProbeCmdletsAsync(shell, ct);

        return new OsInfo(
            productName,
            version.ToString(),
            build,
            string.IsNullOrWhiteSpace(displayVersion) ? null : displayVersion,
            build >= 22000 && !isServer,
            isServer,
            cmdlets,
            DotSupportedBySystemResolver: false);
    }

    private static async Task<bool> ProbeCmdletsAsync(IShell shell, CancellationToken ct = default)
    {
        var result = await shell.RunAsync(
            "if (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue) { Write-Output 'yes' } else { Write-Output 'no' }",
            ct);

        return result.StdOut.Contains("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseBuild(string? value) =>
        int.TryParse(value, out var build) ? build : 0;
}

internal static class EnvironmentExtensions
{
    public static string ReleaseId()
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            return key?.GetValue("ReleaseId")?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
