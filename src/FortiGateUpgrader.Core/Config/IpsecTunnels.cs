namespace FortiGateUpgrader.Core.Config;

/// <summary>An IPsec phase 1 (interface- or policy-based) as found in a configuration.</summary>
public sealed record IpsecTunnel(
    string Section, string Name, string? Interface, string? RemoteGateway, string Type,
    string AuthMethod, string? LocalId, string? Comments, bool HasRemotePsk)
{
    /// <summary>Stable key for user input: "vpn ipsec phase1-interface/HQ".</summary>
    public string Key => Section + "/" + Name;

    /// <summary>Tunnels using certificates don't have a pre-shared key.</summary>
    public bool NeedsPsk => AuthMethod != "signature";

    public string Kind => Section.EndsWith("-interface", StringComparison.Ordinal) ? "Route-based" : "Policy-based";
}

/// <summary>New phase 1 credentials typed by the user, in clear text.</summary>
public sealed record IpsecCredentials(string LocalId, string PreSharedKey);

public static class IpsecTunnels
{
    public static readonly string[] Sections = { "vpn ipsec phase1-interface", "vpn ipsec phase1" };

    public const int MinPskLength = 6;
    public const int MaxPskLength = 128;
    public const int MaxLocalIdLength = 63;

    public static List<IpsecTunnel> Find(FortiConfig config) =>
        Sections.SelectMany(s => config.FindConfigs(s).SelectMany(c => c.Children)
                .Where(e => e.Kind == NodeKind.Edit)
                .Select(e => new IpsecTunnel(
                    s, e.Name,
                    Value(e, "interface"),
                    Value(e, "remote-gw") ?? Value(e, "remotegw-ddns"),
                    Value(e, "type") ?? "static",
                    Value(e, "authmethod") ?? "psk",
                    Value(e, "localid"),
                    Value(e, "comments"),
                    e.FindSet("psksecret-remote") != null)))
            .ToList();

    private static string? Value(ConfigNode edit, string key) => edit.FindSet(key)?.Values.FirstOrDefault()?.Text;

    /// <summary>Returns why the credentials can't be used, or null when they are fine.</summary>
    public static string? Validate(IpsecCredentials? c)
    {
        if (c == null || string.IsNullOrWhiteSpace(c.LocalId) && string.IsNullOrEmpty(c.PreSharedKey)) return "Enter a local ID and a pre-shared key.";
        if (string.IsNullOrWhiteSpace(c.LocalId)) return "Enter a local ID.";
        if (c.LocalId.Trim().Length > MaxLocalIdLength) return $"The local ID can be at most {MaxLocalIdLength} characters.";
        if (string.IsNullOrEmpty(c.PreSharedKey)) return "Enter a pre-shared key.";
        if (c.PreSharedKey.Length is < MinPskLength or > MaxPskLength) return $"The pre-shared key must be {MinPskLength}–{MaxPskLength} characters.";
        if (BadChar(c.LocalId) || BadChar(c.PreSharedKey)) return "Double quotes, backslashes and control characters aren't allowed (FortiOS escaping of these is unreliable).";
        return null;
    }

    private static bool BadChar(string s) => s.Any(ch => ch == '"' || ch == '\\' || char.IsControl(ch));

    /// <summary>Writes the new local ID and clear-text PSK into a phase 1 entry.</summary>
    public static void Apply(ConfigNode phase1, IpsecCredentials c)
    {
        SetValue(phase1, "localid", c.LocalId.Trim(), afterKey: "peertype");
        SetValue(phase1, "psksecret", c.PreSharedKey, afterKey: "localid");
    }

    private static void SetValue(ConfigNode edit, string key, string value, string afterKey)
    {
        var set = edit.FindSet(key);
        if (set != null)
        {
            set.Tokens.RemoveRange(2, set.Tokens.Count - 2);
            set.Tokens.Add(ConfigToken.Create(value));
            return;
        }
        var anchor = edit.FindSet(afterKey);
        var node = ConfigNode.NewSet(key, ConfigToken.Create(value));
        if (anchor != null) edit.Insert(edit.Children.IndexOf(anchor) + 1, node);
        else edit.Add(node);
    }
}
