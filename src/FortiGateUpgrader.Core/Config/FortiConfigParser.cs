using System.Text;

namespace FortiGateUpgrader.Core.Config;

public sealed class ConfigParseException : Exception
{
    public ConfigParseException(string message) : base(message) { }
}

/// <summary>
/// Parses FortiOS configuration text (full backups and flat FortiManager "preview" exports)
/// into a <see cref="FortiConfig"/> tree. Quoted values may span lines (certificates, scripts).
/// </summary>
public static class FortiConfigParser
{
    public static FortiConfig ParseFile(string path) => Parse(File.ReadAllText(path));

    public static FortiConfig Parse(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];

        var config = new FortiConfig();
        var stack = new Stack<ConfigNode>();
        stack.Push(config.Root);
        var seenStatement = false;

        foreach (var (tokens, line, comment) in Tokenize(text))
        {
            if (comment != null)
            {
                if (!seenStatement) config.HeaderLines.Add(comment);
                else stack.Peek().Add(new ConfigNode(NodeKind.Statement, new() { new ConfigToken(comment, false, comment) }, line));
                continue;
            }
            if (tokens.Count == 0) continue;
            seenStatement = true;

            switch (tokens[0].Text)
            {
                case "config":
                {
                    var node = new ConfigNode(NodeKind.Config, tokens, line);
                    stack.Peek().Add(node);
                    stack.Push(node);
                    break;
                }
                case "edit":
                {
                    if (stack.Peek().Kind == NodeKind.Edit) stack.Pop(); // tolerate a missing "next"
                    var node = new ConfigNode(NodeKind.Edit, tokens, line);
                    stack.Peek().Add(node);
                    stack.Push(node);
                    break;
                }
                case "next":
                    if (stack.Peek().Kind == NodeKind.Edit) stack.Pop();
                    break;
                case "end":
                    if (stack.Peek().Kind == NodeKind.Edit) stack.Pop(); // tolerate a missing "next"
                    if (stack.Count > 1) stack.Pop();
                    break;
                default:
                    stack.Peek().Add(new ConfigNode(NodeKind.Statement, tokens, line));
                    break;
            }
        }

        if (stack.Count > 1)
            throw new ConfigParseException($"Configuration ended inside an open block (\"{stack.Peek().Describe()}\"). The file may be truncated.");
        if (config.Root.Children.Count == 0)
            throw new ConfigParseException("No FortiOS configuration statements were found in the file.");
        return config;
    }

    /// <summary>Splits text into statements (token lists). Newlines inside quotes do not end a statement.</summary>
    private static IEnumerable<(List<ConfigToken> Tokens, int Line, string? Comment)> Tokenize(string text)
    {
        var tokens = new List<ConfigToken>();
        var raw = new StringBuilder();
        var value = new StringBuilder();
        var line = 1;
        var statementLine = 1;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\n')
            {
                if (tokens.Count > 0) yield return (tokens, statementLine, null);
                tokens = new List<ConfigToken>();
                line++;
                i++;
                statementLine = line;
                continue;
            }
            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            // Comment / header line: '#' at the start of a statement.
            if (c == '#' && tokens.Count == 0)
            {
                var end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                yield return (tokens, line, text[i..end].TrimEnd());
                i = end;
                continue;
            }

            raw.Clear();
            value.Clear();
            if (c is '"' or '\'')
            {
                var quote = c;
                raw.Append(c);
                i++;
                var closed = false;
                while (i < text.Length)
                {
                    var q = text[i];
                    if (q == '\\' && quote == '"' && i + 1 < text.Length)
                    {
                        raw.Append(q).Append(text[i + 1]);
                        value.Append(text[i + 1]);
                        if (text[i + 1] == '\n') line++;
                        i += 2;
                        continue;
                    }
                    raw.Append(q);
                    i++;
                    if (q == quote)
                    {
                        closed = true;
                        break;
                    }
                    if (q == '\n') line++;
                    value.Append(q);
                }
                if (!closed)
                    throw new ConfigParseException($"Unterminated quoted value starting on line {statementLine}.");
                tokens.Add(new ConfigToken(value.ToString(), quote == '"', raw.ToString()));
            }
            else
            {
                while (i < text.Length && text[i] is not (' ' or '\t' or '\n'))
                {
                    raw.Append(text[i]);
                    i++;
                }
                tokens.Add(new ConfigToken(raw.ToString(), false, raw.ToString()));
            }
        }
        if (tokens.Count > 0) yield return (tokens, statementLine, null);
    }
}
