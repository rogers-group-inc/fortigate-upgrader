using System.Text;

namespace FortiGateUpgrader.Core.Config;

/// <summary>Writes a <see cref="FortiConfig"/> back to text using standard 4-space FortiOS indentation.</summary>
public static class FortiConfigWriter
{
    public static string Write(FortiConfig config)
    {
        var sb = new StringBuilder();
        foreach (var h in config.HeaderLines) sb.Append(h).Append('\n');
        foreach (var child in config.Root.Children) WriteNode(sb, child, 0);
        return sb.ToString();
    }

    public static void WriteFile(FortiConfig config, string path) =>
        File.WriteAllText(path, Write(config), new UTF8Encoding(false));

    private static void WriteNode(StringBuilder sb, ConfigNode node, int depth)
    {
        var indent = new string(' ', depth * 4);
        sb.Append(indent).AppendJoin(' ', node.Tokens.Select(t => t.Raw)).Append('\n');
        switch (node.Kind)
        {
            case NodeKind.Config:
                foreach (var c in node.Children) WriteNode(sb, c, depth + 1);
                sb.Append(indent).Append("end\n");
                break;
            case NodeKind.Edit:
                foreach (var c in node.Children) WriteNode(sb, c, depth + 1);
                sb.Append(indent).Append("next\n");
                break;
        }
    }
}
