using System.Text;
using SquadDns.Core.Shell;

namespace SquadDns.Core.Tests;

public class PowerShellShellTests
{
    [Fact]
    public async Task Script_is_transferred_as_a_decoded_UTF16_command()
    {
        var runner = new FakeProcessRunner();
        var shell = new PowerShellShell(runner);
        const string script = "Get-DnsClientServerAddress | ConvertTo-Json -Compress";

        await shell.RunAsync(script);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("powershell.exe", call.FileName);

        var arguments = call.Arguments;
        Assert.Contains("-NoProfile", arguments);
        Assert.Contains("-NonInteractive", arguments);
        Assert.Equal("-EncodedCommand", arguments[arguments.Count - 2]);

        var decoded = Decode(arguments[arguments.Count - 1]);
        Assert.Contains(script, decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Exit_statement_stays_on_its_own_line_so_the_last_command_is_not_swallowed()
    {
        var runner = new FakeProcessRunner();
        var shell = new PowerShellShell(runner);
        const string script = "Get-DnsClientServerAddress | ConvertTo-Json -Compress -Depth 4";

        await shell.RunAsync(script);

        var decoded = Decode(runner.Calls.Single().Arguments.Last());

        Assert.Contains("\nexit 0\n", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain($"{script} exit", decoded, StringComparison.Ordinal);
        Assert.Contains("\n} catch {\n", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_uses_single_quotes_and_doubles_embedded_ones()
    {
        Assert.Equal("'1.1.1.1'", PowerShellShell.Quote("1.1.1.1"));
        Assert.Equal("'https://dns.example/dns-query'", PowerShellShell.Quote("https://dns.example/dns-query"));
        Assert.Equal("'O''Brien'", PowerShellShell.Quote("O'Brien"));
        Assert.Equal("'''; Remove-Item x'", PowerShellShell.Quote("'; Remove-Item x"));
        Assert.Equal("''''''", PowerShellShell.Quote("''"));
    }

    [Fact]
    public void QuoteArray_produces_a_parenthesised_string_array()
    {
        Assert.Equal("('1.1.1.1','1.0.0.1')", PowerShellShell.QuoteArray(new[] { "1.1.1.1", "1.0.0.1" }));
        Assert.Equal("('a''b')", PowerShellShell.QuoteArray(new[] { "a'b" }));
        Assert.Equal("()", PowerShellShell.QuoteArray(Array.Empty<string>()));
    }

    [Fact]
    public async Task Non_zero_exit_code_is_reported_as_failure()
    {
        var runner = new FakeProcessRunner { Result = new Core.Io.ProcessResult(1, string.Empty, "boom") };
        var shell = new PowerShellShell(runner);

        var result = await shell.RunAsync("Get-Thing");

        Assert.False(result.Success);
        Assert.Equal("boom", result.StdErr);
    }

    [Fact]
    public void Json_deserialises_a_powershell_object_and_tolerates_noise()
    {
        var ok = new ShellResult(0, "  {\"Servers\":[\"1.1.1.1\"]}  ", string.Empty);
        var payload = ok.Json<ServersPayload>();

        Assert.NotNull(payload);
        Assert.Equal(new[] { "1.1.1.1" }, payload!.Servers);

        Assert.Null(new ShellResult(0, "not json", string.Empty).Json<ServersPayload>());
        Assert.Null(new ShellResult(0, "   ", string.Empty).Json<ServersPayload>());
    }

    private static string Decode(string encoded) => Encoding.Unicode.GetString(Convert.FromBase64String(encoded));

    private sealed record ServersPayload(IReadOnlyList<string> Servers);
}
