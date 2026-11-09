using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Core.Config;

public sealed class PortUsage
{
    public PortUsage(string name) => Name = name;

    public string Name { get; }

    /// <summary>The port has an entry under config system interface.</summary>
    public bool Defined { get; set; }

    public List<string> Reasons { get; } = new();

    public List<InterfaceReference> References { get; } = new();

    /// <summary>True when the port carries configuration that must be moved to the new box.</summary>
    public bool InUse => Reasons.Count > 0;

    public string Summary => InUse ? string.Join("\n", Reasons) : "Not used (default settings only)";

    /// <summary>
    /// True when the port's only job is being a member of a switch/aggregate (no address, nothing else
    /// refers to it). Such ports don't need mapping: the group's new members are chosen separately.
    /// </summary>
    public bool OnlyGroupMember { get; internal set; }

    /// <summary>Names of the switch/aggregate interfaces this port is a member of.</summary>
    public List<string> Groups { get; } = new();
}

/// <summary>Determines which physical/virtual ports of the source model carry configuration.</summary>
public static class InterfaceUsageAnalyzer
{
    public static Dictionary<string, PortUsage> Analyze(FortiConfig config, ModelProfile source)
    {
        var names = source.Ports.Select(p => p.Name).Concat(source.VirtualPorts).ToHashSet();
        var result = names.ToDictionary(n => n, n => new PortUsage(n));

        foreach (var r in InterfaceReferences.Find(config, names))
        {
            var u = result[r.Name];
            u.References.Add(r);
            if (r.IsDefinition)
            {
                u.Defined = true;
                continue;
            }
            u.Reasons.Add(Explain(r));
        }

        var hasOwn = new HashSet<string>();
        foreach (var name in names)
        {
            var def = InterfaceReferences.FindDefinition(config, name);
            if (def == null) continue;
            var own = OwnSettings(def);
            if (own == null) continue;
            result[name].Reasons.Insert(0, own);
            hasOwn.Add(name);
        }

        foreach (var u in result.Values)
        {
            var refs = u.References.Where(r => !r.IsDefinition).ToList();
            foreach (var r in refs.Where(IsMembership)) u.Groups.Add(GroupOf(r));
            u.OnlyGroupMember = refs.Count > 0 && refs.All(IsMembership) && !hasOwn.Contains(u.Name);
        }

        foreach (var u in result.Values)
        {
            // Deduplicate identical explanations (e.g. ten policies using the same port).
            var grouped = u.Reasons.GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key} (×{g.Count()})" : g.Key).ToList();
            u.Reasons.Clear();
            u.Reasons.AddRange(grouped);
        }
        return result;
    }

    /// <summary>Describes addressing on the interface's own entry, or null if it only has defaults.</summary>
    private static string? OwnSettings(ConfigNode def)
    {
        var status = def.FindSet("status")?.Values.FirstOrDefault()?.Text;
        if (status == "down") return null;

        var ip = def.FindSet("ip")?.Values.Select(v => v.Text).ToList();
        if (ip is { Count: > 0 } && ip[0] != "0.0.0.0") return $"Has IP address {string.Join(' ', ip)}";

        var mode = def.FindSet("mode")?.Values.FirstOrDefault()?.Text;
        if (mode is "dhcp" or "pppoe") return $"Addressing mode {mode}";

        var role = def.FindSet("role")?.Values.FirstOrDefault()?.Text;
        var alias = def.FindSet("alias")?.Values.FirstOrDefault()?.Text;
        if (def.FindSet("ip6-address") != null || def.Children.Any(c => c.Kind == NodeKind.Config && c.Name == "ipv6" && c.Descendants().Any(d => d.SetKey == "ip6-address")))
            return "Has an IPv6 address";
        if (alias != null && role != null) return $"Configured as \"{alias}\" (role {role})";
        return null;
    }

    /// <summary>True for a reference that makes the port a member of a switch/aggregate.</summary>
    public static bool IsMembership(InterfaceReference r) =>
        r.Node.ConfigPath == "system virtual-switch/port" ||
        r.Node.SetKey == "member" && r.Node.ConfigPath is "system interface" or "system switch-interface";

    private static string GroupOf(InterfaceReference r) =>
        r.Node.ConfigPath == "system virtual-switch/port" ? r.Node.Parent?.Parent?.Name ?? "" : r.Node.Parent?.Name ?? "";

    private static string Explain(InterfaceReference r)
    {
        var path = r.Node.ConfigPath;
        var key = r.Node.SetKey;
        return path switch
        {
            "system virtual-switch/port" => $"Member of hardware switch \"{r.Node.Parent?.Parent?.Name}\"",
            "system interface" when key == "member" => $"Member of aggregate/redundant \"{r.Node.Parent?.Name}\"",
            "system switch-interface" when key == "member" => $"Member of software switch \"{r.Node.Parent?.Name}\"",
            "system interface" when key == "interface" && r.Node.Parent?.FindSet("type")?.Values.FirstOrDefault()?.Text == "tunnel"
                => $"Underlay of tunnel \"{r.Node.Parent?.Name}\"",
            "system interface" when key == "interface" => $"Parent of VLAN/sub-interface \"{r.Node.Parent?.Name}\"",
            "system zone" => $"Member of zone \"{r.Node.Parent?.Name}\"",
            "system sdwan/members" or "system virtual-wan-link/members" => "SD-WAN member",
            "system ha" => $"HA setting ({key})",
            "firewall policy" or "firewall policy6" or "firewall proxy-policy" => "Used in firewall policies",
            "router static" or "router static6" => "Used in static routes",
            "router policy" => "Used in policy routes",
            "firewall vip" => "Used in virtual IPs",
            "system dhcp server" => "DHCP server interface",
            "vpn ipsec phase1-interface" => "IPsec tunnel bound to it",
            _ => r.Kind == ReferenceKind.EditName ? $"Entry in {path.Replace('/', ' ')}" : $"Referenced in {path.Replace('/', ' ')} ({key})",
        };
    }
}
