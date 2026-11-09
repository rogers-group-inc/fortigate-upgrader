using System.Net;
using System.Text;

namespace FortiGateUpgrader.Core.Config;

/// <summary>Produces a self-contained HTML report of a conversion for human review.</summary>
public static class ReportWriter
{
    /// <param name="manualEdits">Set when the user edited the source and/or converted config by hand in the app.</param>
    public static string ToHtml(ConversionRequest req, ConversionResult result, string sourceFile, string outputFile, string? manualEdits = null)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var sb = new StringBuilder();
        sb.Append("""
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><title>FortiGate conversion report</title>
<style>
:root{--bg:#fff;--fg:#1d1f23;--muted:#5d6470;--line:#d9dde3;--err:#b42318;--warn:#a15c07;--info:#1f5fa8;--head:#f3f5f8}
@media (prefers-color-scheme: dark){:root{--bg:#16181c;--fg:#e6e8eb;--muted:#9aa3ad;--line:#2f343b;--err:#f97066;--warn:#fdb022;--info:#7cb4f5;--head:#1f2228}}
body{background:var(--bg);color:var(--fg);font:14px/1.5 "Segoe UI",system-ui,sans-serif;margin:0 auto;max-width:1100px;padding:24px 16px}
h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid var(--line);padding-bottom:4px}
.muted{color:var(--muted)}table{border-collapse:collapse;width:100%;font-size:13px}
th,td{border:1px solid var(--line);padding:4px 8px;text-align:left;vertical-align:top}th{background:var(--head)}
code{font-family:Consolas,monospace}.Error{color:var(--err);font-weight:600}.Warning{color:var(--warn);font-weight:600}.Info{color:var(--info)}
.banner{border:2px solid var(--warn);padding:10px 14px;border-radius:6px;margin:16px 0}
</style></head><body>
""");
        sb.Append($"<h1>FortiGate {E(req.SourceModel.Model)} → {E(req.TargetModel.Model)} conversion report</h1>");
        sb.Append($"<div class=muted>Generated {DateTime.Now:yyyy-MM-dd HH:mm} · Hostname <b>{E(result.Output.Hostname)}</b> · Firmware {E(req.Source.FirmwareDescription)}</div>");
        sb.Append($"<div class=muted>Source file: <code>{E(sourceFile)}</code><br>Output file: <code>{E(outputFile)}</code></div>");
        sb.Append("<div class=banner><b>Human review required.</b> This configuration was generated automatically. A network engineer must review it, and it should be restored on a lab/staging unit, before it is deployed to production.</div>");
        if (!string.IsNullOrEmpty(manualEdits))
            sb.Append($"<div class=banner><b>Edited by hand.</b> {E(manualEdits)} The rename list below covers the automatic conversion only; review the hand edits separately.</div>");

        sb.Append("<h2>Port mapping</h2><table><tr><th>Source port</th><th>Target port</th></tr>");
        foreach (var (s, t) in result.EffectiveMapping.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var sp = req.SourceModel.FindPort(s);
            var tp = req.TargetModel.FindPort(t);
            sb.Append($"<tr><td><code>{E(s)}</code> {E(sp == null ? "(virtual)" : $"label {sp.Label}, {sp.MediaDescription}")}</td><td><code>{E(t)}</code> {E(tp == null ? "(virtual)" : $"label {tp.Label}, {tp.MediaDescription}")}</td></tr>");
        }
        sb.Append("</table>");
        if (result.RemovedInterfaces.Count > 0)
        {
            sb.Append($"<p>Not mapped, so not converted: {string.Join(", ", result.RemovedInterfaces.Select(x => $"<code>{E(x)}</code>"))}</p>");
            var deps = result.RemovedDependants.Where(kv => kv.Value.Count > 0).ToList();
            if (deps.Count > 0)
            {
                sb.Append("<h3>Removed because they depended on an unmapped port</h3><table><tr><th>Port</th><th>Removed</th></tr>");
                foreach (var (port, items) in deps)
                    foreach (var item in items) sb.Append($"<tr><td><code>{E(port)}</code></td><td>{E(item)}</td></tr>");
                sb.Append("</table>");
            }
        }
        if (result.UpdatedTunnels.Count > 0)
            sb.Append($"<p>IPsec tunnels given a new local ID and pre-shared key: {string.Join(", ", result.UpdatedTunnels.Select(x => $"<code>{E(x)}</code>"))}. The keys are in the .conf file in clear text and are not shown in this report.</p>");
        if (result.AddedInterfaces.Count > 0)
            sb.Append($"<p>Added with default settings: {string.Join(", ", result.AddedInterfaces.Select(x => $"<code>{E(x)}</code>"))}</p>");

        sb.Append("<h2>Messages</h2><table><tr><th>Severity</th><th>Message</th></tr>");
        foreach (var m in result.Messages.OrderByDescending(m => m.Severity))
            sb.Append($"<tr><td class={m.Severity}>{m.Severity}</td><td>{E(m.Message)}</td></tr>");
        sb.Append("</table>");

        sb.Append($"<h2>Renamed references ({result.Renames.Count})</h2><table><tr><th>Location</th><th>Old</th><th>New</th></tr>");
        foreach (var r in result.Renames)
            sb.Append($"<tr><td>{E(r.Location)}</td><td><code>{E(r.OldName)}</code></td><td><code>{E(r.NewName)}</code></td></tr>");
        sb.Append("</table>");

        var ignored = result.Ignored.Where(i => !i.Reason.StartsWith("belongs to another device", StringComparison.Ordinal)).ToList();
        var foreign = result.Ignored.Count - ignored.Count;
        sb.Append($"<h2>Matches left unchanged ({ignored.Count})</h2>");
        sb.Append("<p class=muted>These tokens have the same text as a mapped port but are not interface references (object names, group members, etc.). Check that none of them should have changed.");
        if (foreign > 0) sb.Append($" {foreign} more matches inside FortiSwitch/FortiAP/FortiExtender sections were also left alone; those are ports on the other devices.");
        sb.Append("</p>");
        if (ignored.Count > 0)
        {
            sb.Append("<table><tr><th>Location</th><th>Text</th><th>Why unchanged</th></tr>");
            foreach (var i in ignored)
                sb.Append($"<tr><td>{E(i.Location)}</td><td><code>{E(i.Name)}</code></td><td>{E(i.Reason)}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Restore checklist</h2><ol>");
        sb.Append($"<li>Install the firmware the source was running ({E(req.Source.FirmwareDescription)}) on the new {E(req.TargetModel.Model)}.</li>");
        sb.Append("<li>System → Configuration → Restore, and select the converted file (or use <code>execute restore config</code>).</li>");
        sb.Append("<li>After reboot, run <code>diagnose debug config-error-log read</code> and resolve any lines it reports.</li>");
        sb.Append("<li>Check interfaces, SD-WAN, VPNs, HA and FortiLink, then upgrade the firmware along Fortinet's upgrade path if needed.</li>");
        sb.Append("</ol></body></html>");
        return sb.ToString();
    }
}
