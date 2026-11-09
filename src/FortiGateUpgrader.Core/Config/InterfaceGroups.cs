using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Core.Config;

public enum GroupKind { HardwareSwitch, SoftwareSwitch, Aggregate, Redundant }

/// <summary>
/// An interface built from physical ports: a hardware switch (e.g. "internal"/"lan"), a software
/// switch, an 802.3ad aggregate (e.g. "fortilink") or a redundant pair.
/// </summary>
public sealed record InterfaceGroup(
    string Name, GroupKind Kind,
    IReadOnlyList<string> PhysicalMembers,
    IReadOnlyList<string> OtherMembers,
    string? Address, bool IsFortiLink)
{
    public string KindLabel => Kind switch
    {
        GroupKind.HardwareSwitch => "hardware switch",
        GroupKind.SoftwareSwitch => "software switch",
        GroupKind.Aggregate => IsFortiLink ? "aggregate (FortiLink)" : "aggregate (802.3ad)",
        _ => "redundant",
    };
}

public static class InterfaceGroups
{
    /// <summary>Finds every switch/aggregate interface and its members. Physical members are ports of <paramref name="model"/>.</summary>
    public static List<InterfaceGroup> Find(FortiConfig config, ModelProfile model)
    {
        var physical = model.Ports.Select(p => p.Name).ToHashSet();
        var defs = config.FindConfigs("system interface").SelectMany(c => c.Children).Where(e => e.Kind == NodeKind.Edit)
            .GroupBy(e => e.Name).ToDictionary(g => g.Key, g => g.First());
        var groups = new List<InterfaceGroup>();

        InterfaceGroup Make(string name, GroupKind kind, IEnumerable<string> members)
        {
            var list = members.ToList();
            defs.TryGetValue(name, out var def);
            var ip = def?.FindSet("ip")?.Values.Select(v => v.Text).ToList();
            var address = ip is { Count: > 0 } && ip[0] != "0.0.0.0" ? string.Join(' ', ip) : def?.FindSet("mode")?.Values.FirstOrDefault()?.Text is "dhcp" or "pppoe" ? def.FindSet("mode")!.Values.First().Text : null;
            var fortilink = def?.FindSet("fortilink")?.Values.FirstOrDefault()?.Text == "enable";
            return new InterfaceGroup(name, kind, list.Where(physical.Contains).ToList(), list.Where(m => !physical.Contains(m)).ToList(), address, fortilink);
        }

        foreach (var vs in config.FindConfigs("system virtual-switch").SelectMany(c => c.Children).Where(e => e.Kind == NodeKind.Edit))
            groups.Add(Make(vs.Name, GroupKind.HardwareSwitch, PortBlock(vs)?.Children.Where(e => e.Kind == NodeKind.Edit).Select(e => e.Name) ?? Enumerable.Empty<string>()));

        foreach (var sw in config.FindConfigs("system switch-interface").SelectMany(c => c.Children).Where(e => e.Kind == NodeKind.Edit))
            groups.Add(Make(sw.Name, GroupKind.SoftwareSwitch, Members(sw)));

        foreach (var def in defs.Values)
        {
            var type = def.FindSet("type")?.Values.FirstOrDefault()?.Text;
            if (type is "aggregate") groups.Add(Make(def.Name, GroupKind.Aggregate, Members(def)));
            else if (type is "redundant") groups.Add(Make(def.Name, GroupKind.Redundant, Members(def)));
        }
        return groups;
    }

    private static IEnumerable<string> Members(ConfigNode edit) => edit.FindSet("member")?.Values.Select(v => v.Text) ?? Enumerable.Empty<string>();

    private static ConfigNode? PortBlock(ConfigNode virtualSwitch) =>
        virtualSwitch.Children.FirstOrDefault(c => c.Kind == NodeKind.Config && c.Name == "port");

    /// <summary>The node that holds the group's membership (virtual-switch / switch-interface / interface entry).</summary>
    public static ConfigNode? Owner(FortiConfig config, InterfaceGroup g)
    {
        var path = g.Kind switch
        {
            GroupKind.HardwareSwitch => "system virtual-switch",
            GroupKind.SoftwareSwitch => "system switch-interface",
            _ => "system interface",
        };
        return config.FindConfigs(path).SelectMany(c => c.Children).FirstOrDefault(e => e.Kind == NodeKind.Edit && e.Name == g.Name);
    }

    /// <summary>Removes the physical members (source ports) from every group, keeping other members (VLANs, SSIDs, ...).</summary>
    public static void StripPhysicalMembers(FortiConfig config, IEnumerable<InterfaceGroup> groups)
    {
        foreach (var g in groups)
        {
            var owner = Owner(config, g);
            if (owner == null) continue;
            var drop = g.PhysicalMembers.ToHashSet();
            if (g.Kind == GroupKind.HardwareSwitch)
            {
                var block = PortBlock(owner);
                block?.Children.RemoveAll(e => e.Kind == NodeKind.Edit && drop.Contains(e.Name));
                continue;
            }
            var set = owner.FindSet("member");
            if (set == null) continue;
            var keep = set.Tokens.Take(2).Concat(set.Tokens.Skip(2).Where(t => !drop.Contains(t.Text))).ToList();
            set.Tokens.Clear();
            set.Tokens.AddRange(keep);
            if (set.Tokens.Count <= 2) owner.Children.Remove(set);
        }
    }

    /// <summary>Adds <paramref name="ports"/> (target port names) as members of the group.</summary>
    public static void AddMembers(FortiConfig config, InterfaceGroup g, IEnumerable<string> ports)
    {
        var owner = Owner(config, g);
        if (owner == null) return;
        if (g.Kind == GroupKind.HardwareSwitch)
        {
            var block = PortBlock(owner);
            if (block == null)
            {
                block = new ConfigNode(NodeKind.Config, new List<ConfigToken> { ConfigToken.Create("config", false), ConfigToken.Create("port", false) }, 0);
                owner.Add(block);
            }
            var existing = block.Children.Select(e => e.Name).ToHashSet();
            foreach (var p in ports.Where(p => !existing.Contains(p))) block.Add(ConfigNode.NewEdit(p));
            return;
        }
        var set = owner.FindSet("member");
        if (set == null)
        {
            set = ConfigNode.NewSet("member");
            var anchor = owner.FindSet("type") ?? owner.FindSet("vdom");
            if (anchor != null) owner.Insert(owner.Children.IndexOf(anchor) + 1, set);
            else owner.Add(set);
        }
        // Physical ports first, in the order given, then whatever other members were kept.
        var others = set.Tokens.Skip(2).ToList();
        set.Tokens.RemoveRange(2, set.Tokens.Count - 2);
        foreach (var p in ports.Where(p => !others.Any(o => o.Text == p))) set.Tokens.Add(ConfigToken.Create(p));
        set.Tokens.AddRange(others);
    }

    /// <summary>Removes a group's own definition entries (the switch/aggregate tables); the interface itself is removed by <see cref="InterfaceRemover"/>.</summary>
    public static void RemoveOwnerEntry(FortiConfig config, InterfaceGroup g)
    {
        if (g.Kind is GroupKind.HardwareSwitch or GroupKind.SoftwareSwitch)
        {
            var owner = Owner(config, g);
            owner?.Parent?.Children.Remove(owner);
        }
    }
}
