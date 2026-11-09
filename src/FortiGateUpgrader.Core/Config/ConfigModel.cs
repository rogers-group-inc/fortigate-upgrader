using System.Text;

namespace FortiGateUpgrader.Core.Config;

/// <summary>One whitespace-separated token of a FortiOS statement.</summary>
public sealed class ConfigToken
{
    public ConfigToken(string text, bool quoted, string raw)
    {
        Text = text;
        Quoted = quoted;
        Raw = raw;
    }

    /// <summary>Unescaped value (quotes removed).</summary>
    public string Text { get; private set; }

    /// <summary>True when the token was written in double quotes.</summary>
    public bool Quoted { get; }

    /// <summary>Exactly as it appeared in the source file.</summary>
    public string Raw { get; private set; }

    public void Replace(string newText)
    {
        Text = newText;
        Raw = Quoted ? Quote(newText) : newText;
    }

    public static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public static ConfigToken Create(string value, bool quoted = true) =>
        new(value, quoted, quoted ? Quote(value) : value);

    public override string ToString() => Raw;
}

public enum NodeKind
{
    /// <summary><c>config ...</c> block, closed by <c>end</c>.</summary>
    Config,
    /// <summary><c>edit ...</c> block, closed by <c>next</c>.</summary>
    Edit,
    /// <summary>Any leaf statement: set / unset / append / select / comment etc.</summary>
    Statement,
}

/// <summary>Node of the parsed configuration tree.</summary>
public sealed class ConfigNode
{
    public ConfigNode(NodeKind kind, List<ConfigToken> tokens, int line)
    {
        Kind = kind;
        Tokens = tokens;
        Line = line;
    }

    public NodeKind Kind { get; }
    public List<ConfigToken> Tokens { get; }
    public List<ConfigNode> Children { get; } = new();
    public ConfigNode? Parent { get; internal set; }

    /// <summary>1-based line number in the source file (0 for generated nodes).</summary>
    public int Line { get; }

    public string Keyword => Tokens.Count > 0 ? Tokens[0].Text : "";

    /// <summary>For config nodes: "system interface". For edits: the edit name.</summary>
    public string Name => Kind switch
    {
        NodeKind.Config => string.Join(' ', Tokens.Skip(1).Select(t => t.Text)),
        NodeKind.Edit => Tokens.Count > 1 ? Tokens[1].Text : "",
        _ => Tokens.Count > 1 ? Tokens[1].Text : "",
    };

    /// <summary>For set statements: the key (e.g. "interface").</summary>
    public string? SetKey => Kind == NodeKind.Statement && Tokens.Count > 1 ? Tokens[1].Text : null;

    /// <summary>Values after "set key".</summary>
    public IEnumerable<ConfigToken> Values => Tokens.Skip(2);

    public void Add(ConfigNode child)
    {
        child.Parent = this;
        Children.Add(child);
    }

    public void Insert(int index, ConfigNode child)
    {
        child.Parent = this;
        Children.Insert(index, child);
    }

    /// <summary>
    /// Path of enclosing config blocks, e.g. "system virtual-switch/port".
    /// "global" / "vdom" wrappers of multi-VDOM configs are ignored.
    /// </summary>
    public string ConfigPath
    {
        get
        {
            var parts = new List<string>();
            for (var n = Kind == NodeKind.Config ? this : Parent; n?.Parent != null; n = n.Parent)
            {
                if (n.Kind == NodeKind.Config && n.Name is not ("global" or "vdom"))
                    parts.Add(n.Name);
            }
            parts.Reverse();
            return string.Join('/', parts);
        }
    }

    /// <summary>Human readable location, e.g. "system interface [wan1] set mode".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        for (var n = this; n != null; n = n.Parent)
        {
            if (n.Kind == NodeKind.Config) parts.Add(n.Name);
            else if (n.Kind == NodeKind.Edit) parts.Add($"[{n.Name}]");
            else parts.Add(string.Join(' ', n.Tokens.Take(2).Select(t => t.Text)));
        }
        parts.Reverse();
        return string.Join(' ', parts);
    }

    public ConfigNode? FindSet(string key) =>
        Children.FirstOrDefault(c => c.Kind == NodeKind.Statement && c.Keyword == "set" && c.SetKey == key);

    public IEnumerable<ConfigNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public static ConfigNode NewSet(string key, params ConfigToken[] values)
    {
        var tokens = new List<ConfigToken> { ConfigToken.Create("set", false), ConfigToken.Create(key, false) };
        tokens.AddRange(values);
        return new ConfigNode(NodeKind.Statement, tokens, 0);
    }

    public static ConfigNode NewEdit(string name) =>
        new(NodeKind.Edit, new List<ConfigToken> { ConfigToken.Create("edit", false), ConfigToken.Create(name) }, 0);

    public override string ToString() => string.Join(' ', Tokens.Select(t => t.Raw));
}

/// <summary>A whole configuration file: '#' header lines plus the statement tree.</summary>
public sealed class FortiConfig
{
    public List<string> HeaderLines { get; } = new();
    public ConfigNode Root { get; } = new(NodeKind.Config, new List<ConfigToken>(), 0);

    public string? GetHeader(string key)
    {
        var prefix = "#" + key + "=";
        return HeaderLines.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..];
    }

    public string? Platform => GetHeader("platform");

    public string? Build => GetHeader("build");

    /// <summary>Top-level (or global-scoped) config blocks with the given path, e.g. "system interface".</summary>
    public IEnumerable<ConfigNode> FindConfigs(string path) =>
        Root.Descendants().Where(n => n.Kind == NodeKind.Config && n.ConfigPath == path);

    public string? Hostname =>
        FindConfigs("system global").Select(g => g.FindSet("hostname")).FirstOrDefault(s => s != null)?.Values.FirstOrDefault()?.Text;

    public string? FirmwareDescription
    {
        get
        {
            var v = GetHeader("config-version");
            if (v == null) return null;
            var parts = v.Split(':')[0].Split('-');
            return parts.Length >= 4 ? $"{parts[1]} {parts[3]}" : v;
        }
    }

    public override string ToString() => FortiConfigWriter.Write(this);
}
