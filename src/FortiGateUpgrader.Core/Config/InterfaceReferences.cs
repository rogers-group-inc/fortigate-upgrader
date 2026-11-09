namespace FortiGateUpgrader.Core.Config;

public enum ReferenceKind
{
    /// <summary>The name of an interface-table entry, e.g. <c>edit "wan1"</c> under <c>config system interface</c>.</summary>
    EditName,
    /// <summary>A value of a set statement, e.g. <c>set interface "wan1"</c>.</summary>
    Value,
}

/// <summary>A token in the config that refers to a FortiGate interface by name.</summary>
public sealed record InterfaceReference(string Name, ConfigNode Node, ConfigToken Token, ReferenceKind Kind)
{
    /// <summary>True for the interface's own definition under <c>config system interface</c>.</summary>
    public bool IsDefinition => Kind == ReferenceKind.EditName && Node.ConfigPath == "system interface";

    public string Location => Kind == ReferenceKind.EditName
        ? $"{Node.ConfigPath.Replace('/', ' ')} → edit \"{Name}\""
        : Node.Describe();
}

/// <summary>A token that matches an interface name but is deliberately NOT treated as an interface reference.</summary>
public sealed record IgnoredMatch(string Name, ConfigNode Node, string Reason)
{
    public string Location => Node.Kind == NodeKind.Edit ? $"{Node.ConfigPath.Replace('/', ' ')} → edit \"{Name}\"" : Node.Describe();
}

/// <summary>
/// Knows which tokens of a FortiOS config are FortiGate interface names. This is the one place that
/// decides it, so the usage analyzer and the renamer always agree.
/// </summary>
public static class InterfaceReferences
{
    /// <summary>
    /// Config subtrees that describe OTHER devices (FortiSwitch, FortiAP, FortiExtender). Their port
    /// names (e.g. FortiSwitch "port1") must never be renamed.
    /// </summary>
    public static readonly string[] ForeignDeviceSections =
    {
        "switch-controller managed-switch",
        "switch-controller switch-profile",
        "switch-controller auto-config custom",
        "switch-controller dynamic-port-policy",
        "wireless-controller wtp",
        "wireless-controller wtp-profile",
        "extension-controller extender",
        "extension-controller extender-profile",
        "extender-controller extender",
    };

    /// <summary>Config tables whose entry names (edit "...") are interface names.</summary>
    public static readonly HashSet<string> InterfaceTables = new(StringComparer.Ordinal)
    {
        "system interface",
        "system virtual-switch/port",
        "system dns-server",
        "system ddns/monitor-interface",
        "system ntp/interface",
        "router rip/interface",
        "router ripng/interface",
        "router multicast/interface",
        "router multicast6/interface",
        "router isis/isis-interface",
        "router isis/isis-interface6",
    };

    /// <summary>"set member" lists interfaces only in these tables (elsewhere it lists addresses, services, ...).</summary>
    public static readonly HashSet<string> InterfaceMemberTables = new(StringComparer.Ordinal)
    {
        "system interface",
        "system switch-interface",
        "system virtual-wire-pair",
    };

    /// <summary>Set keys whose values are never interface names.</summary>
    public static readonly HashSet<string> NonInterfaceKeys = new(StringComparer.Ordinal)
    {
        "alias", "description", "comments", "comment", "name", "hostname", "vdom",
        "srcaddr", "dstaddr", "srcaddr6", "dstaddr6", "service", "schedule", "users", "groups", "fsso-groups",
        "poolname", "poolname6", "internet-service-name", "internet-service-src-name",
        "application-list", "av-profile", "webfilter-profile", "ips-sensor", "ssl-ssh-profile", "profile-group",
        "dnsfilter-profile", "emailfilter-profile", "file-filter-profile", "dlp-profile", "dlp-sensor", "waf-profile",
        "voip-profile", "icap-profile", "videofilter-profile", "sctp-filter-profile", "casb-profile",
        "virtual-patch-profile", "replacemsg-override-group", "vlan", "allowed-vlans", "untagged-vlans",
        "password", "passwd", "psksecret", "certificate", "buffer", "private-key", "ca", "local-cert",
        "switch-controller-nac", "switch-controller-dynamic", "switch-controller-traffic-policy",
        "proposal", "peertype", "authusrgrp", "mappedip", "extip",
    };

    private static readonly HashSet<string> ValueKeywords = new(StringComparer.Ordinal) { "set", "append", "select", "unselect" };

    /// <summary>Returns the foreign-device section a node sits in, or null.</summary>
    public static string? ForeignSection(ConfigNode node)
    {
        var path = node.ConfigPath;
        return ForeignDeviceSections.FirstOrDefault(s => path == s || path.StartsWith(s + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Finds every interface reference to any of <paramref name="names"/>.
    /// Tokens that match a name but are not interface references are returned in <paramref name="ignored"/>.
    /// </summary>
    public static List<InterfaceReference> Find(FortiConfig config, IReadOnlySet<string> names, List<IgnoredMatch>? ignored = null)
    {
        var refs = new List<InterfaceReference>();
        foreach (var node in config.Root.Descendants())
        {
            if (node.Kind == NodeKind.Config) continue;

            if (node.Kind == NodeKind.Edit)
            {
                if (node.Tokens.Count < 2 || !names.Contains(node.Tokens[1].Text)) continue;
                var name = node.Tokens[1].Text;
                var foreign = ForeignSection(node);
                if (foreign != null) ignored?.Add(new IgnoredMatch(name, node, $"belongs to another device ({foreign})"));
                else if (InterfaceTables.Contains(node.ConfigPath)) refs.Add(new InterfaceReference(name, node, node.Tokens[1], ReferenceKind.EditName));
                else ignored?.Add(new IgnoredMatch(name, node, "object name in a non-interface table"));
                continue;
            }

            if (node.Tokens.Count < 3 || !ValueKeywords.Contains(node.Keyword)) continue;
            var key = node.Tokens[1].Text;
            for (var i = 2; i < node.Tokens.Count; i++)
            {
                var tok = node.Tokens[i];
                if (!tok.Quoted || !names.Contains(tok.Text)) continue;

                var foreign = ForeignSection(node);
                if (foreign != null)
                    ignored?.Add(new IgnoredMatch(tok.Text, node, $"belongs to another device ({foreign})"));
                else if (NonInterfaceKeys.Contains(key))
                    ignored?.Add(new IgnoredMatch(tok.Text, node, $"\"{key}\" does not hold interface names"));
                else if (key == "member" && !InterfaceMemberTables.Contains(node.ConfigPath))
                    ignored?.Add(new IgnoredMatch(tok.Text, node, "group member, not an interface"));
                else
                    refs.Add(new InterfaceReference(tok.Text, node, tok, ReferenceKind.Value));
            }
        }
        return refs;
    }

    /// <summary>The definition entry of an interface under <c>config system interface</c>.</summary>
    public static ConfigNode? FindDefinition(FortiConfig config, string name) =>
        config.FindConfigs("system interface").SelectMany(c => c.Children)
              .FirstOrDefault(e => e.Kind == NodeKind.Edit && e.Name == name);
}
