using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Core.Mapping;

public sealed record PortLink(string Source, string Target);

public enum LinkStatus
{
    /// <summary>One source port to one target port (green).</summary>
    Valid,
    /// <summary>The target port has several source ports, or the source port goes to several targets (red).</summary>
    Conflict,
}

public enum SourcePortStatus
{
    Mapped,
    Conflict,
    /// <summary>Not mapped and has no configuration; it is not converted.</summary>
    UnmappedUnused,
    /// <summary>Not mapped but carries configuration (amber). It is not converted and its settings are removed.</summary>
    UnmappedInUse,
    /// <summary>Not mapped, and its only role is being a switch/aggregate member: the group's new ports are chosen on the groups tab.</summary>
    GroupMember,
}

/// <summary>The user's port-to-port links plus the validation rules (green / red / amber).</summary>
public sealed class MappingState
{
    private readonly List<PortLink> _links = new();

    public MappingState(ModelProfile source, ModelProfile target, IReadOnlyDictionary<string, PortUsage> usage)
    {
        Source = source;
        Target = target;
        Usage = usage;
    }

    public ModelProfile Source { get; }
    public ModelProfile Target { get; }
    public IReadOnlyDictionary<string, PortUsage> Usage { get; }
    public IReadOnlyList<PortLink> Links => _links;

    public event EventHandler? Changed;

    public PortLink Add(string source, string target)
    {
        if (Source.FindPort(source) == null) throw new ArgumentException($"Unknown source port {source}");
        if (Target.FindPort(target) == null) throw new ArgumentException($"Unknown target port {target}");
        var existing = _links.FirstOrDefault(l => l.Source == source && l.Target == target);
        if (existing != null) return existing;
        var link = new PortLink(source, target);
        _links.Add(link);
        OnChanged();
        return link;
    }

    public void Remove(PortLink link)
    {
        if (_links.Remove(link)) OnChanged();
    }

    public void Clear()
    {
        _links.Clear();
        OnChanged();
    }

    /// <summary>Copies links from another state (e.g. after the source config was edited). Invalid links are skipped.</summary>
    public void CopyFrom(MappingState other)
    {
        _links.Clear();
        foreach (var l in other.Links)
        {
            if (Source.FindPort(l.Source) != null && Target.FindPort(l.Target) != null) _links.Add(l);
        }
        OnChanged();
    }

    public LinkStatus StatusOf(PortLink link) =>
        _links.Count(l => l.Target == link.Target) > 1 || _links.Count(l => l.Source == link.Source) > 1
            ? LinkStatus.Conflict
            : LinkStatus.Valid;

    public SourcePortStatus StatusOfSource(string source)
    {
        var links = _links.Where(l => l.Source == source).ToList();
        if (links.Count > 0) return links.Any(l => StatusOf(l) == LinkStatus.Conflict) ? SourcePortStatus.Conflict : SourcePortStatus.Mapped;
        if (!Usage.TryGetValue(source, out var u) || !u.InUse) return SourcePortStatus.UnmappedUnused;
        return u.OnlyGroupMember ? SourcePortStatus.GroupMember : SourcePortStatus.UnmappedInUse;
    }

    public bool IsTargetConflicted(string target) => _links.Count(l => l.Target == target) > 1;

    public IReadOnlyList<string> Problems
    {
        get
        {
            var p = new List<string>();
            foreach (var g in _links.GroupBy(l => l.Target).Where(g => g.Count() > 1))
                p.Add($"{Target.Model} port {g.Key} has {g.Count()} source ports ({string.Join(", ", g.Select(l => l.Source))}).");
            foreach (var g in _links.GroupBy(l => l.Source).Where(g => g.Count() > 1))
                p.Add($"{Source.Model} port {g.Key} is connected to {g.Count()} target ports.");
            return p;
        }
    }

    /// <summary>Ports that carry configuration but are not mapped. Allowed: they and their settings are not converted.</summary>
    public IReadOnlyList<string> UnmappedInUse =>
        Source.Ports.Where(x => StatusOfSource(x.Name) == SourcePortStatus.UnmappedInUse).Select(x => x.Name).ToList();

    /// <summary>Non-blocking notes about the mapping (unmapped ports that carry configuration).</summary>
    public IReadOnlyList<string> Warnings =>
        UnmappedInUse.Select(n =>
        {
            var refs = Usage[n].References.Count(r => !r.IsDefinition);
            return $"{n} is not mapped, so it won't be converted. Its settings" +
                   (refs > 0 ? $" and anything that depends on it ({refs} reference(s): policies, routes, VLANs, …) will be removed." : " will be removed.");
        }).ToList();

    public bool IsComplete => Problems.Count == 0;

    /// <summary>Like <see cref="ToMapping"/>, but tolerates conflicts (first link wins).</summary>
    public IReadOnlyDictionary<string, string> ToMappingLenient()
    {
        var d = new Dictionary<string, string>();
        foreach (var l in _links) d.TryAdd(l.Source, l.Target);
        return d;
    }

    public IReadOnlyDictionary<string, string> ToMapping() => _links.ToDictionary(l => l.Source, l => l.Target);

    /// <summary>
    /// Suggests links: first by matching front-panel label (WAN1→"WAN1 / SFP+1", "3"→"3", A→A),
    /// then by identical interface name. In-use ports get first pick of target ports.
    /// </summary>
    public void AutoMap()
    {
        _links.Clear();
        var usedTargets = new HashSet<string>();
        var ordered = Source.Ports.OrderByDescending(p => Usage.TryGetValue(p.Name, out var u) && u.InUse).ToList();

        foreach (var sp in ordered)
        {
            var labels = LabelParts(sp.Label).ToHashSet();
            var match = Target.Ports.FirstOrDefault(t => !usedTargets.Contains(t.Name) && LabelParts(t.Label).Any(labels.Contains))
                        ?? Target.Ports.FirstOrDefault(t => !usedTargets.Contains(t.Name) && t.Name == sp.Name);
            if (match == null) continue;
            usedTargets.Add(match.Name);
            _links.Add(new PortLink(sp.Name, match.Name));
        }
        OnChanged();
    }

    private static string NormalizeLabel(string label) => label.Replace(" ", "").ToUpperInvariant();

    private static IEnumerable<string> LabelParts(string label) => label.Split('/').Select(NormalizeLabel);

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
