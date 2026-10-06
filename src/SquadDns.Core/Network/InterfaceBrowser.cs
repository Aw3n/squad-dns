using SquadDns.Core.Shell;

namespace SquadDns.Core.Network;

public sealed record NetworkAdapter(string Alias, int Index, string Family, IReadOnlyList<string> Servers, string Dhcp, string State, int Metric, bool HasDefaultRoute)
{
    public bool IsConnected => State.Equals("Connected", StringComparison.OrdinalIgnoreCase);
    public bool UsesDhcp => Dhcp.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
    public bool HasStaticDns => Servers.Count > 0 && !UsesDhcp;
}

public sealed class InterfaceBrowser
{
    private readonly IShell _shell;

    public InterfaceBrowser(IShell shell) => _shell = shell;

    private const string Script = """
        $routes = @{}
        try {
          Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | ForEach-Object { $routes[[int]$_.InterfaceIndex] = $true }
        } catch {}
        try {
          Get-NetRoute -DestinationPrefix '::/0' -ErrorAction SilentlyContinue | ForEach-Object { $routes[[int]$_.InterfaceIndex] = $true }
        } catch {}
        $rows = @(
          Get-DnsClientServerAddress | ForEach-Object {
            $idx = [int]$_.InterfaceIndex
            $fam = if ([int]$_.AddressFamily -eq 2) { 'IPv4' } else { 'IPv6' }
            $iph = Get-NetIPInterface -InterfaceIndex $idx -ErrorAction SilentlyContinue | Select-Object -First 1
            [pscustomobject]@{
              Index = $idx
              Alias = [string]$_.InterfaceAlias
              Family = $fam
              Servers = @($_.ServerAddresses)
              Dhcp = if ($iph) { [string]$iph.Dhcp } else { 'Unknown' }
              State = if ($iph) { [string]$iph.ConnectionState } else { 'Unknown' }
              Metric = if ($iph) { [int]$iph.InterfaceMetric } else { 0 }
              HasDefaultRoute = $routes.ContainsKey($idx)
            }
          }
        )
        ConvertTo-Json -Compress -Depth 4 -InputObject $rows
        """;

    public async Task<IReadOnlyList<NetworkAdapter>> ListAsync(CancellationToken ct = default)
    {
        var result = await _shell.RunAsync(Script, ct);
        var parsed = Parse(result);

        if (parsed.Count == 0)
        {
            return Fallback();
        }

        return parsed
            .GroupBy(a => (a.Index, a.Family))
            .Select(g => g.OrderBy(a => a.Servers.Count).First())
            .OrderByDescending(a => a.HasDefaultRoute)
            .ThenByDescending(a => a.IsConnected)
            .ThenBy(a => a.Metric)
            .ThenBy(a => a.Alias, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<NetworkAdapter> Parse(ShellResult result)
    {
        var payload = result.StdOut.Trim();
        var list = new List<NetworkAdapter>();
        if (payload.Length == 0 || !payload.StartsWith('[') && !payload.StartsWith('{'))
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
                    AddRow(list, element);
                }
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                AddRow(list, root);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through to empty result
        }

        return list;
    }

    private static void AddRow(List<NetworkAdapter> list, System.Text.Json.JsonElement element)
    {
        var index = element.TryGetProperty("Index", out var idx) && idx.TryGetInt32(out var i) ? i : -1;
        var alias = element.TryGetProperty("Alias", out var al) ? al.GetString() ?? string.Empty : string.Empty;
        if (index < 0 || alias.Length == 0)
        {
            return;
        }

        var servers = new List<string>();
        if (element.TryGetProperty("Servers", out var srv))
        {
            if (srv.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var value in srv.EnumerateArray())
                {
                    var text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        servers.Add(text);
                    }
                }
            }
            else if (srv.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                servers.Add(srv.GetString()!);
            }
        }

        list.Add(new NetworkAdapter(
            alias,
            index,
            element.TryGetProperty("Family", out var fam) ? fam.GetString() ?? "IPv4" : "IPv4",
            servers,
            element.TryGetProperty("Dhcp", out var dhcp) ? dhcp.GetString() ?? "Unknown" : "Unknown",
            element.TryGetProperty("State", out var state) ? state.GetString() ?? "Unknown" : "Unknown",
            element.TryGetProperty("Metric", out var metric) && metric.TryGetInt32(out var m) ? m : 0,
            element.TryGetProperty("HasDefaultRoute", out var route) && route.ValueKind == System.Text.Json.JsonValueKind.True));
    }

    private static IReadOnlyList<NetworkAdapter> Fallback()
    {
        var list = new List<NetworkAdapter>();
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
            {
                continue;
            }

            var props = adapter.GetIPProperties();
            var servers = props.DnsAddresses
                .Select(a => a.ToString())
                .ToList();

            var index = IndexOf(adapter.Id);
            list.Add(new NetworkAdapter(adapter.Name, index, "IPv4", servers, "Unknown",
                "Connected", 0, props.GatewayAddresses.Count > 0));
        }

        return list;
    }

    private static int IndexOf(string id)
    {
        foreach (var adapter in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (string.Equals(adapter.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var property = adapter.GetIPProperties().GetIPv4Properties();
                    if (property is not null)
                    {
                        return property.Index;
                    }
                }
                catch (NotSupportedException)
                {
                    // no IPv4 stack
                }
            }
        }

        return -1;
    }
}
