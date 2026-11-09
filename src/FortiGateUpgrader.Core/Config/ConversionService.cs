using FortiGateUpgrader.Core.Models;

namespace FortiGateUpgrader.Core.Config;

public enum Severity { Info, Warning, Error }

public sealed record ConversionMessage(Severity Severity, string Message);

public sealed record RenameRecord(string Location, string OldName, string NewName);

public sealed class ConversionRequest
{
    public required FortiConfig Source { get; init; }
    public required ModelProfile SourceModel { get; init; }
    public required ModelProfile TargetModel { get; init; }

    /// <summary>Source physical port name → target physical port name.</summary>
    public required IReadOnlyDictionary<string, string> Mapping { get; init; }

    /// <summary>New local ID / clear-text pre-shared key per IPsec tunnel, keyed by <see cref="IpsecTunnel.Key"/>.</summary>
    public IReadOnlyDictionary<string, IpsecCredentials> Ipsec { get; init; } = new Dictionary<string, IpsecCredentials>();

    /// <summary>When false, tunnels without credentials are left as they are (used to list the tunnels before keys are entered).</summary>
    public bool RequireIpsecCredentials { get; init; } = true;

    public string? NewHostname { get; init; }

    /// <summary>
    /// Switch/aggregate interface name → its new member ports (target port names). A group with an
    /// empty list (and no other members) is not converted. When null, members follow the port mapping.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? GroupMembers { get; init; }
}

public sealed class ConversionResult
{
    public required FortiConfig Output { get; init; }
    public List<RenameRecord> Renames { get; } = new();
    public List<ConversionMessage> Messages { get; } = new();
    public List<IgnoredMatch> Ignored { get; } = new();
    /// <summary>Source ports (and virtual ports) that were not converted.</summary>
    public List<string> RemovedInterfaces { get; } = new();
    /// <summary>Switch/aggregate interfaces left without ports, so not converted.</summary>
    public List<string> RemovedGroups { get; } = new();
    /// <summary>Final members of each converted switch/aggregate interface.</summary>
    public Dictionary<string, IReadOnlyList<string>> GroupMembers { get; } = new();
    /// <summary>Everything removed along with them, per removed port or group.</summary>
    public Dictionary<string, List<string>> RemovedDependants { get; } = new();
    /// <summary>IPsec tunnels in the output that need a pre-shared key.</summary>
    public List<IpsecTunnel> Tunnels { get; } = new();
    /// <summary>Tunnels whose local ID and pre-shared key were replaced.</summary>
    public List<string> UpdatedTunnels { get; } = new();
    public List<string> AddedInterfaces { get; } = new();
    public Dictionary<string, string> EffectiveMapping { get; } = new();

    public bool HasErrors => Messages.Any(m => m.Severity == Severity.Error);
}

public sealed class ConversionException : Exception
{
    public ConversionException(IEnumerable<string> problems) : base(string.Join(Environment.NewLine, problems))
    {
        Problems = problems.ToList();
    }

    public IReadOnlyList<string> Problems { get; }
}

/// <summary>Converts a configuration from one FortiGate model to another by remapping physical interfaces.</summary>
public static class ConversionService
{
    private static readonly string[] DroppedHeaders = { "serialno", "hwrev", "mgmt.data", "mgmt.dat2" };

    public static ConversionResult Convert(ConversionRequest req)
    {
        var src = req.SourceModel;
        var dst = req.TargetModel;
        var usage = InterfaceUsageAnalyzer.Analyze(req.Source, src);

        Validate(req, usage);

        // Work on a deep copy so the loaded source stays untouched.
        var output = FortiConfigParser.Parse(FortiConfigWriter.Write(req.Source));
        var result = new ConversionResult { Output = output };

        // ---- Switch/aggregate interfaces: take the old physical members out now; the new members
        //      (chosen on the groups tab) are added after renaming.
        var groups = InterfaceGroups.Find(output, src);
        var groupMembers = ResolveGroupMembers(req, groups);
        InterfaceGroups.StripPhysicalMembers(output, groups);

        // ---- Build the full rename map (physical + virtual ports). Anything unmapped is not converted.
        var mapping = new Dictionary<string, string>(req.Mapping);
        var remove = new List<string>();
        foreach (var v in src.VirtualPorts)
        {
            if (dst.VirtualPorts.Contains(v)) mapping[v] = v;
            else if (usage[v].Defined || usage[v].InUse) remove.Add(v);
        }
        foreach (var p in src.Ports)
        {
            if (mapping.ContainsKey(p.Name)) continue;
            if (usage[p.Name].Defined || usage[p.Name].InUse) remove.Add(p.Name);
        }
        foreach (var kv in mapping) result.EffectiveMapping[kv.Key] = kv.Value;

        // ---- Remove unmapped ports and everything that depends on them (before renaming, so a
        //      removed port1 can't be confused with a target port1 that another port is mapped to).
        foreach (var name in remove.Distinct())
        {
            var gone = InterfaceRemover.Remove(output, new[] { name });
            result.RemovedInterfaces.Add(name);
            result.RemovedDependants[name] = gone.Where(g => g != $"system interface → edit \"{name}\"").ToList();
        }

        // ---- Groups left without any member are not converted either.
        foreach (var g in groups.Where(g => groupMembers[g.Name].Count == 0 && g.OtherMembers.Count == 0))
        {
            InterfaceGroups.RemoveOwnerEntry(output, g);
            var gone = InterfaceRemover.Remove(output, new[] { g.Name });
            result.RemovedGroups.Add(g.Name);
            result.RemovedDependants[g.Name] = gone.Where(x => x != $"system interface → edit \"{g.Name}\"").ToList();
        }

        // ---- Name collisions: a logical interface (VLAN, tunnel, ...) already uses a target port name.
        var remainingDefs = output.FindConfigs("system interface").SelectMany(c => c.Children)
            .Where(e => e.Kind == NodeKind.Edit).Select(e => e.Name).ToHashSet();
        foreach (var t in dst.Ports.Select(p => p.Name).Concat(dst.VirtualPorts))
        {
            if (remainingDefs.Contains(t) && !mapping.ContainsKey(t))
                result.Messages.Add(new(Severity.Error, $"The configuration already has an interface named \"{t}\" that is not a {src.Model} port. It would clash with the {dst.Model}'s physical port \"{t}\"; rename it on the old firewall first."));
        }

        // ---- Simultaneous rename: every token is looked up once in the original-name map,
        //      so swaps (port1↔port3) and identity mappings are safe.
        var renameMap = mapping.Where(kv => kv.Key != kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
        var refs = InterfaceReferences.Find(output, renameMap.Keys.ToHashSet(), result.Ignored);
        foreach (var r in refs)
        {
            var newName = renameMap[r.Name];
            result.Renames.Add(new RenameRecord(r.Location, r.Name, newName));
            r.Token.Replace(newName);
        }

        foreach (var g in groups.Where(g => !result.RemovedGroups.Contains(g.Name)))
        {
            InterfaceGroups.AddMembers(output, g, groupMembers[g.Name]);
            result.GroupMembers[g.Name] = groupMembers[g.Name];
        }

        ApplyIpsec(req, output, result);
        AddMissingTargetPorts(output, dst, result);
        RewriteHeader(output, src, dst, result);
        if (!string.IsNullOrWhiteSpace(req.NewHostname)) SetHostname(output, req.NewHostname.Trim());
        AddWarnings(req, output, mapping, result);
        return result;
    }

    /// <summary>Problems that make a conversion impossible (the UI prevents these, the engine re-checks).</summary>
    public static List<string> FindBlockingProblems(ConversionRequest req, Dictionary<string, PortUsage>? usage = null)
    {
        usage ??= InterfaceUsageAnalyzer.Analyze(req.Source, req.SourceModel);
        var problems = new List<string>();
        foreach (var (s, t) in req.Mapping)
        {
            if (req.SourceModel.FindPort(s) == null) problems.Add($"\"{s}\" is not a {req.SourceModel.Model} port.");
            if (req.TargetModel.FindPort(t) == null) problems.Add($"\"{t}\" is not a {req.TargetModel.Model} port.");
        }
        foreach (var g in req.Mapping.GroupBy(kv => kv.Value).Where(g => g.Count() > 1))
            problems.Add($"{req.TargetModel.Model} port \"{g.Key}\" has more than one source port: {string.Join(", ", g.Select(x => x.Key))}.");
        if (req.GroupMembers != null)
        {
            var groups = InterfaceGroups.Find(req.Source, req.SourceModel);
            foreach (var (name, ports) in req.GroupMembers)
            {
                var g = groups.FirstOrDefault(x => x.Name == name);
                if (g == null) { problems.Add($"\"{name}\" is not a switch or aggregate interface in this configuration."); continue; }
                foreach (var p in ports)
                {
                    var tp = req.TargetModel.FindPort(p);
                    if (tp == null) { problems.Add($"\"{p}\" (member of {name}) is not a {req.TargetModel.Model} port."); continue; }
                    if (g.Kind == GroupKind.HardwareSwitch && tp.SwitchCapable == false)
                        problems.Add($"{p} can't be a hardware switch member on the {req.TargetModel.Model} (hardware switch \"{name}\").");
                    foreach (var (s, t) in req.Mapping.Where(kv => kv.Value == p))
                    {
                        if (usage.TryGetValue(s, out var u) && u.InUse && !u.OnlyGroupMember && !g.PhysicalMembers.Contains(s))
                            problems.Add($"{p} receives {s}'s settings and can't also be a member of \"{name}\".");
                    }
                }
            }
            foreach (var dup in req.GroupMembers.SelectMany(kv => kv.Value.Select(p => (Port: p, Group: kv.Key))).GroupBy(x => x.Port).Where(x => x.Count() > 1))
                problems.Add($"{dup.Key} is in more than one group: {string.Join(", ", dup.Select(x => x.Group))}.");
        }
        return problems;
    }

    /// <summary>New members per group: as given, or (when not given) the targets of the old members in the port mapping.</summary>
    private static Dictionary<string, IReadOnlyList<string>> ResolveGroupMembers(ConversionRequest req, List<InterfaceGroup> groups)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var g in groups)
        {
            if (req.GroupMembers != null)
                result[g.Name] = req.GroupMembers.TryGetValue(g.Name, out var m) ? m.Distinct().ToList() : new List<string>();
            else
                result[g.Name] = g.PhysicalMembers.Where(req.Mapping.ContainsKey).Select(p => req.Mapping[p]).Distinct().ToList();
        }
        return result;
    }

    private static void Validate(ConversionRequest req, Dictionary<string, PortUsage> usage)
    {
        var problems = FindBlockingProblems(req, usage);
        if (problems.Count > 0) throw new ConversionException(problems);
    }

    /// <summary>Lists the tunnels that remain after conversion and writes the user's new credentials into them.</summary>
    private static void ApplyIpsec(ConversionRequest req, FortiConfig output, ConversionResult result)
    {
        var tunnels = IpsecTunnels.Find(output);
        result.Tunnels.AddRange(tunnels.Where(t => t.NeedsPsk));

        var missing = new List<string>();
        foreach (var t in tunnels.Where(t => t.NeedsPsk))
        {
            req.Ipsec.TryGetValue(t.Key, out var c);
            var problem = IpsecTunnels.Validate(c);
            if (problem != null)
            {
                if (req.RequireIpsecCredentials) missing.Add($"IPsec tunnel \"{t.Name}\": {problem}");
                continue;
            }
            var entry = output.FindConfigs(t.Section).SelectMany(x => x.Children).First(e => e.Kind == NodeKind.Edit && e.Name == t.Name);
            IpsecTunnels.Apply(entry, c!);
            result.UpdatedTunnels.Add(t.Name);
            if (t.HasRemotePsk)
                result.Messages.Add(new(Severity.Warning, $"IPsec tunnel \"{t.Name}\" also has a psksecret-remote (encrypted). It was left as is and must be re-entered on the new firewall."));
        }
        if (missing.Count > 0) throw new ConversionException(missing);

        foreach (var t in tunnels.Where(t => !t.NeedsPsk))
            result.Messages.Add(new(Severity.Info, $"IPsec tunnel \"{t.Name}\" uses certificates; its certificate must be installed on the new firewall."));
        if (result.UpdatedTunnels.Count > 0)
            result.Messages.Add(new(Severity.Warning, $"The new pre-shared keys for {result.UpdatedTunnels.Count} IPsec tunnel(s) are in this file in clear text. Keep the file private and delete it once the new firewall is set up."));
    }

    private static void AddMissingTargetPorts(FortiConfig output, ModelProfile dst, ConversionResult result)
    {
        var sysIf = output.FindConfigs("system interface").FirstOrDefault();
        if (sysIf == null) return;

        var edits = sysIf.Children.Where(e => e.Kind == NodeKind.Edit).ToList();
        var existing = edits.Select(e => e.Name).ToHashSet();
        var vdom = edits.Select(e => e.FindSet("vdom")?.Values.FirstOrDefault()?.Text).FirstOrDefault(v => v != null) ?? "root";

        // Insert after the last physical port definition so the physical ports stay together.
        var physical = dst.Ports.Select(p => p.Name).ToHashSet();
        var insertAt = sysIf.Children.FindLastIndex(e => e.Kind == NodeKind.Edit && physical.Contains(e.Name)) + 1;

        foreach (var port in dst.Ports.Where(p => !existing.Contains(p.Name)))
        {
            var edit = ConfigNode.NewEdit(port.Name);
            edit.Add(ConfigNode.NewSet("vdom", ConfigToken.Create(vdom)));
            edit.Add(ConfigNode.NewSet("type", ConfigToken.Create("physical", false)));
            sysIf.Insert(insertAt++, edit);
            result.AddedInterfaces.Add(port.Name);
        }
    }

    private static void RewriteHeader(FortiConfig output, ModelProfile src, ModelProfile dst, ConversionResult result)
    {
        var h = output.HeaderLines;
        h.RemoveAll(l => DroppedHeaders.Any(k => l.StartsWith("#" + k + "=", StringComparison.OrdinalIgnoreCase)));

        var cvIndex = h.FindIndex(l => l.StartsWith("#config-version=", StringComparison.OrdinalIgnoreCase));
        if (cvIndex >= 0)
        {
            var value = h[cvIndex]["#config-version=".Length..];
            var dash = value.IndexOf('-');
            var oldPrefix = dash > 0 ? value[..dash] : value;
            if (!oldPrefix.Equals(src.ConfigPrefix, StringComparison.OrdinalIgnoreCase))
                result.Messages.Add(new(Severity.Warning, $"Header model \"{oldPrefix}\" does not match the expected {src.ConfigPrefix}."));
            h[cvIndex] = "#config-version=" + dst.ConfigPrefix + (dash > 0 ? value[dash..] : "");
        }
        else
        {
            result.Messages.Add(new(Severity.Warning, "The file has no #config-version header. FortiOS needs it to restore a backup; this file may only be usable by pasting into the CLI."));
        }

        var pIndex = h.FindIndex(l => l.StartsWith("#platform=", StringComparison.OrdinalIgnoreCase));
        if (pIndex >= 0) h[pIndex] = "#platform=" + dst.Platform;
    }

    private static void SetHostname(FortiConfig output, string hostname)
    {
        var global = output.FindConfigs("system global").FirstOrDefault();
        if (global == null) return;
        var set = global.FindSet("hostname");
        if (set != null && set.Tokens.Count > 2) set.Tokens[2].Replace(hostname);
        else global.Insert(0, ConfigNode.NewSet("hostname", ConfigToken.Create(hostname)));
    }

    private static void AddWarnings(ConversionRequest req, FortiConfig output, Dictionary<string, string> mapping, ConversionResult result)
    {
        var src = req.SourceModel;
        var dst = req.TargetModel;
        var msgs = result.Messages;

        var firmware = req.Source.FirmwareDescription ?? "the same FortiOS version";
        msgs.Add(new(Severity.Info, $"Before restoring, load the {dst.Model} with the same firmware the source config came from ({firmware}). Restore this file, confirm it, then upgrade along Fortinet's supported upgrade path."));

        if (!dst.Verified)
            msgs.Add(new(Severity.Warning, $"The {dst.Model} port profile was built from documentation and has not been checked against a real {dst.Model} config. Confirm the interface names (run \"show system interface ?\" on the new box) before deploying."));

        var global = output.FindConfigs("system global").FirstOrDefault();
        if (global?.FindSet("private-data-encryption")?.Values.FirstOrDefault()?.Text == "enable")
            msgs.Add(new(Severity.Warning, "private-data-encryption is enabled. Encrypted secrets (ENC ...) will only load if the new firewall is configured with the same private data encryption key."));

        foreach (var (s, t) in mapping)
        {
            var sp = src.FindPort(s);
            var tp = dst.FindPort(t);
            if (sp == null || tp == null) continue;
            if (sp.IsOptical != tp.IsOptical && (sp.IsOptical || tp.IsOptical))
                msgs.Add(new(Severity.Info, $"{s} ({sp.MediaDescription}) moves to {t} ({tp.MediaDescription}). Check cabling/transceivers."));

            var def = InterfaceReferences.FindDefinition(output, t);
            var speed = def?.FindSet("speed")?.Values.FirstOrDefault()?.Text;
            if (speed != null && !tp.AllowedSpeeds.Contains(speed))
                msgs.Add(new(Severity.Warning, $"{t}: \"set speed {speed}\" (from {s}) is likely not valid on a {tp.MediaDescription} port. Review it, or remove it to use auto."));
        }

        // Hardware switch members must be ports the target's switch chip supports.
        foreach (var vs in output.FindConfigs("system virtual-switch").SelectMany(c => c.Children).Where(c => c.Kind == NodeKind.Edit))
        {
            var members = vs.Descendants().Where(d => d.Kind == NodeKind.Edit && d.ConfigPath == "system virtual-switch/port").Select(d => d.Name);
            foreach (var m in members)
            {
                var tp = dst.FindPort(m);
                if (tp?.SwitchCapable == false)
                    msgs.Add(new(Severity.Error, $"Hardware switch \"{vs.Name}\" includes {m}, which cannot be a hardware switch member on the {dst.Model}. Use a different port, or change the switch to a software switch."));
                else if (tp != null && tp.SwitchCapable == null)
                    msgs.Add(new(Severity.Warning, $"Hardware switch \"{vs.Name}\" includes {m}. It is not confirmed that this port can join the {dst.Model}'s hardware switch; check after restoring."));
            }
        }

        // Aggregates should not mix speeds.
        foreach (var e in output.FindConfigs("system interface").SelectMany(c => c.Children).Where(c => c.Kind == NodeKind.Edit))
        {
            var type = e.FindSet("type")?.Values.FirstOrDefault()?.Text;
            if (type is not ("aggregate" or "redundant")) continue;
            var ports = (e.FindSet("member")?.Values ?? Enumerable.Empty<ConfigToken>()).Select(v => dst.FindPort(v.Text)).Where(p => p != null).ToList();
            if (ports.Select(p => p!.MaxSpeedMbps).Distinct().Count() > 1)
                msgs.Add(new(Severity.Warning, $"{type} \"{e.Name}\" mixes ports of different speeds ({string.Join(", ", ports.Select(p => $"{p!.Name} {p.MediaDescription}"))})."));
        }

        if (result.Renames.Any(r => r.Location.StartsWith("system ha", StringComparison.Ordinal)))
            msgs.Add(new(Severity.Info, "HA heartbeat/monitor interfaces were renamed. If this unit is in an HA cluster, both members must be replaced and use the same mapping."));

        foreach (var name in result.RemovedGroups)
        {
            var deps = result.RemovedDependants[name];
            msgs.Add(new(deps.Count == 0 ? Severity.Info : Severity.Warning,
                $"\"{name}\" has no ports on the {dst.Model}, so it was not converted." + (deps.Count == 0 ? "" :
                $" {deps.Count} item(s) that depended on it were removed too: " + string.Join("; ", deps.Take(8)) + (deps.Count > 8 ? $"; … and {deps.Count - 8} more (see the report)." : "."))));
        }

        foreach (var name in result.RemovedInterfaces)
        {
            var deps = result.RemovedDependants[name];
            if (deps.Count == 0)
                msgs.Add(new(Severity.Info, $"\"{name}\" was not mapped, so it was not converted."));
            else
                msgs.Add(new(Severity.Warning, $"\"{name}\" was not mapped, so it was not converted. {deps.Count} item(s) that depended on it were removed too: " +
                    string.Join("; ", deps.Take(8)) + (deps.Count > 8 ? $"; … and {deps.Count - 8} more (see the report)." : ".")));
            if (deps.Any(d => d.StartsWith("system sdwan", StringComparison.Ordinal) || d.StartsWith("system virtual-wan-link", StringComparison.Ordinal)))
                msgs.Add(new(Severity.Warning, $"SD-WAN members on \"{name}\" were removed. Check the SD-WAN rules and health checks."));
        }
    }
}
