using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Mapping;

namespace FortiGateUpgrader.Tests;

public class MappingTests
{
    private static MappingState New61Fto91G()
    {
        var cfg = Fixtures.Parse61F();
        return new MappingState(Fixtures.Model("61F"), Fixtures.Model("91G"), InterfaceUsageAnalyzer.Analyze(cfg, Fixtures.Model("61F")));
    }

    [Fact]
    public void AutoMap_matches_labels_then_names()
    {
        var s = New61Fto91G();
        s.AutoMap();
        var m = s.ToMapping();
        Assert.Equal("x1", m["wan1"]);
        Assert.Equal("x2", m["wan2"]);
        Assert.Equal("port1", m["internal1"]);
        Assert.Equal("port5", m["internal5"]);
        Assert.Equal("a", m["a"]);
        Assert.False(m.ContainsKey("dmz")); // no DMZ on the 91G
        Assert.Equal(SourcePortStatus.UnmappedInUse, s.StatusOfSource("dmz"));
        Assert.True(s.IsComplete); // unmapped ports are allowed...
        Assert.Contains(s.Warnings, w => w.StartsWith("dmz is not mapped")); // ...but flagged

        s.Add("dmz", "port6");
        Assert.True(s.IsComplete);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void Two_sources_on_one_target_are_red()
    {
        var s = New61Fto91G();
        var l1 = s.Add("wan1", "x1");
        var l2 = s.Add("wan2", "x1");
        Assert.Equal(LinkStatus.Conflict, s.StatusOf(l1));
        Assert.Equal(LinkStatus.Conflict, s.StatusOf(l2));
        Assert.True(s.IsTargetConflicted("x1"));
        s.Remove(l2);
        Assert.Equal(LinkStatus.Valid, s.StatusOf(l1));
    }

    [Fact]
    public void One_source_to_two_targets_is_red()
    {
        var s = New61Fto91G();
        var l1 = s.Add("wan1", "x1");
        s.Add("wan1", "x2");
        Assert.Equal(LinkStatus.Conflict, s.StatusOf(l1));
    }

    [Fact]
    public void All_profiles_load_and_paths_resolve()
    {
        var c = Fixtures.Catalog;
        Assert.Equal(4, c.Profiles.Count);
        Assert.Equal(new[] { "91G", "101F", "201G" }, c.TargetsFor(Fixtures.Model("61F")).Select(p => p.Model));
        Assert.Equal(new[] { "101F", "201G" }, c.TargetsFor(Fixtures.Model("91G")).Select(p => p.Model));
        Assert.Same(Fixtures.Model("61F"), c.FindByPlatform("FORTIGATE-61F"));
        Assert.Equal(28, Fixtures.Model("101F").Ports.Count);
        Assert.Equal(30, Fixtures.Model("201G").Ports.Count);
    }
}
