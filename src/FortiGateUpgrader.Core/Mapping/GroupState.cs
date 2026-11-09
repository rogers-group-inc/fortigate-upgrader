using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Core.Mapping;

/// <summary>A target (new firewall) port assigned to a switch/aggregate interface.</summary>
public sealed record GroupLink(string Port, string Group);

/// <summary>
/// Which ports of the new firewall belong to each switch/aggregate interface of the configuration.
/// Starts empty; the user drags ports onto the groups.
/// </summary>
public sealed class GroupState
{
    private readonly List<GroupLink> _links = new();

    public GroupState(MappingState mapping, IReadOnlyList<InterfaceGroup> groups)
    {
        Mapping = mapping;
        Groups = groups;
    }

    public MappingState Mapping { get; }
    public ModelProfile Target => Mapping.Target;
    public IReadOnlyList<InterfaceGroup> Groups { get; }
    public IReadOnlyList<GroupLink> Links => _links;

    public event EventHandler? Changed;

    public InterfaceGroup? FindGroup(string name) => Groups.FirstOrDefault(g => g.Name == name);

    public GroupLink Add(string port, string group)
    {
        if (Target.FindPort(port) == null) throw new ArgumentException($"Unknown port {port}");
        if (FindGroup(group) == null) throw new ArgumentException($"Unknown group {group}");
        var existing = _links.FirstOrDefault(l => l.Port == port && l.Group == group);
        if (existing != null) return existing;
        var link = new GroupLink(port, group);
        _links.Add(link);
        OnChanged();
        return link;
    }

    public void Remove(GroupLink link)
    {
        if (_links.Remove(link)) OnChanged();
    }

    public void Clear()
    {
        _links.Clear();
        OnChanged();
    }

    /// <summary>Copies links from another state (e.g. after the original config was edited), skipping ones that no longer apply.</summary>
    public void CopyFrom(GroupState other)
    {
        _links.Clear();
        foreach (var l in other.Links)
            if (Target.FindPort(l.Port) != null && FindGroup(l.Group) != null) _links.Add(l);
        OnChanged();
    }

    /// <summary>Suggests members: each group gets the new ports its old members are mapped to in the port mapping.</summary>
    public void FromPortMapping()
    {
        _links.Clear();
        var map = Mapping.ToMappingLenient();
        foreach (var g in Groups)
            foreach (var m in g.PhysicalMembers)
                if (map.TryGetValue(m, out var t) && _links.All(l => l.Port != t)) _links.Add(new GroupLink(t, g.Name));
        OnChanged();
    }

    public IEnumerable<GroupLink> LinksOf(string group) => _links.Where(l => l.Group == group);

    /// <summary>Why a link is red, or null when it's fine.</summary>
    public string? ProblemOf(GroupLink link)
    {
        var g = FindGroup(link.Group)!;
        var port = Target.FindPort(link.Port)!;
        var others = _links.Where(l => l.Port == link.Port && l.Group != link.Group).Select(l => l.Group).ToList();
        if (others.Count > 0) return $"{link.Port} is also in {string.Join(", ", others)}; a port can only be in one.";
        if (g.Kind == GroupKind.HardwareSwitch && port.SwitchCapable == false)
            return $"{link.Port} ({port.MediaDescription}) can't be a hardware switch member on the {Target.Model}. Use a software switch or another port.";
        // The port also receives a standalone port's settings from the port mapping.
        foreach (var src in Mapping.Links.Where(l => l.Target == link.Port).Select(l => l.Source))
        {
            if (Mapping.Usage.TryGetValue(src, out var u) && u.InUse && !u.OnlyGroupMember && !g.PhysicalMembers.Contains(src))
                return $"{link.Port} receives {src}'s settings in the port mapping, so it can't also be a member of {g.Name}.";
        }
        return null;
    }

    public string? WarningOf(GroupLink link)
    {
        var g = FindGroup(link.Group)!;
        var port = Target.FindPort(link.Port)!;
        if (g.Kind == GroupKind.HardwareSwitch && port.SwitchCapable == null)
            return $"It isn't confirmed that {link.Port} can join a hardware switch on the {Target.Model}.";
        return null;
    }

    /// <summary>Blocking problems (red lines).</summary>
    public IReadOnlyList<string> Problems =>
        _links.Select(ProblemOf).Where(p => p != null).Distinct().Cast<string>().ToList();

    /// <summary>Groups that end up with no members and so won't be converted.</summary>
    public IReadOnlyList<InterfaceGroup> EmptyGroups =>
        Groups.Where(g => !LinksOf(g.Name).Any() && g.OtherMembers.Count == 0).ToList();

    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = EmptyGroups.Select(g => $"{g.Name} ({g.KindLabel}) has no ports, so it won't be converted. Its settings and anything that uses it will be removed.").ToList();
            w.AddRange(_links.Select(WarningOf).Where(x => x != null).Distinct().Cast<string>());
            foreach (var g in Groups.Where(g => g.Kind == GroupKind.Aggregate))
            {
                var speeds = LinksOf(g.Name).Select(l => Target.FindPort(l.Port)!.MaxSpeedMbps).Distinct().Count();
                if (speeds > 1) w.Add($"Aggregate {g.Name} mixes ports of different speeds.");
            }
            return w;
        }
    }

    public bool IsComplete => Problems.Count == 0;

    /// <summary>Group name → new member ports (in front-panel order), for every group.</summary>
    public Dictionary<string, IReadOnlyList<string>> ToMembers()
    {
        var order = Target.Ports.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => x.i);
        return Groups.ToDictionary(g => g.Name,
            g => (IReadOnlyList<string>)LinksOf(g.Name).Select(l => l.Port).Distinct().OrderBy(p => order[p]).ToList());
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
