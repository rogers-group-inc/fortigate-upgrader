using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Mapping;

namespace FortiGateUpgrader.Tests;

public class GroupTests
{
    // Only the standalone ports are mapped; switch/aggregate members are chosen per group.
    private static readonly Dictionary<string, string> StandaloneOnly = new() { ["wan1"] = "x1", ["wan2"] = "x2", ["dmz"] = "port6" };

    private static ConversionRequest Request(Dictionary<string, string> map, Dictionary<string, IReadOnlyList<string>>? groups) => new()
    {
        Source = Fixtures.Parse61F(),
        SourceModel = Fixtures.Model("61F"),
        TargetModel = Fixtures.Model("91G"),
        Mapping = map,
        GroupMembers = groups,
    };

    private static ConfigNode? Entry(FortiConfig cfg, string section, string name) =>
        cfg.FindConfigs(section).SelectMany(c => c.Children).FirstOrDefault(e => e.Kind == NodeKind.Edit && e.Name == name);

    [Fact]
    public void Groups_and_their_members_are_found()
    {
        var groups = InterfaceGroups.Find(Fixtures.Parse61F(), Fixtures.Model("61F"));
        var internalSw = groups.Single(g => g.Name == "internal");
        Assert.Equal(GroupKind.HardwareSwitch, internalSw.Kind);
        Assert.Equal(new[] { "internal1", "internal2", "internal3" }, internalSw.PhysicalMembers);
        Assert.Equal("192.0.2.1 255.255.255.0", internalSw.Address);
        var fl = groups.Single(g => g.Name == "fortilink");
        Assert.Equal(GroupKind.Aggregate, fl.Kind);
        Assert.True(fl.IsFortiLink);
        Assert.Equal(new[] { "a", "b" }, fl.PhysicalMembers);
    }

    [Fact]
    public void Member_only_ports_are_recognised()
    {
        var u = InterfaceUsageAnalyzer.Analyze(Fixtures.Parse61F(), Fixtures.Model("61F"));
        Assert.True(u["internal1"].OnlyGroupMember);
        Assert.Equal(new[] { "internal" }, u["internal1"].Groups);
        Assert.False(u["a"].OnlyGroupMember);   // also an HA heartbeat port
        Assert.False(u["dmz"].OnlyGroupMember);

        var s = new MappingState(Fixtures.Model("61F"), Fixtures.Model("91G"), u);
        Assert.Equal(SourcePortStatus.GroupMember, s.StatusOfSource("internal1"));
        Assert.DoesNotContain("internal1", s.UnmappedInUse);
    }

    [Fact]
    public void New_members_come_from_the_groups_tab_not_the_port_mapping()
    {
        var r = ConversionService.Convert(Request(new(StandaloneOnly) { ["a"] = "a", ["b"] = "b" }, new()
        {
            ["internal"] = new[] { "port1", "port2", "port3", "port4", "port5" },
            ["fortilink"] = new[] { "a", "b" },
        }));
        var o = r.Output;
        var vs = Entry(o, "system virtual-switch", "internal")!;
        var ports = vs.Descendants().Where(d => d.Kind == NodeKind.Edit).Select(d => d.Name).ToList();
        Assert.Equal(new[] { "port1", "port2", "port3", "port4", "port5" }, ports);
        Assert.Equal("192.0.2.1", InterfaceReferences.FindDefinition(o, "internal")!.FindSet("ip")!.Values.First().Text);
        Assert.Equal(new[] { "a", "b" }, InterfaceReferences.FindDefinition(o, "fortilink")!.FindSet("member")!.Values.Select(v => v.Text));
        Assert.Null(InterfaceReferences.FindDefinition(o, "internal1"));
        Assert.NotNull(InterfaceReferences.FindDefinition(o, "port4"));   // created as a default physical port
        Assert.NotNull(Entry(o, "firewall policy", "2"));                   // policy on "internal" survives
        Assert.DoesNotContain(r.Messages, m => m.Message.Contains("internal1") && m.Severity == Severity.Warning);
        Assert.Equal(5, r.GroupMembers["internal"].Count);
    }

    [Fact]
    public void Group_without_ports_is_not_converted()
    {
        var r = ConversionService.Convert(Request(new(StandaloneOnly) { ["a"] = "a", ["b"] = "b" }, new()
        {
            ["internal"] = Array.Empty<string>(),
            ["fortilink"] = new[] { "a", "b" },
        }));
        Assert.Contains("internal", r.RemovedGroups);
        Assert.Null(Entry(r.Output, "system virtual-switch", "internal"));
        Assert.Null(InterfaceReferences.FindDefinition(r.Output, "internal"));
        Assert.Null(Entry(r.Output, "firewall policy", "2"));
        Assert.Contains(r.Messages, m => m.Message.Contains("\"internal\" has no ports"));
    }

    [Fact]
    public void Invalid_group_choices_are_blocked()
    {
        var ex = Assert.Throws<ConversionException>(() => ConversionService.Convert(Request(StandaloneOnly, new()
        {
            ["internal"] = new[] { "x1", "port6", "port1" }, // x1 can't join; port6 receives dmz (has an IP)
            ["fortilink"] = new[] { "port1" },                // port1 in two groups
        })));
        Assert.Contains(ex.Problems, p => p.Contains("x1") && p.Contains("hardware switch"));
        Assert.Contains(ex.Problems, p => p.Contains("port6 receives dmz"));
        Assert.Contains(ex.Problems, p => p.Contains("port1 is in more than one group"));
    }

    [Fact]
    public void Group_state_starts_empty_and_can_follow_the_port_mapping()
    {
        var u = InterfaceUsageAnalyzer.Analyze(Fixtures.Parse61F(), Fixtures.Model("61F"));
        var m = new MappingState(Fixtures.Model("61F"), Fixtures.Model("91G"), u);
        var g = new GroupState(m, InterfaceGroups.Find(Fixtures.Parse61F(), Fixtures.Model("61F")));
        Assert.Empty(g.Links);
        Assert.Equal(2, g.EmptyGroups.Count);

        m.Add("internal1", "port1");
        m.Add("internal2", "port2");
        m.Add("a", "a");
        g.FromPortMapping();
        Assert.Equal(new[] { "port1", "port2" }, g.ToMembers()["internal"]);
        Assert.Equal(new[] { "a" }, g.ToMembers()["fortilink"]);

        // Red: port6 gets dmz's settings and is put in the switch.
        m.Add("dmz", "port6");
        var bad = g.Add("port6", "internal");
        Assert.Contains("dmz", g.ProblemOf(bad));
        Assert.False(g.IsComplete);
        g.Remove(bad);
        Assert.True(g.IsComplete);

        // Amber: a/b on the 91G aren't confirmed hardware-switch capable.
        var maybe = g.Add("b", "internal");
        Assert.Null(g.ProblemOf(maybe));
        Assert.NotNull(g.WarningOf(maybe));
    }
}
