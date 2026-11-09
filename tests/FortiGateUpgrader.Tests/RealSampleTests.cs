using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Mapping;
using Xunit.Abstractions;

namespace FortiGateUpgrader.Tests;

/// <summary>
/// Optional checks against real exported configs, which are never committed.
/// Set FGU_SAMPLE_61F / FGU_SAMPLE_91G to file paths (and optionally FGU_OUT_DIR) to run them;
/// without the variables these tests pass without doing anything.
/// </summary>
public class RealSampleTests
{
    private readonly ITestOutputHelper _out;

    public RealSampleTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Real_61F_to_91G()
    {
        var path = Environment.GetEnvironmentVariable("FGU_SAMPLE_61F");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var src = FortiConfigParser.ParseFile(path);
        var s = Fixtures.Catalog.FindByPlatform(src.Platform)!;
        var t = Fixtures.Model("91G");
        var usage = InterfaceUsageAnalyzer.Analyze(src, s);
        foreach (var u in usage.Values) _out.WriteLine($"{u.Name,-10} inUse={u.InUse} {string.Join(" | ", u.Reasons)}");

        var state = new MappingState(s, t, usage);
        state.AutoMap();
        if (state.Links.All(l => l.Source != "dmz")) state.Add("dmz", "port6");
        Assert.True(state.IsComplete, string.Join("; ", state.Problems));

        // Dummy keys for every tunnel (the real ones are never needed here).
        var listed = ConversionService.Convert(new ConversionRequest { Source = src, SourceModel = s, TargetModel = t, Mapping = state.ToMapping(), RequireIpsecCredentials = false });
        _out.WriteLine($"ipsec tunnels needing keys: {listed.Tunnels.Count}");
        var keys = listed.Tunnels.ToDictionary(x => x.Key, x => new IpsecCredentials("TEST-ID", "test-key-123"));
        var req = new ConversionRequest { Source = src, SourceModel = s, TargetModel = t, Mapping = state.ToMapping(), Ipsec = keys };
        var r = ConversionService.Convert(req);
        Assert.Equal(listed.Tunnels.Count, r.UpdatedTunnels.Count);
        Assert.DoesNotContain(r.Output.FindConfigs("vpn ipsec phase1-interface").SelectMany(c => c.Children),
            e => e.FindSet("psksecret")?.Values.FirstOrDefault()?.Text == "ENC");
        var text = FortiConfigWriter.Write(r.Output);

        // No remaining references to 61F-only port names, outside foreign-device sections.
        var leftovers = InterfaceReferences.Find(r.Output, new HashSet<string> { "wan1", "wan2", "dmz", "internal1", "internal2", "internal3", "internal4", "internal5" });
        Assert.Empty(leftovers);
        Assert.Equal(src.Root.Descendants().Count(n => n.Kind == NodeKind.Config), r.Output.Root.Descendants().Count(n => n.Kind == NodeKind.Config));
        Assert.Equal(src.FindConfigs("firewall policy").Sum(c => c.Children.Count), r.Output.FindConfigs("firewall policy").Sum(c => c.Children.Count));
        Assert.StartsWith("#config-version=FGT91G-", r.Output.HeaderLines[0]);
        Assert.Contains("build2829", r.Output.HeaderLines[0]);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diff = FortiGateUpgrader.Core.Diff.LineDiff.Compute(File.ReadAllText(path), text, ignoreIndentation: true);
        _out.WriteLine($"diff: +{diff.Added} -{diff.Removed} approximate={diff.Approximate} in {sw.ElapsedMilliseconds} ms");
        Assert.False(diff.Approximate);
        Assert.InRange(diff.Removed, 1, 200);

        _out.WriteLine($"renames={r.Renames.Count} ignored={r.Ignored.Count} removed=[{string.Join(",", r.RemovedInterfaces)}] added=[{string.Join(",", r.AddedInterfaces)}]");
        foreach (var m in r.Messages) _out.WriteLine($"{m.Severity}: {m.Message}");
        foreach (var i in r.Ignored.Where(i => !i.Reason.StartsWith("belongs"))) _out.WriteLine($"ignored: {i.Location} ({i.Reason})");

        var outDir = Environment.GetEnvironmentVariable("FGU_OUT_DIR");
        if (!string.IsNullOrEmpty(outDir))
        {
            File.WriteAllText(Path.Combine(outDir, "61F-to-91G.conf"), text);
            File.WriteAllText(Path.Combine(outDir, "61F-to-91G-report.html"), ReportWriter.ToHtml(req, r, path, "61F-to-91G.conf"));
        }
    }

    [Fact]
    public void Real_91G_round_trip_and_usage()
    {
        var path = Environment.GetEnvironmentVariable("FGU_SAMPLE_91G");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

        var cfg = FortiConfigParser.ParseFile(path);
        var once = FortiConfigWriter.Write(cfg);
        Assert.Equal(once, FortiConfigWriter.Write(FortiConfigParser.Parse(once)));

        // Every physical interface in the real config must be in the 91G profile (verifies the profile).
        var model = Fixtures.Model("91G");
        var physical = cfg.FindConfigs("system interface").SelectMany(c => c.Children)
            .Where(e => e.FindSet("type")?.Values.FirstOrDefault()?.Text == "physical").Select(e => e.Name).ToList();
        _out.WriteLine("physical: " + string.Join(", ", physical));
        Assert.All(physical, p => Assert.True(model.HasInterface(p), $"{p} missing from 91G profile"));

        var s = Fixtures.Model("91G");
        foreach (var target in new[] { "101F", "201G" })
        {
            var usage = InterfaceUsageAnalyzer.Analyze(cfg, s);
            var state = new MappingState(s, Fixtures.Model(target), usage);
            state.AutoMap();
            _out.WriteLine($"91G→{target} automap: {string.Join(", ", state.Links.Select(l => $"{l.Source}→{l.Target}"))}");
            _out.WriteLine($"  problems: {string.Join("; ", state.Problems)}");
            if (!state.IsComplete) continue;
            var baseReq = new ConversionRequest { Source = cfg, SourceModel = s, TargetModel = Fixtures.Model(target), Mapping = state.ToMapping(), RequireIpsecCredentials = false };
            var keys = ConversionService.Convert(baseReq).Tunnels.ToDictionary(x => x.Key, x => new IpsecCredentials("TEST-ID", "test-key-123"));
            var r = ConversionService.Convert(new ConversionRequest { Source = cfg, SourceModel = s, TargetModel = Fixtures.Model(target), Mapping = state.ToMapping(), Ipsec = keys });
            _out.WriteLine($"  renames={r.Renames.Count} tunnels={r.UpdatedTunnels.Count} errors={r.Messages.Count(m => m.Severity == Severity.Error)}");
            foreach (var (port, deps) in r.RemovedDependants) _out.WriteLine($"  not converted: {port} (+{deps.Count} dependants)");
        }
    }
}
