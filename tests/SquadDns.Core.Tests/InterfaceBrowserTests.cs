using SquadDns.Core.Network;
using SquadDns.Core.Shell;

namespace SquadDns.Core.Tests;

public class InterfaceBrowserTests
{
    [Fact]
    public void Parse_reads_every_field_of_a_powershell_row_array()
    {
        const string payload = """
            [
              {"Index":4,"Alias":"Ethernet","Family":"IPv4","Servers":["10.0.0.1","fd00::1"],"Dhcp":"Disabled","State":"Connected","Metric":25,"HasDefaultRoute":true},
              {"Index":4,"Alias":"Ethernet","Family":"IPv6","Servers":[],"Dhcp":"Enabled","State":"Connected","Metric":25,"HasDefaultRoute":true},
              {"Index":17,"Alias":"Ethernet 3","Family":"IPv4","Servers":["1.1.1.1"],"Dhcp":"Enabled","State":"Disconnected","Metric":300,"HasDefaultRoute":false}
            ]
            """;

        var rows = InterfaceBrowser.Parse(new ShellResult(0, payload, string.Empty));

        Assert.Equal(3, rows.Count);

        var first = rows[0];
        Assert.Equal("Ethernet", first.Alias);
        Assert.Equal(4, first.Index);
        Assert.Equal("IPv4", first.Family);
        Assert.Equal(new[] { "10.0.0.1", "fd00::1" }, first.Servers);
        Assert.True(first.HasStaticDns);
        Assert.True(first.HasDefaultRoute);
        Assert.True(first.IsConnected);

        Assert.True(rows[1].UsesDhcp);
        Assert.Equal(25, rows[1].Metric);
        Assert.Equal("Ethernet 3", rows[2].Alias);
        Assert.False(rows[2].IsConnected);
        Assert.False(rows[2].HasDefaultRoute);
    }

    [Fact]
    public void Parse_accepts_a_single_object_and_a_lone_server_string()
    {
        const string payload = """{"Index":8,"Alias":"Wi-Fi","Servers":"192.168.0.1","Dhcp":"Enabled","State":"Connected","Metric":35}""";

        var row = Assert.Single(InterfaceBrowser.Parse(new ShellResult(0, payload, string.Empty)));

        Assert.Equal("IPv4", row.Family);
        Assert.Equal(new[] { "192.168.0.1" }, row.Servers);
        Assert.False(row.HasDefaultRoute);
    }

    [Fact]
    public void Parse_skips_rows_that_cannot_be_identified()
    {
        const string payload = """
            [
              {"Index":-1,"Alias":"Ghost","Servers":[]},
              {"Index":5,"Alias":"","Servers":[]},
              {"Index":6,"Alias":"Good","Servers":[]}
            ]
            """;

        var rows = InterfaceBrowser.Parse(new ShellResult(0, payload, string.Empty));

        Assert.Equal(new[] { 6 }, rows.Select(r => r.Index));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CommandNotFoundException")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    public void Parse_returns_nothing_for_unusable_output(string payload)
    {
        Assert.Empty(InterfaceBrowser.Parse(new ShellResult(1, payload, "boom")));
    }

    [Fact]
    public async Task ListAsync_prefers_the_connected_default_route_then_the_lowest_metric()
    {
        const string payload = """
            [
              {"Index":17,"Alias":"Backup","Family":"IPv4","Servers":["1.0.0.1"],"Dhcp":"Enabled","State":"Connected","Metric":300,"HasDefaultRoute":false},
              {"Index":4,"Alias":"Primary","Family":"IPv4","Servers":["10.0.0.1"],"Dhcp":"Disabled","State":"Connected","Metric":25,"HasDefaultRoute":true},
              {"Index":9,"Alias":"Idle","Family":"IPv4","Servers":[],"Dhcp":"Enabled","State":"Disconnected","Metric":10,"HasDefaultRoute":false}
            ]
            """;

        var shell = new FakeShell { DefaultOutput = payload };
        var adapters = await new InterfaceBrowser(shell).ListAsync();

        Assert.Equal(new[] { "Primary", "Backup", "Idle" }, adapters.Select(a => a.Alias));
        Assert.True(adapters[0].HasDefaultRoute && adapters[0].IsConnected);
        Assert.Single(shell.Scripts);
    }

    [Fact]
    public async Task ListAsync_falls_back_to_the_framework_when_powershell_yields_nothing()
    {
        var adapters = await new InterfaceBrowser(new FakeShell { DefaultOutput = string.Empty }).ListAsync();

        Assert.All(adapters, adapter => Assert.False(string.IsNullOrWhiteSpace(adapter.Alias)));
    }
}
