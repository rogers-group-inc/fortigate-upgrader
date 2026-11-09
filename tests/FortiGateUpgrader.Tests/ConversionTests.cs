using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Tests;

public class ConversionTests
{
    private static ConversionResult Convert61Fto91G(Dictionary<string, string>? map = null) =>
        ConversionService.Convert(new ConversionRequest
        {
            Source = Fixtures.Parse61F(),
            SourceModel = Fixtures.Model("61F"),
            TargetModel = Fixtures.Model("91G"),
            Mapping = map ?? Fixtures.Map61Fto91G,
        });

    private static ConfigNode Def(FortiConfig cfg, string name) => InterfaceReferences.FindDefinition(cfg, name)!;

    [Fact]
    public void Usage_analysis_finds_configured_ports()
    {
        var u = InterfaceUsageAnalyzer.Analyze(Fixtures.Parse61F(), Fixtures.Model("61F"));
        Assert.True(u["wan1"].InUse);
        Assert.Contains(u["wan1"].Reasons, r => r.Contains("SD-WAN"));
        Assert.True(u["dmz"].InUse);
        Assert.True(u["internal1"].InUse);
        Assert.Contains(u["internal1"].Reasons, r => r.Contains("hardware switch"));
        Assert.True(u["a"].InUse);
        Assert.False(u["internal4"].InUse);
        Assert.False(u["modem"].InUse); // pppoe but status down
    }

    [Fact]
    public void Renames_physical_ports_everywhere_they_are_interfaces()
    {
        var r = Convert61Fto91G();
        var o = r.Output;
        var text = FortiConfigWriter.Write(o);

        Assert.Null(InterfaceReferences.FindDefinition(o, "wan1"));
        Assert.NotNull(Def(o, "x1"));
        Assert.Equal("dhcp", Def(o, "x1").FindSet("mode")!.Values.First().Text);
        Assert.DoesNotContain("\"internal1\"", text);
        Assert.Contains("set interface \"x1\"", text);      // tunnel + sdwan
        Assert.Contains("set srcintf \"port6\"", text);     // policy on old dmz
        Assert.Contains("set member \"a\" \"b\"", text);    // identity mapping
        Assert.Contains("set hbdev \"a\" 50", text);

        var vsPorts = o.Root.Descendants().Where(n => n.Kind == NodeKind.Edit && n.ConfigPath == "system virtual-switch/port").Select(n => n.Name);
        Assert.Equal(new[] { "port1", "port2", "port3" }, vsPorts);
        Assert.Equal(2, o.FindConfigs("firewall policy").Single().Children.Count);
    }

    [Fact]
    public void Object_named_like_interface_keeps_its_name_but_its_interface_binding_moves()
    {
        var r = Convert61Fto91G();
        var addr = r.Output.FindConfigs("firewall address").Single().Children.Single();
        Assert.Equal("dmz", addr.Name);
        Assert.Equal("port6", addr.FindSet("associated-interface")!.Values.Single().Text);

        var grp = r.Output.FindConfigs("firewall addrgrp").Single().Children.Single();
        Assert.Equal("dmz", grp.FindSet("member")!.Values.Single().Text);
        Assert.Contains(r.Ignored, i => i.Name == "dmz" && i.Reason.Contains("group member"));
    }

    [Fact]
    public void Header_is_rewritten_for_target_and_keeps_firmware_build()
    {
        var h = Convert61Fto91G().Output.HeaderLines;
        Assert.Contains("#config-version=FGT91G-7.00-FW-build2829-000000:opmode=0:vdom=0:user=admin", h);
        Assert.Contains("#platform=FORTIGATE-91G", h);
        Assert.Contains("#build=2829", h);
        Assert.DoesNotContain(h, l => l.StartsWith("#serialno") || l.StartsWith("#hwrev") || l.StartsWith("#mgmt.data"));
    }

    [Fact]
    public void Unused_unmapped_ports_are_removed_and_virtual_ports_carry_over()
    {
        var map = new Dictionary<string, string>(Fixtures.Map61Fto91G);
        map.Remove("internal4");
        map.Remove("internal5");
        var r = Convert61Fto91G(map);
        Assert.Contains("internal4", r.RemovedInterfaces);
        Assert.Null(InterfaceReferences.FindDefinition(r.Output, "internal4"));
        Assert.NotNull(InterfaceReferences.FindDefinition(r.Output, "modem"));
        // 91G port4/port5 had no source: they are created with defaults.
        Assert.Contains("port4", r.AddedInterfaces);
        Assert.Equal("physical", Def(r.Output, "port5").FindSet("type")!.Values.Single().Text);
    }

    [Fact]
    public void Hostname_can_be_changed()
    {
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = Fixtures.Parse61F(), SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"),
            Mapping = Fixtures.Map61Fto91G, NewHostname = "TESTPLANT-91G-1",
        });
        Assert.Equal("TESTPLANT-91G-1", r.Output.Hostname);
    }

    [Fact]
    public void Two_sources_on_one_target_is_rejected()
    {
        var map = new Dictionary<string, string>(Fixtures.Map61Fto91G) { ["wan2"] = "x1" };
        var ex = Assert.Throws<ConversionException>(() => Convert61Fto91G(map));
        Assert.Contains(ex.Problems, p => p.Contains("x1") && p.Contains("more than one"));
    }

    [Fact]
    public void FortiSwitch_ports_are_never_renamed()
    {
        // 91G → 101F with a swap: port1 ↔ port2, plus everything else by identity/label.
        var map = new Dictionary<string, string>
        {
            ["x1"] = "wan1", ["x2"] = "x1", ["port1"] = "port2", ["port2"] = "port1",
            ["port3"] = "port3", ["port4"] = "port4", ["port5"] = "port5", ["port6"] = "port6",
            ["a"] = "port7", ["b"] = "port8",
        };
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = Fixtures.Parse91G(), SourceModel = Fixtures.Model("91G"), TargetModel = Fixtures.Model("101F"), Mapping = map,
        });
        var o = r.Output;

        // Swap applied once, not twice.
        Assert.Equal("192.0.2.1", Def(o, "port2").FindSet("ip")!.Values.First().Text);
        Assert.Equal("198.51.100.1", Def(o, "port1").FindSet("ip")!.Values.First().Text);
        var policy = o.FindConfigs("firewall policy").Single().Children.Single();
        Assert.Equal("port2", policy.FindSet("srcintf")!.Values.Single().Text);
        Assert.Equal("port1", policy.FindSet("dstintf")!.Values.Single().Text);
        Assert.Equal("x1", Def(o, "fortilink").FindSet("member")!.Values.Single().Text);

        // FortiSwitch port entries are untouched.
        var swPorts = o.Root.Descendants().Where(n => n.Kind == NodeKind.Edit && n.ConfigPath == "switch-controller managed-switch/ports").Select(n => n.Name).ToList();
        Assert.Equal(new[] { "port1", "port2" }, swPorts);
        Assert.Contains(r.Ignored, i => i.Reason.Contains("another device"));

        // modem has no counterpart on 101F and was unused, so it is removed.
        Assert.Contains("modem", r.RemovedInterfaces);
        Assert.Contains(r.Messages, m => m.Message.Contains("not been checked against a real 101F"));
    }

    [Fact]
    public void Logical_interface_named_like_target_port_is_reported()
    {
        var text = Fixtures.Config61F.Replace("edit \"Users\"", "edit \"port6\"");
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = FortiConfigParser.Parse(text), SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"),
            Mapping = Fixtures.Map61Fto91G.Where(kv => kv.Key != "dmz").Append(new("dmz", "port6")).ToDictionary(kv => kv.Key, kv => kv.Value),
        });
        // The VLAN already called port6 clashes only if it is not a mapped source; here it isn't a 61F port at all.
        Assert.Contains(r.Messages, m => m.Severity == Severity.Error && m.Message.Contains("\"port6\""));
    }

    [Fact]
    public void Speed_not_supported_by_target_media_is_warned()
    {
        var map = new Dictionary<string, string>
        {
            ["wan1"] = "port1", ["wan2"] = "port17", ["dmz"] = "port2", ["internal1"] = "port3",
            ["internal2"] = "port4", ["internal3"] = "port5", ["a"] = "x1", ["b"] = "x2",
        };
        var r = ConversionService.Convert(new ConversionRequest
        {
            Source = Fixtures.Parse61F(), SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("201G"), Mapping = map,
        });
        Assert.Contains(r.Messages, m => m.Message.Contains("port17") && m.Message.Contains("100full"));
        Assert.Contains(r.Messages, m => m.Message.Contains("RJ45") && m.Message.Contains("SFP"));
    }

    public static IEnumerable<object[]> UpgradePaths() =>
        Fixtures.Catalog.Paths.SelectMany(p => p.Targets.Select(t => new object[] { p.Source, t }));

    [Theory]
    [MemberData(nameof(UpgradePaths))]
    public void Every_upgrade_path_converts_a_fully_used_config(string sourceModel, string targetModel)
    {
        ModelProfile s = Fixtures.Model(sourceModel), t = Fixtures.Model(targetModel);
        var cfg = Fixtures.AllPortsInUse(s);

        // Auto-map, then put any leftovers on free target ports.
        var usage = InterfaceUsageAnalyzer.Analyze(cfg, s);
        var state = new FortiGateUpgrader.Core.Mapping.MappingState(s, t, usage);
        state.AutoMap();
        var free = new Queue<string>(t.Ports.Select(p => p.Name).Where(n => state.Links.All(l => l.Target != n)));
        foreach (var p in s.Ports.Where(p => state.Links.All(l => l.Source != p.Name)))
            state.Add(p.Name, free.Dequeue());
        Assert.True(state.IsComplete, string.Join("; ", state.Problems));

        var r = ConversionService.Convert(new ConversionRequest { Source = cfg, SourceModel = s, TargetModel = t, Mapping = state.ToMapping() });

        foreach (var (src, dst) in state.ToMapping())
        {
            var ip = Def(r.Output, dst).FindSet("ip")!.Values.First().Text;
            Assert.Equal(Def(cfg, src).FindSet("ip")!.Values.First().Text, ip);
        }
        foreach (var port in t.Ports) Assert.NotNull(InterfaceReferences.FindDefinition(r.Output, port.Name));
        Assert.Equal($"#platform={t.Platform}", r.Output.HeaderLines.Single(l => l.StartsWith("#platform=")));
        Assert.Equal(s.Ports.Count, r.Output.FindConfigs("firewall policy").Single().Children.Count);
        // Output must parse again.
        FortiConfigParser.Parse(FortiConfigWriter.Write(r.Output));
    }

    [Fact]
    public void Report_is_generated()
    {
        var req = new ConversionRequest
        {
            Source = Fixtures.Parse61F(), SourceModel = Fixtures.Model("61F"), TargetModel = Fixtures.Model("91G"), Mapping = Fixtures.Map61Fto91G,
        };
        var html = ReportWriter.ToHtml(req, ConversionService.Convert(req), "in.conf", "out.conf");
        Assert.Contains("Human review required", html);
        Assert.Contains("<code>wan1</code>", html);
    }
}
