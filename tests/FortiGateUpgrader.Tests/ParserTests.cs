using FortiGateUpgrader.Core.Config;

namespace FortiGateUpgrader.Tests;

public class ParserTests
{
    [Fact]
    public void Parses_flat_export_into_tree()
    {
        var cfg = Fixtures.Parse61F();
        Assert.Equal("FORTIGATE-61F", cfg.Platform);
        Assert.Equal("TESTPLANT-61F-1", cfg.Hostname);
        Assert.Equal("7.00 build2829", cfg.FirmwareDescription);
        var sysIf = Assert.Single(cfg.FindConfigs("system interface"));
        Assert.Contains(sysIf.Children, e => e.Name == "internal5");
        var port = cfg.Root.Descendants().First(n => n.Kind == NodeKind.Edit && n.Name == "internal2");
        Assert.Equal("system virtual-switch/port", port.ConfigPath);
    }

    [Fact]
    public void Round_trip_is_stable()
    {
        var once = FortiConfigWriter.Write(Fixtures.Parse61F());
        var twice = FortiConfigWriter.Write(FortiConfigParser.Parse(once));
        Assert.Equal(once, twice);
        Assert.Contains("    edit \"wan1\"\n        set vdom \"root\"\n", once);
    }

    [Fact]
    public void Multiline_quoted_values_are_kept_intact()
    {
        var cfg = Fixtures.Parse61F();
        var cert = cfg.Root.Descendants().First(n => n.SetKey == "certificate");
        Assert.Contains("\nend\nnext\n-----END CERTIFICATE-----", cert.Tokens[2].Text);
        // The "end"/"next" inside the certificate must not close any block: the policy table still exists.
        Assert.Equal(2, cfg.FindConfigs("firewall policy").Single().Children.Count);
    }

    [Fact]
    public void Escaped_quotes_survive_round_trip()
    {
        var text = FortiConfigWriter.Write(Fixtures.Parse61F());
        Assert.Contains("set comments \"has a \\\"quoted\\\" word\"", text);
    }

    [Fact]
    public void Truncated_file_is_rejected()
    {
        Assert.Throws<ConfigParseException>(() => FortiConfigParser.Parse("config system interface\nedit \"wan1\"\nset vdom \"root\"\n"));
        Assert.Throws<ConfigParseException>(() => FortiConfigParser.Parse("set x \"unterminated\n"));
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var cfg = FortiConfigParser.Parse(Fixtures.Config61F.Replace("\n", "\r\n"));
        Assert.Equal("TESTPLANT-61F-1", cfg.Hostname);
    }
}
