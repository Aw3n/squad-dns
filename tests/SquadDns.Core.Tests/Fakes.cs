using Microsoft.Win32;
using SquadDns.Core.Io;
using SquadDns.Core.Shell;

namespace SquadDns.Core.Tests;

public sealed class FakeShell : IShell
{
    private readonly Queue<string> _queued = new();

    public List<string> Scripts { get; } = new();

    public string DefaultOutput { get; set; } = string.Empty;

    public int ExitCode { get; set; }

    public string CapabilityProbeOutput { get; set; } = "yes";

    public void Enqueue(string output) => _queued.Enqueue(output);

    public Task<ShellResult> RunAsync(string script, CancellationToken ct = default)
    {
        Scripts.Add(script);

        if (script.Contains("Get-Command", StringComparison.Ordinal))
        {
            return Task.FromResult(new ShellResult(0, CapabilityProbeOutput, string.Empty));
        }

        var output = _queued.Count > 0 ? _queued.Dequeue() : DefaultOutput;
        return Task.FromResult(new ShellResult(ExitCode, output, ExitCode == 0 ? string.Empty : "fake failure"));
    }
}

public sealed class FakeProcessRunner : IProcessRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments)> Calls { get; } = new();

    public ProcessResult Result { get; set; } = new(0, string.Empty, string.Empty);

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add((fileName, arguments));
        return Task.FromResult(Result);
    }
}

public sealed class FakeRegistry : IRegistryAccess
{
    private readonly Dictionary<string, Dictionary<string, object?>> _store = new(StringComparer.OrdinalIgnoreCase);

    public static string Key(RegistryHive hive, string subKey) => $"{hive}\\{subKey}";

    public string? ReadString(RegistryHive hive, string subKey, string name) =>
        TryGet(hive, subKey, name) as string;

    public int? ReadDword(RegistryHive hive, string subKey, string name) =>
        TryGet(hive, subKey, name) is int value ? value : null;

    public void WriteString(RegistryHive hive, string subKey, string name, string value) => Put(hive, subKey, name, value);

    public void WriteDword(RegistryHive hive, string subKey, string name, int value) => Put(hive, subKey, name, value);

    public void DeleteValue(RegistryHive hive, string subKey, string name)
    {
        if (_store.TryGetValue(Key(hive, subKey), out var values))
        {
            values.Remove(name);
        }
    }

    public bool KeyExists(RegistryHive hive, string subKey) => _store.ContainsKey(Key(hive, subKey));

    public IReadOnlyList<string> ReadSubKeyNames(RegistryHive hive, string subKey)
    {
        var prefix = Key(hive, subKey) + "\\";
        return _store.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyDictionary<string, object?> ReadAllValues(RegistryHive hive, string subKey) =>
        _store.TryGetValue(Key(hive, subKey), out var values)
            ? values
            : new Dictionary<string, object?>();

    private object? TryGet(RegistryHive hive, string subKey, string name) =>
        _store.TryGetValue(Key(hive, subKey), out var values) && values.TryGetValue(name, out var value) ? value : null;

    private void Put(RegistryHive hive, string subKey, string name, object value)
    {
        if (!_store.TryGetValue(Key(hive, subKey), out var values))
        {
            values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            _store[Key(hive, subKey)] = values;
        }

        values[name] = value;
    }
}

public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "squaddns-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
