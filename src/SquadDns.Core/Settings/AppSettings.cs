using System.Security.Principal;
using System.Text.Json;
using SquadDns.Core.Models;

namespace SquadDns.Core.Settings;

public sealed class AppPaths
{
    public string Root { get; }
    public string Logs { get; }
    public string Backups { get; }
    public string SettingsFile { get; }
    public string LastApplyFile { get; }

    public AppPaths(string? overrideRoot = null)
    {
        Root = overrideRoot
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SquadDns");
        Logs = Path.Combine(Root, "logs");
        Backups = Path.Combine(Root, "backups");
        SettingsFile = Path.Combine(Root, "settings.json");
        LastApplyFile = Path.Combine(Root, "last-apply.json");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Backups);
    }
}

public sealed class AppSettings
{
    public string Language { get; set; } = "fr";
    public string Theme { get; set; } = "dark";
    public string SecurityMode { get; set; } = nameof(DnsSecurityMode.EncryptedPreferred);
    public bool UseDotEndpoints { get; set; }
    public bool AutoBackupBeforeApply { get; set; } = true;
    public bool AdvancedMode { get; set; }
    public int? PreferredInterfaceIndex { get; set; }
    public int TestSampleCount { get; set; } = 5;
    public string TestDomain { get; set; } = "example.com";
    public string? LastProfileId { get; set; }
    public bool UpdatesEnabled { get; set; }
    public string UpdateFeedUrl { get; set; } = string.Empty;
    public bool AcceptDotLimitation { get; set; }
    public string? CustomProfileJson { get; set; }

    public DnsSecurityMode Mode => Enum.TryParse<DnsSecurityMode>(SecurityMode, out var mode)
        ? mode
        : DnsSecurityMode.EncryptedPreferred;

    public static AppSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return loaded ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        File.WriteAllText(path, json);
    }
}

public static class Elevation
{
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static bool StartProcessElevated(string fileName, string arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
        };

        try
        {
            System.Diagnostics.Process.Start(startInfo);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
