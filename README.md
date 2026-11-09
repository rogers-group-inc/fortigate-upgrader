# FortiGate Config Upgrader

A Windows app that converts a FortiGate configuration backup from one model to another by remapping the physical interfaces. You draw the port mapping by dragging lines between two front-panel views.

Supported upgrade paths (set in [profiles/upgrade-paths.json](profiles/upgrade-paths.json)):

| From | To |
|---|---|
| 61F | 91G, 101F, 201G |
| 91G | 101F, 201G |

## Using it

1. **Open config.** Pick a `.conf` backup or a FortiManager config preview (`.txt`). You can also drag the file onto the window or onto the exe. The source model comes from the `#platform=` header.
2. **New model.** Pick the target model. Ports are auto-mapped by front-panel label first (WAN1 → `x1` "WAN1", 3 → `port3`, A → `a`), then by matching interface name.
3. **Map the ports.**
   - Drag from a current port to a new port. Drag a connected port to move its line, or drop it on empty space to remove it.
   - Click a line and press Delete to remove it. Esc cancels a drag.
   - Line colors:
     - **Green**: one port to one port.
     - **Red**: two ports on one port, or one port to two.
   - **Amber outline**: the port carries configuration but isn't mapped.
   - **Blue dot**: the port carries configuration.
   - Hover or click a port to see where it's referenced.
   - You don't have to map every port. **Unmapped ports are not converted.** The port and everything that depends on it are left out of the new config, and each removal is listed in the warnings and the report. That includes VLANs, tunnels and aggregates built on it, plus the policies, routes, DHCP servers, VIPs and SD-WAN members that use it. Lists such as zone members and policy interfaces just lose that one entry.
4. **IPsec keys.** The old pre-shared keys are encrypted for the old firewall and won't work on the new one. This tab lists every IPsec tunnel the new config will keep. Tunnels on unmapped ports are already gone, and certificate-based tunnels are skipped. Enter a new **local ID** and **pre-shared key** (clear text, 6–128 characters) for each one. The converted config isn't generated, and Export stays disabled, until every tunnel is filled in. The keys are only kept in memory. They're written into the exported `.conf` in clear text and are never put in the report.
5. **Compare & edit.** The original config is on the left and the converted config is on the right, side by side:
   - The right side highlights what changed. Changed lines get a light green background, and the exact changed text is shaded darker, e.g. `wan1` → `x1`. A red bar marks where lines were removed. The left side shows the original lines in red.
   - Indentation-only differences are ignored, so flat FortiManager exports compare cleanly. **Previous/Next change** steps through the differences, and **Scroll together** keeps the matching lines of both sides in view.
   - Both sides are editable:
     - **Left:** fix the original, then click **Apply changes**. The mapping and keys are kept. **Revert** drops unapplied edits, **Reload from file** goes back to the file on disk, and **Save as…** saves the edited original.
     - **Right:** hand edits are kept and exported as-is, after a syntax check. If you later change the mapping, keys, hostname or original, a banner says your edits are outdated and offers **Regenerate**.
   - **Find** (Ctrl+F) highlights every match in the text as you type and outlines the current one. F3 / Shift+F3 step through the matches. **Line** (Ctrl+G) jumps to a line.
6. **Export.** This writes `<host>-<model>-converted.conf` plus an HTML report. The report covers the mapping, every renamed reference, unchanged look-alike names, everything removed with unmapped ports, the tunnels that got new keys (names only), and warnings. It also notes whether either config was edited by hand.

> **The exported .conf contains IPsec pre-shared keys in clear text.** Keep it private (no email, no shared folders) and delete it once the new firewall is set up.

### What the conversion does
- Renames interface references only where they really are interface names:
  - `config system interface`
  - virtual-switch ports
  - zones
  - SD-WAN
  - policies, routes, VIPs, DHCP, IPsec, HA `hbdev`/`monitor`, DNS server, and similar
- Never touches FortiSwitch, FortiAP, or FortiExtender sections. Their `port1`… belong to those devices.
- Leaves object names (an address called `dmz`) and address-group members alone. They're listed in the report so you can check them.
- Does all renames in one pass, so swaps (`port1`↔`port2`) work.
- Leaves unmapped ports out, along with everything that depends on them (see step 3). Adds target ports with no source as default `type physical` entries.
- Writes the new local ID and clear-text `psksecret` into each IPsec phase 1 (route- and policy-based).
- Rewrites `#config-version`/`#platform` for the new model and keeps the **source firmware build**. Drops `#serialno`/`#hwrev`/`#mgmt.data`.
- Warns about:
  - unsupported `set speed` values
  - hardware-switch membership on ports that can't join the switch
  - mixed-speed aggregates
  - `private-data-encryption`
  - HA renames
  - logical interfaces whose names clash with new physical port names

### Restoring on the new box
1. Load the new firewall with the **same FortiOS build** the source config came from (shown in the app and report).
2. Restore the converted file. Then run `diagnose debug config-error-log read` and fix anything it reports.
3. Verify the box, then upgrade firmware along Fortinet's upgrade path.

> **Human review required.** The output is generated automatically. A network engineer must review the report and config, and test on a lab unit, before production use.

## Model profiles

Port definitions are JSON files in [profiles/](profiles/). They are built into the exe, so the exe runs on its own. To change them without rebuilding, copy the `profiles` folder next to the exe as `Profiles` and edit it there. If that folder exists, the app uses it instead of the built-in copy. Each port has:
- `name`: the FortiOS interface name
- `label`: the front-panel text
- `media`: `RJ45`, `SFP`, `SFPPlus`, `SharedRJ45SFP`, or `SharedRJ45SFPPlus`
- `maxSpeedMbps`
- `switchCapable`: `true`, `false`, or `null` for unknown
- `row` and `column`: the panel layout (fractional columns make gaps)

The 61F and 91G profiles were checked against real configs. The 101F and 201G profiles come from Fortinet docs (`"verified": false`), and the app flags this. The 201G `ha`/`mgmt` names in particular should be confirmed with `show system interface` on a real unit, then set `"verified": true`.

## Building

Requires the .NET 8 SDK on Windows.

```powershell
dotnet build
dotnet test
# One self-contained exe in .\publish (no .NET install or other files needed)
dotnet publish src/FortiGateUpgrader -p:PublishProfile=win-x64
```

To run the optional checks against real exports (never commit those files):

```powershell
$env:FGU_SAMPLE_61F = "C:\path\to\61F.conf"; $env:FGU_SAMPLE_91G = "C:\path\to\91G.conf"
dotnet test --filter RealSample
```

## Layout
- `src/FortiGateUpgrader.Core/`: parser and writer, interface-reference rules, usage analysis, conversion, mapping rules, and the report
- `src/FortiGateUpgrader/`: WPF UI (`Views/PortMapView.cs` is the drag-and-drop panel view; `Views/ConfigEditor` wraps the [AvalonEdit](https://github.com/icsharpcode/AvalonEdit) text editor, MIT-licensed, the app's only third-party package)
- `tests/FortiGateUpgrader.Tests/`: xUnit tests on synthetic configs, covering every upgrade path
- `profiles/`: model profiles, upgrade paths, and reference photos

The app works fully offline and never sends config contents anywhere.
