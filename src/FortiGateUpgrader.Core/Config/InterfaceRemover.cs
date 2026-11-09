namespace FortiGateUpgrader.Core.Config;

/// <summary>
/// Removes interfaces from a configuration together with everything that depends on them:
/// VLANs, tunnels and aggregates built on them, the policies, routes, DHCP servers, VIPs and
/// SD-WAN members that use them, and so on. Lists (zone members, policy interfaces, HA ports)
/// only lose the removed name; an entry is removed only when it would be left without one.
/// </summary>
public static class InterfaceRemover
{
    /// <summary>Tables whose entries are meaningless without their interface: they are removed when they lose it.</summary>
    private static readonly HashSet<string> EntryTables = new(StringComparer.Ordinal)
    {
        "system interface", "system switch-interface", "system gre-tunnel", "system ipip-tunnel", "system vxlan",
        "system virtual-wire-pair", "system arp-table", "system dhcp server", "system dhcp6 server",
        "system sdwan/members", "system virtual-wan-link/members", "system ipsec-aggregate",
        "vpn ipsec phase1-interface", "vpn ipsec phase2-interface", "vpn ipsec phase1", "vpn ipsec manualkey-interface",
        "firewall policy", "firewall policy6", "firewall proxy-policy", "firewall local-in-policy", "firewall local-in-policy6",
        "firewall multicast-policy", "firewall multicast-policy6", "firewall shaping-policy", "firewall interface-policy",
        "firewall interface-policy6", "firewall DoS-policy", "firewall DoS-policy6", "firewall sniffer", "firewall acl", "firewall acl6",
        "firewall vip", "firewall vip6", "firewall vip46", "firewall vip64",
        "router static", "router static6", "router policy", "router policy6",
        "router ospf/ospf-interface", "router ospf6/ospf6-interface", "router ospf/passive-interface",
        "system dns-server", "system ddns", "system virtual-switch/port", "system ntp/interface",
    };

    /// <summary>Removing an entry from these tables also removes an interface (or VIP) of the same name.</summary>
    private static readonly HashSet<string> NamedInterfaceTables = new(StringComparer.Ordinal)
    {
        "system interface", "system switch-interface", "system gre-tunnel", "system ipip-tunnel", "system vxlan",
        "system ipsec-aggregate", "vpn ipsec phase1-interface", "vpn ipsec manualkey-interface",
    };

    private static readonly HashSet<string> VipTables = new(StringComparer.Ordinal)
    {
        "firewall vip", "firewall vip6", "firewall vip46", "firewall vip64", "firewall vipgrp", "firewall vipgrp6",
    };

    /// <summary>Removes <paramref name="names"/> (and dependants) from <paramref name="config"/>; returns what was removed.</summary>
    public static List<string> Remove(FortiConfig config, IEnumerable<string> names)
    {
        var removed = new List<string>();
        var queue = new Queue<string>(names.Distinct());
        var done = new HashSet<string>();
        var removedSdwanMembers = new HashSet<string>();

        while (queue.Count > 0)
        {
            var batch = new HashSet<string>();
            while (queue.Count > 0) { var n = queue.Dequeue(); if (done.Add(n)) batch.Add(n); }
            if (batch.Count == 0) break;

            var newlyRemovedVips = new HashSet<string>();
            foreach (var r in InterfaceReferences.Find(config, batch))
            {
                if (IsDetached(r.Node)) continue;
                if (r.Kind == ReferenceKind.EditName)
                {
                    RemoveEntry(r.Node, removed, queue, newlyRemovedVips, removedSdwanMembers);
                    continue;
                }

                var stmt = r.Node;
                var idx = stmt.Tokens.IndexOf(r.Token);
                if (idx < 0) continue;
                stmt.Tokens.RemoveAt(idx);
                // "set hbdev "port1" 50 "port2" 50": drop the port's priority with it.
                if (stmt.SetKey == "hbdev" && idx < stmt.Tokens.Count && !stmt.Tokens[idx].Quoted && int.TryParse(stmt.Tokens[idx].Text, out _))
                    stmt.Tokens.RemoveAt(idx);
                if (stmt.Tokens.Count > 2) continue; // other values remain

                var entry = stmt.Parent;
                if (entry is { Kind: NodeKind.Edit } && EntryTables.Contains(entry.ConfigPath))
                    RemoveEntry(entry, removed, queue, newlyRemovedVips, removedSdwanMembers);
                else
                {
                    stmt.Parent?.Children.Remove(stmt);
                    removed.Add($"{stmt.Describe()} (setting removed)");
                }
            }

            // VIPs are referenced by name from policies and VIP groups.
            if (newlyRemovedVips.Count > 0) RemoveVipUses(config, newlyRemovedVips, removed, queue, removedSdwanMembers);
        }

        if (removedSdwanMembers.Count > 0) CleanSdwanSeqRefs(config, removedSdwanMembers, removed);
        return removed;
    }

    private static bool IsDetached(ConfigNode node)
    {
        for (var n = node; n != null; n = n.Parent)
            if (n.Parent == null) return n.Tokens.Count != 0; // reached a root: real root has no tokens
        return true;
    }

    private static void RemoveEntry(ConfigNode entry, List<string> removed, Queue<string> queue, HashSet<string> vips, HashSet<string> sdwan)
    {
        if (entry.Parent == null) return;
        var path = entry.ConfigPath;
        entry.Parent.Children.Remove(entry);
        entry.Parent = null;
        removed.Add($"{path} → edit \"{entry.Name}\"");
        if (NamedInterfaceTables.Contains(path)) queue.Enqueue(entry.Name);
        if (VipTables.Contains(path)) vips.Add(entry.Name);
        if (path is "system sdwan/members" or "system virtual-wan-link/members") sdwan.Add(entry.Name);
        if (path == "vpn ipsec phase1-interface" || path == "vpn ipsec phase1") queue.Enqueue(entry.Name); // phase2 "phase1name" refs
    }

    private static void RemoveVipUses(FortiConfig config, HashSet<string> vips, List<string> removed, Queue<string> queue, HashSet<string> sdwan)
    {
        while (vips.Count > 0)
        {
            var more = new HashSet<string>();
            foreach (var stmt in config.Root.Descendants().ToList())
            {
                if (stmt.Kind != NodeKind.Statement || stmt.Keyword != "set" || stmt.Tokens.Count < 3) continue;
                var path = stmt.ConfigPath;
                var key = stmt.SetKey;
                var isPolicyDst = path.StartsWith("firewall ", StringComparison.Ordinal) && path.Contains("policy") && key is "dstaddr" or "dstaddr6";
                var isGroup = path is "firewall vipgrp" or "firewall vipgrp6" && key == "member";
                if (!isPolicyDst && !isGroup) continue;
                var hits = stmt.Tokens.Skip(2).Where(t => t.Quoted && vips.Contains(t.Text)).ToList();
                if (hits.Count == 0 || IsDetached(stmt)) continue;
                foreach (var h in hits) stmt.Tokens.Remove(h);
                if (stmt.Tokens.Count > 2) continue;
                if (stmt.Parent is { Kind: NodeKind.Edit } entry) RemoveEntry(entry, removed, queue, more, sdwan);
            }
            vips = more;
        }
    }

    /// <summary>SD-WAN health checks and rules refer to members by sequence number.</summary>
    private static void CleanSdwanSeqRefs(FortiConfig config, HashSet<string> seqs, List<string> removed)
    {
        foreach (var root in config.FindConfigs("system sdwan").Concat(config.FindConfigs("system virtual-wan-link")))
        {
            foreach (var stmt in root.Descendants().Where(n => n.Kind == NodeKind.Statement && n.Keyword == "set" && n.SetKey is "members" or "priority-members").ToList())
            {
                var hits = stmt.Tokens.Skip(2).Where(t => seqs.Contains(t.Text)).ToList();
                if (hits.Count == 0) continue;
                foreach (var h in hits) stmt.Tokens.Remove(h);
                if (stmt.Tokens.Count > 2) continue;
                var entry = stmt.Parent;
                if (stmt.SetKey == "priority-members" && entry is { Kind: NodeKind.Edit } && entry.ConfigPath.EndsWith("/service", StringComparison.Ordinal))
                {
                    entry.Parent?.Children.Remove(entry);
                    removed.Add($"{entry.ConfigPath} → edit \"{entry.Name}\"");
                }
                else
                {
                    stmt.Parent?.Children.Remove(stmt);
                    removed.Add($"{stmt.Describe()} (setting removed)");
                }
            }
        }
    }
}
