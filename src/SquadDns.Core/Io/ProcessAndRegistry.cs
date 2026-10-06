using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace SquadDns.Core.Io;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        var readOut = process.StandardOutput.ReadToEndAsync();
        var readErr = process.StandardError.ReadToEndAsync();

        var completed = await WaitForExitAsync(process, timeout, ct);
        if (!completed)
        {
            TryKill(process);
            return new ProcessResult(-1, stdout.ToString(), "timeout");
        }

        stdout.Append(await readOut);
        stderr.Append(await readErr);

        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best effort
        }
    }
}

public interface IRegistryAccess
{
    string? ReadString(RegistryHive hive, string subKey, string name);
    int? ReadDword(RegistryHive hive, string subKey, string name);
    void WriteDword(RegistryHive hive, string subKey, string name, int value);
    void WriteString(RegistryHive hive, string subKey, string name, string value);
    void DeleteValue(RegistryHive hive, string subKey, string name);
    bool KeyExists(RegistryHive hive, string subKey);
    IReadOnlyList<string> ReadSubKeyNames(RegistryHive hive, string subKey);
    IReadOnlyDictionary<string, object?> ReadAllValues(RegistryHive hive, string subKey);
}

public sealed class WindowsRegistryAccess : IRegistryAccess
{
    private static RegistryKey Open(RegistryHive hive, string subKey, bool writable) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey, writable)
            ?? throw new IOException($"Registry key not found: {subKey}");

    public string? ReadString(RegistryHive hive, string subKey, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    public int? ReadDword(RegistryHive hive, string subKey, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is null ? null : Convert.ToInt32(value);
    }

    public void WriteDword(RegistryHive hive, string subKey, string name, int value)
    {
        Ensure(hive, subKey);
        using var key = Open(hive, subKey, true);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    public void WriteString(RegistryHive hive, string subKey, string name, string value)
    {
        Ensure(hive, subKey);
        using var key = Open(hive, subKey, true);
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public void DeleteValue(RegistryHive hive, string subKey, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (key.GetValue(name) is not null)
        {
            key.DeleteValue(name);
        }
    }

    public bool KeyExists(RegistryHive hive, string subKey) =>
        RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey) is not null;

    public IReadOnlyList<string> ReadSubKeyNames(RegistryHive hive, string subKey)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
        return key?.GetSubKeyNames() ?? Array.Empty<string>();
    }

    public IReadOnlyDictionary<string, object?> ReadAllValues(RegistryHive hive, string subKey)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(subKey);
        if (key is null)
        {
            return result;
        }

        foreach (var name in key.GetValueNames())
        {
            if (name.Length == 0)
            {
                continue;
            }

            result[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }

        return result;
    }

    private static void Ensure(RegistryHive hive, string subKey)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var _ = baseKey.CreateSubKey(subKey, writable: true);
    }
}
