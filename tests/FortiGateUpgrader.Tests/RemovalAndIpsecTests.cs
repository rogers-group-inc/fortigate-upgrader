using FortiGateUpgrader.Core.Config;

namespace FortiGateUpgrader.Tests;

public class RemovalAndIpsecTests
{
    private static readonly Dictionary<string, IpsecCredentials> GoodKeys = new()
    {
        ["vpn ipsec phase1-interface/Overlay-1"] = new("NEW-OVERLAY", "s3cret-overlay"),
        ["vpn ipsec phase1-interface/Backup-1"] = new("NEW-BACKUP", "s3cret-backup"),
    };

    private static ConversionResult Convert(Dictionary<string, string> map, FortiConfig? src = null, IReadOnlyDictionary<string, IpsecCredentials>? keys = null, bool require = true) =>
        ConversionService.Convert(new ConversionRequest
        {
            Source = src ?? Fixtures.Parse61F(),
            SourceModel = Fixtures.Model("61F"),
            TargetModel = Fixtures.Model("91G"),
            Mapping = map,
            Ipsec = keys ?? new Dictionary<string, IpsecCredentials>(),
            RequireIpsecCredentials = require,
        });

    private static Dictionary<string, string> MapWithout(params string[] ports)
    {
        var m = new Dictionary<string, string>(Fixtures.Map61Fto91G);
        foreach (var p in ports) m.Remove(p);
        return m;
    }

    private static ConfigNode? Entry(FortiConfig cfg, string section, string name) =>
        cfg.FindConfigs(section).SelectMany(c => c.Children).FirstOrDefault(e => e.Kind == NodeKind.Edit && e.Name == name);

    [Fact]
    public void Unmapped_port_is_not_converted_and_its_dependants_go_with_it()
    {
        var r = Convert(MapWithout("dmz"));
        var o = r.Output;
        var text = FortiConfigWriter.Write(o);

        Assert.Contains("dmz", r.RemovedInterfaces);
        Assert.Null(InterfaceReferences.FindDefinition(o, "dmz"));
        Assert.Null(Entry(o, "firewall policy", "1"));        // its only srcintf was dmz
        Assert.NotNull(Entry(o, "firewall policy", "2"));
        var address = Entry(o, "firewall address", "dmz")!;  // object named "dmz" stays...
        Assert.Null(address.FindSet("associated-interface")); // ...but loses its binding
        Assert.Contains("set member \"dmz\"", text);           // address group member is an address, untouched
        Assert.Contains(r.Messages, m => m.Severity == Severity.Warning && m.Message.Contains("\"dmz\" was not mapped"));
        Assert.Contains(r.RemovedDependants["dmz"], d => d.Contains("firewall policy"));
        Assert.DoesNotContain("port6", r.EffectiveMapping.Values);
    }

    [Fact]
    public void Lists_only_lose_the_removed_member()
    {
        var r = Convert(MapWithout("a"));
        var o = r.Output;
        var fl = InterfaceReferences.FindDefinition(o, "fortilink")!;
        Assert.Equal(new[] { "b" }, fl.FindSet("member")!.Values.Select(v => v.Text));
        Assert.NotNull(InterfaceReferences.FindDefinition(o, "Users")); // VLAN on fortilink survives
        // "set hbdev "a" 50" loses the port and its priority; nothing left, so the setting goes.
        Assert.Null(o.FindConfigs("system ha").Single().FindSet("hbdev"));
    }

    [Fact]
    public void Removing_a_port_cascades_through_vlans_tunnels_routes_and_sdwan()
    {
        var map = MapWithout("wan2");
        var r = Convert(map, Fixtures.Parse61FWithVpn(), GoodKeys.Where(k => k.Key.EndsWith("Overlay-1")).ToDictionary(k => k.Key, k => k.Value));
        var o = r.Output;

        Assert.Null(Entry(o, "vpn ipsec phase1-interface", "Backup-1"));
        Assert.Null(Entry(o, "vpn ipsec phase2-interface", "Backup-1-p2"));
        Assert.Null(Entry(o, "router static", "1"));
        Assert.NotNull(Entry(o, "router static", "2"));
        Assert.Null(Entry(o, "system sdwan/members", "2"));
        Assert.NotNull(Entry(o, "system sdwan/members", "1"));
        Assert.Equal(new[] { "Overlay-1" }, r.Tunnels.Select(t => t.Name)); // Cert-1 needs no key, Backup-1 is gone
    }

    [Fact]
    public void Removing_all_aggregate_members_removes_the_aggregate_and_its_vlans()
    {
        var r = Convert(MapWithout("a", "b"));
        Assert.Null(InterfaceReferences.FindDefinition(r.Output, "fortilink"));
        Assert.Null(InterfaceReferences.FindDefinition(r.Output, "Users"));
    }

    [Fact]
    public void Tunnels_need_keys_before_conversion()
    {
        var src = Fixtures.Parse61FWithVpn();
        var ex = Assert.Throws<ConversionException>(() => Convert(Fixtures.Map61Fto91G, src));
        Assert.Equal(2, ex.Problems.Count);
        Assert.Contains(ex.Problems, p => p.Contains("Overlay-1"));
        Assert.Contains(ex.Problems, p => p.Contains("Backup-1"));

        // Listing mode: no keys required, nothing changed.
        var listed = Convert(Fixtures.Map61Fto91G, src, require: false);
        Assert.Equal(new[] { "Overlay-1", "Backup-1" }, listed.Tunnels.Select(t => t.Name));
        Assert.Equal("x1", listed.Tunnels[0].Interface); // shown with the new interface name
        Assert.Equal("OLD-ID", listed.Tunnels[0].LocalId);
        Assert.Empty(listed.UpdatedTunnels);
    }

    [Fact]
    public void Keys_are_written_in_clear_text()
    {
        var r = Convert(Fixtures.Map61Fto91G, Fixtures.Parse61FWithVpn(), GoodKeys);
        var o = r.Output;
        var overlay = Entry(o, "vpn ipsec phase1-interface", "Overlay-1")!;
        Assert.Equal("NEW-OVERLAY", overlay.FindSet("localid")!.Values.Single().Text);
        Assert.Equal("set psksecret \"s3cret-overlay\"", overlay.FindSet("psksecret")!.ToString());
        var backup = Entry(o, "vpn ipsec phase1-interface", "Backup-1")!;
        Assert.Equal("NEW-BACKUP", backup.FindSet("localid")!.Values.Single().Text); // added where there was none
        var cert = Entry(o, "vpn ipsec phase1-interface", "Cert-1")!;
        Assert.Null(cert.FindSet("psksecret"));
        Assert.DoesNotContain("ENC AAAA", FortiConfigWriter.Write(o));
        Assert.Equal(new[] { "Overlay-1", "Backup-1" }, r.UpdatedTunnels);
        Assert.Contains(r.Messages, m => m.Message.Contains("clear text"));

        // Round trip: the output still parses and keeps the values.
        var again = FortiConfigParser.Parse(FortiConfigWriter.Write(o));
        Assert.Equal("s3cret-backup", Entry(again, "vpn ipsec phase1-interface", "Backup-1")!.FindSet("psksecret")!.Values.Single().Text);

        // The report never contains the keys.
        var html = ReportWriter.ToHtml(new ConversionRequest { Source = Fixtures.Parse61FWithVpn(), SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"), Mapping = Fixtures.Map61Fto91G, Ipsec = GoodKeys }, r, "in.conf", "out.conf");
        Assert.DoesNotContain("s3cret", html);
    }

    [Theory]
    [InlineData("", "abcdef", "local ID")]
    [InlineData("id", "", "pre-shared key")]
    [InlineData("id", "short", "6")]
    [InlineData("id", "has\"quote", "Double quotes")]
    [InlineData("id", "back\\slash", "backslashes")]
    public void Bad_credentials_are_rejected(string id, string psk, string expected)
    {
        Assert.Contains(expected, IpsecTunnels.Validate(new IpsecCredentials(id, psk)));
    }
}
