using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FortiGateUpgrader.Core.Config;
using FortiGateUpgrader.Core.Diff;
using FortiGateUpgrader.Core.Mapping;
using FortiGateUpgrader.Core.Models;
using FortiGateUpgrader.Views;
using Microsoft.Win32;

namespace FortiGateUpgrader;

public sealed record StatusLine(string Text, Brush Brush);

public partial class MainWindow : Window
{
    private static readonly Brush ErrorBrush = Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush WarnBrush = Frozen(0xB4, 0x53, 0x09);
    private static readonly Brush OkBrush = Frozen(0x15, 0x80, 0x3D);
    private static readonly Brush InfoBrush = Frozen(0x37, 0x41, 0x51);

    // Diff colors: right side (converted) green, left side (original) red.
    private static readonly Brush AddedLine = Frozen(0xEC, 0xFD, 0xF5);
    private static readonly Brush AddedSpan = Frozen(0xBB, 0xF7, 0xD0);
    private static readonly Brush RemovedLine = Frozen(0xFE, 0xF2, 0xF2);
    private static readonly Brush RemovedSpan = Frozen(0xFE, 0xCA, 0xCA);
    private static readonly Brush RemovedMarker = Frozen(0xDC, 0x26, 0x26);
    private static readonly Brush AddedMarker = Frozen(0x16, 0xA3, 0x4A);

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    private ProfileCatalog? _catalog;

    // Source: the file on disk, and the version currently applied (possibly edited on the left of Compare & edit).
    private string? _sourcePath;
    private string? _fileText;
    private string? _originalText;
    private FortiConfig? _source;
    private ModelProfile? _sourceModel;
    private Dictionary<string, PortUsage>? _usage;
    private MappingState? _state;
    private GroupState? _groups;

    // IPsec: user input per tunnel (kept while the same file is open), and the tunnels the current mapping keeps.
    private readonly Dictionary<string, TunnelInput> _tunnelInputs = new();
    private List<TunnelInput> _currentTunnels = new();

    // Conversion output. _generatedText is what the converter produced; the right-hand editor may hold hand edits.
    private ConversionRequest? _request;
    private ConversionResult? _result;
    private string? _generatedText;
    private bool _conversionStale = true;

    /// <summary>
    /// The latest conversion output. Equals _generatedText unless the inputs changed after the user
    /// hand-edited the converted config (then those edits are based on outdated output).
    /// </summary>
    private string? _pendingGenerated;
    private string? _conversionError;

    // Diff state for highlights, navigation and scroll sync.
    private List<ChangeBlock> _blocks = new();
    private int[] _newToOld = Array.Empty<int>();
    private int[] _oldToNew = Array.Empty<int>();
    private readonly DispatcherTimer _diffTimer;
    private ConfigEditor? _syncIgnoreEditor;
    private int _syncIgnoreLine;

    /// <summary>Set after edits; cleared when the work is exported or saved.</summary>
    private bool _unsavedEdits;

    public MainWindow()
    {
        InitializeComponent();
        _diffTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _diffTimer.Tick += (_, _) => { _diffTimer.Stop(); if (Tabs.SelectedItem == CompareTab) RefreshDiff(); };

        MapView.PortSelected += (_, p) => ShowPortDetails(p);
        OriginalEditor.UserEdited += (_, _) => { _unsavedEdits = true; UpdateEditState(); RestartDiffTimer(); };
        ConvertedEditor.UserEdited += (_, _) => { _unsavedEdits = true; UpdateEditState(); RestartDiffTimer(); };
        OriginalEditor.Scrolled += (_, _) => SyncScroll(OriginalEditor, ConvertedEditor, _oldToNew);
        ConvertedEditor.Scrolled += (_, _) => SyncScroll(ConvertedEditor, OriginalEditor, _newToOld);
        Loaded += (_, _) =>
        {
            LoadCatalog();
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) OpenFile(args[1]);
        };
        OriginalEditor.IsReadOnly = true;
        ConvertedEditor.IsReadOnly = true;
        Stepper.SetSteps(StepTitles);
        Stepper.StepClicked += (_, i) => GoToStep(i);
        MapView.Rebuild();
        RefreshTunnels();
        UpdateEditState();
        CheckList.ItemsSource = new[] { new StatusLine("No file opened yet.", InfoBrush) };
        GoToStep(0);
    }

    // ------------------------------------------------------------------ wizard navigation

    private static readonly string[] StepTitles =
        { "Open config", "Model & hostname", "Port mapping", "Aggregates & switches", "IPsec keys", "Compare & edit", "Summary" };

    private TabItem[] Steps => new[] { OpenStep, ModelStep, MapTab, GroupsTab, IpsecTab, CompareTab, SummaryStep };

    private int CurrentStep => Math.Max(0, Tabs.SelectedIndex);

    /// <summary>Why the user can't move past <paramref name="step"/> yet, or null when it's done.</summary>
    private string? Blocker(int step) => step switch
    {
        0 => _source == null ? "Open a configuration file to continue." : null,
        1 => _state == null ? "Choose the new model to continue."
            : HostnameBox.Text.Trim() is { Length: > 0 } h && !IsValidHostname(h) ? "Fix the hostname: at most 35 characters, no spaces or quotes." : null,
        2 => _state == null || _state.Links.Count == 0 ? "Drag at least one port from the current firewall to the new one (or click Auto-map)."
            : !_state.IsComplete ? "Fix the red lines to continue." : null,
        3 => !GroupsComplete ? "Fix the red lines to continue." : null,
        4 => !IpsecComplete ? $"Enter a local ID and pre-shared key for every tunnel to continue ({_currentTunnels.Count(t => t.Problem == null)} of {_currentTunnels.Count} done)." : null,
        // Not generated yet (stale) is fine: the summary generates it. Only a failed conversion blocks.
        5 => !ReadyToConvert && !ConvertedEdited ? "Complete the earlier steps first."
            : !_conversionStale && _result == null && !ConvertedEdited ? _conversionError ?? "The converted config couldn't be generated." : null,
        _ => null,
    };

    private bool Reachable(int step) => Enumerable.Range(0, step).All(i => Blocker(i) == null);

    private void GoToStep(int step)
    {
        step = Math.Clamp(step, 0, Steps.Length - 1);
        if (step > CurrentStep && !Reachable(step)) return;
        Tabs.SelectedItem = Steps[step];
        UpdateSidePanel();
        UpdateNav();
    }

    /// <summary>The side panel describes the two wiring steps; the other steps get the full width.</summary>
    private void UpdateSidePanel()
    {
        if (SidePanel == null) return;
        var map = Tabs.SelectedItem == MapTab;
        SidePanel.Visibility = map || Tabs.SelectedItem == GroupsTab ? Visibility.Visible : Visibility.Collapsed;
        PortDetailsSection.Visibility = HowToMap.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
        HowToGroups.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoToStep(CurrentStep - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => GoToStep(CurrentStep + 1);

    private static readonly Brush NextBrush = Frozen(0x25, 0x63, 0xEB);

    /// <summary>Updates the stepper and the Back/Next buttons for the current state.</summary>
    private void UpdateNav()
    {
        if (Stepper == null || NextButton == null) return;
        var current = CurrentStep;
        var blockers = Enumerable.Range(0, Steps.Length).Select(Blocker).ToList();
        var reachable = Enumerable.Range(0, Steps.Length).Select(i => blockers.Take(i).All(b => b == null)).ToList();
        var done = Enumerable.Range(0, Steps.Length).Select(i => i < current && blockers[i] == null).ToList();
        Stepper.Update(current, done, reachable);

        BackButton.IsEnabled = current > 0;
        var last = current == Steps.Length - 1;
        NextButton.Visibility = last ? Visibility.Collapsed : Visibility.Visible;
        var ok = !last && blockers[current] == null;
        NextButton.IsEnabled = ok;
        // Highlight Next once the step is complete.
        NextButton.Background = ok ? NextBrush : null;
        NextButton.Foreground = ok ? Brushes.White : null;
        NextButton.Content = !last ? $"Next: {StepTitles[current + 1]}  ▶" : "Next  ▶";
        if (NextButton.Background == null) NextButton.ClearValue(BackgroundProperty);
        if (NextButton.Foreground == null) NextButton.ClearValue(ForegroundProperty);
        ExportStatus.Text = last ? "" : blockers[current] ?? "";
    }

    private bool OriginalDirty => _originalText != null && OriginalEditor.Text != _originalText;
    private bool OriginalEdited => _originalText != null && _fileText != null && _originalText != _fileText;
    private bool ConvertedEdited => _generatedText != null && ConvertedEditor.Text != _generatedText;
    private bool IpsecComplete => _currentTunnels.All(t => t.Problem == null);
    private bool GroupsComplete => _groups == null || _groups.IsComplete;
    private bool ReadyToConvert => _state != null && _source != null && _sourceModel != null && _state.IsComplete && GroupsComplete && IpsecComplete;

    private void LoadCatalog()
    {
        // A "Profiles" folder next to the exe overrides the built-in profiles, so they can be
        // edited without rebuilding. Otherwise the copy embedded in the exe is used.
        var dir = Path.Combine(AppContext.BaseDirectory, "Profiles");
        var source = Directory.Exists(dir) ? dir : "the built-in profiles";
        try
        {
            _catalog = Directory.Exists(dir) ? ProfileCatalog.Load(dir) : LoadEmbeddedCatalog();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load the model profiles from\n{source}\n\n{ex.Message}", "Profiles missing", MessageBoxButton.OK, MessageBoxImage.Error);
            OpenButton.IsEnabled = false;
        }
    }

    private static ProfileCatalog LoadEmbeddedCatalog()
    {
        const string prefix = "Profiles/";
        var asm = typeof(MainWindow).Assembly;
        var files = new Dictionary<string, byte[]>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            files[name[prefix.Length..]] = ms.ToArray();
        }
        return ProfileCatalog.Load(files, "built-in");
    }

    // ------------------------------------------------------------------ step 1: open

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog == null || !ConfirmDiscardEdits()) return;
        var dlg = new OpenFileDialog
        {
            Title = "Open FortiGate configuration backup",
            Filter = "FortiGate config (*.conf;*.txt)|*.conf;*.txt|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        OpenFile(dlg.FileName);
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files && ConfirmDiscardEdits()) OpenFile(files[0]);
    }

    /// <summary>Loads a source configuration (from the Open dialog, a dropped file or the command line).</summary>
    public void OpenFile(string fileName)
    {
        if (_catalog == null) return;
        var checks = new List<StatusLine>();
        void Show()
        {
            if (_source != null && _sourcePath != fileName)
                checks.Add(new StatusLine($"The previously opened file is still loaded: {_sourcePath}", InfoBrush));
            CheckList.ItemsSource = checks;
            GoToStep(0);
        }

        string text;
        try
        {
            text = File.ReadAllText(fileName);
        }
        catch (Exception ex)
        {
            checks.Add(new StatusLine($"✘ Couldn't read {fileName}: {ex.Message}", ErrorBrush));
            Show();
            return;
        }
        checks.Add(new StatusLine($"✔ Read {Path.GetFileName(fileName)} ({LineDiff.SplitLines(text).Length:N0} lines)", OkBrush));

        var error = LoadSource(text, fileName, keepMapping: false);
        if (error != null)
        {
            checks.Add(new StatusLine("✘ " + error, ErrorBrush));
            Show();
            return;
        }
        _fileText = text;
        _unsavedEdits = false;
        checks.AddRange(SourceChecks());
        UpdateEditState();
        Show();
    }

    /// <summary>What the step-1 check found in the loaded config.</summary>
    private List<StatusLine> SourceChecks()
    {
        var cfg = _source!;
        var model = _sourceModel!;
        var lines = new List<StatusLine>
        {
            new($"✔ Read as a FortiGate configuration ({cfg.Root.Descendants().Count(n => n.Kind == NodeKind.Config):N0} config sections)", OkBrush),
            new($"✔ Model: {model.DisplayName} (#platform={cfg.Platform})", OkBrush),
        };
        lines.Add(cfg.GetHeader("config-version") != null
            ? new StatusLine($"✔ Firmware: FortiOS {cfg.FirmwareDescription}", OkBrush)
            : new StatusLine("⚠ No #config-version header. A file without it may not restore as a backup.", WarnBrush));
        lines.Add(new StatusLine($"✔ Hostname: {cfg.Hostname ?? "(none)"}", cfg.Hostname != null ? OkBrush : WarnBrush));
        lines.Add(new StatusLine($"✔ Can be converted to: {string.Join(", ", _catalog!.TargetsFor(model).Select(t => t.DisplayName))}", OkBrush));

        var inUse = model.Ports.Count(p => _usage![p.Name].InUse);
        lines.Add(new StatusLine($"• {inUse} of {model.Ports.Count} ports carry configuration", InfoBrush));
        var groups = InterfaceGroups.Find(cfg, model);
        if (groups.Count > 0)
            lines.Add(new StatusLine($"• Switch/aggregate interfaces: {string.Join(", ", groups.Select(g => $"{g.Name} ({g.KindLabel}: {string.Join(", ", g.PhysicalMembers)})"))}", InfoBrush));
        var tunnels = IpsecTunnels.Find(cfg);
        if (tunnels.Count > 0)
            lines.Add(new StatusLine($"• {tunnels.Count} IPsec tunnel(s) – {tunnels.Count(t => t.NeedsPsk)} with pre-shared keys that will need new keys", InfoBrush));
        var policies = cfg.FindConfigs("firewall policy").Sum(c => c.Children.Count(e => e.Kind == NodeKind.Edit));
        lines.Add(new StatusLine($"• {policies:N0} firewall policies", InfoBrush));

        if (cfg.FindConfigs("system global").FirstOrDefault()?.FindSet("private-data-encryption")?.Values.FirstOrDefault()?.Text == "enable")
            lines.Add(new StatusLine("⚠ private-data-encryption is enabled: encrypted secrets only load if the new firewall has the same key.", WarnBrush));
        if (cfg.Root.Children.Any(c => c.Kind == NodeKind.Config && c.Name == "vdom"))
            lines.Add(new StatusLine("⚠ This config uses multiple VDOMs. Review the converted config carefully.", WarnBrush));
        if (!model.Verified)
            lines.Add(new StatusLine($"⚠ The {model.Model} port names come from documentation and haven't been checked against a real config.", WarnBrush));
        return lines;
    }

    /// <summary>
    /// Parses and applies a source config. With <paramref name="keepMapping"/> (an edit of the same model)
    /// the existing port links and IPsec keys are carried over.
    /// </summary>
    /// <returns>null on success, otherwise why the config can't be used.</returns>
    private string? LoadSource(string text, string? path, bool keepMapping)
    {
        FortiConfig cfg;
        try
        {
            cfg = FortiConfigParser.Parse(text);
        }
        catch (Exception ex)
        {
            return $"This isn't a readable FortiGate configuration: {ex.Message}";
        }

        var model = _catalog!.FindByPlatform(cfg.Platform);
        if (model == null)
        {
            var supported = string.Join(", ", _catalog.Paths.Select(p => p.Source));
            return $"The configuration is for platform \"{cfg.Platform ?? "unknown"}\" (from the #platform= line), which this tool doesn't convert. Supported source models: {supported}.";
        }
        var targets = _catalog.TargetsFor(model);
        if (targets.Count == 0) return $"There are no upgrade paths defined from the {model.Model}.";

        var sameModel = keepMapping && _sourceModel == model && _state != null;
        _source = cfg;
        _sourcePath = path ?? _sourcePath;
        _sourceModel = model;
        _originalText = text;
        _usage = InterfaceUsageAnalyzer.Analyze(cfg, model);
        OriginalEditor.SetText(text);
        OriginalEditor.IsReadOnly = false;

        var inUse = model.Ports.Count(p => _usage[p.Name].InUse);
        SourceSummary.Text = $"{model.DisplayName}  ·  {cfg.Hostname ?? "(no hostname)"}  ·  FortiOS {cfg.FirmwareDescription ?? "?"}  ·  {inUse} of {model.Ports.Count} ports in use";
        SourceFile.Text = _sourcePath;
        SetPhoto(SourcePhoto, SourcePhotoCaption, model, "Current");

        if (sameModel)
        {
            // Same model: rebuild the state with the new usage but keep the user's links.
            var old = _state!;
            old.Changed -= OnMappingChanged;
            _state = new MappingState(model, old.Target, _usage);
            _state.CopyFrom(old);
            _state.Changed += OnMappingChanged;
            MapView.State = _state;
            var oldGroups = _groups;
            SetGroups(new GroupState(_state, InterfaceGroups.Find(cfg, model)));
            if (oldGroups != null) _groups!.CopyFrom(oldGroups);
            RefreshTunnels();
            MarkConversionStale();
            RefreshStatus();
        }
        else
        {
            // Different file/model: forget all previous output (including hand edits) and keys.
            _generatedText = null;
            _pendingGenerated = null;
            _result = null;
            _request = null;
            _tunnelInputs.Clear();
            ConvertedEditor.SetText("");
            TargetCombo.ItemsSource = targets;
            TargetCombo.IsEnabled = true;
            TargetCombo.SelectedIndex = -1;
            TargetCombo.SelectedIndex = 0;
        }
        ModelStepSource.Text = $"Converting {model.DisplayName} \"{cfg.Hostname ?? "(no hostname)"}\" (FortiOS {cfg.FirmwareDescription ?? "?"}).";
        UpdateEditState();
        return null;
    }

    // ------------------------------------------------------------------ step 2: target

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetCombo.SelectedItem is not ModelProfile target || _sourceModel == null || _usage == null) return;

        if (_state != null) _state.Changed -= OnMappingChanged;
        _state = new MappingState(_sourceModel, target, _usage);
        _state.Changed += OnMappingChanged;
        MapView.State = _state; // starts unwired: the user drags each connection (or clicks Auto-map)
        SetGroups(new GroupState(_state, InterfaceGroups.Find(_source!, _sourceModel)));

        HostnameBox.Text = SuggestHostname(_source?.Hostname, _sourceModel.Model, target.Model);
        SetPhoto(TargetPhoto, TargetPhotoCaption, target, "New");
        TargetNote.Text = target.Verified ? "" : $"The {target.Model} port names come from Fortinet documentation and haven't been checked against a real config yet.";
        AutoMapButton.IsEnabled = ClearButton.IsEnabled = GroupsFromMapButton.IsEnabled = GroupsClearButton.IsEnabled = true;
        ShowPortDetails(null);
        RefreshTunnels();
        MarkConversionStale();
        RefreshStatus();
    }

    private static string SuggestHostname(string? hostname, string from, string to) =>
        string.IsNullOrEmpty(hostname) ? "" :
        hostname.Contains(from, StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(hostname, Regex.Escape(from), to, RegexOptions.IgnoreCase)
            : hostname;

    private void SetPhoto(Image image, TextBlock caption, ModelProfile model, string role)
    {
        caption.Text = $"{role}: {model.DisplayName}";
        image.Source = null;
        var data = _catalog?.ImageBytes(model);
        if (data == null) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.DecodePixelWidth = 600;
            bmp.EndInit();
            image.Source = bmp;
        }
        catch
        {
            // Photos are optional.
        }
    }

    private void HostnameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // New local IDs follow the hostname until the user types their own.
        foreach (var row in _tunnelInputs.Values) row.ApplyDefaultLocalId(EffectiveHostname);
        var h = HostnameBox.Text.Trim();
        if (HostnameNote != null)
        {
            HostnameNote.Text = h.Length > 0 && !IsValidHostname(h)
                ? "✘ At most 35 characters, with no spaces or quotes."
                : h.Length == 0 ? $"Blank keeps the current hostname ({_source?.Hostname ?? "none"}). It is also the default IPsec local ID."
                : "Up to 35 characters, no spaces or quotes. It is also the default IPsec local ID.";
            HostnameNote.Foreground = h.Length > 0 && !IsValidHostname(h) ? ErrorBrush : InfoBrush;
        }
        MarkConversionStale();
        UpdateNav();
    }

    /// <summary>The hostname the new firewall will have: the one typed in, or the original if that's blank/invalid.</summary>
    private string? EffectiveHostname
    {
        get
        {
            var typed = HostnameBox?.Text.Trim();
            return !string.IsNullOrEmpty(typed) && IsValidHostname(typed) ? typed : _source?.Hostname;
        }
    }

    // ------------------------------------------------------------------ step 3: mapping

    private void AutoMapButton_Click(object sender, RoutedEventArgs e) => _state?.AutoMap();

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _state?.Clear();

    private void SetGroups(GroupState groups)
    {
        if (_groups != null) _groups.Changed -= OnGroupsChanged;
        _groups = groups;
        _groups.Changed += OnGroupsChanged;
        GroupView.State = _groups;
        UpdateGroupsHeader();
    }

    private void GroupsFromMap_Click(object sender, RoutedEventArgs e) => _groups?.FromPortMapping();

    private void GroupsClear_Click(object sender, RoutedEventArgs e) => _groups?.Clear();

    private void OnGroupsChanged(object? sender, EventArgs e)
    {
        RefreshTunnels();
        MarkConversionStale();
        RefreshStatus();
    }

    private void UpdateGroupsHeader()
    {
        if (_groups == null) { GroupsTab.Header = "Aggregates & switches"; return; }
        var problems = _groups.Problems.Count;
        var empty = _groups.EmptyGroups.Count;
        GroupsTab.Header = new TextBlock
        {
            Text = "Aggregates & switches" + (problems > 0 ? $" ({problems} issue(s))" : empty > 0 ? $" ({empty} without ports)" : _groups.Groups.Count > 0 ? " ✔" : ""),
            Foreground = problems > 0 ? ErrorBrush : empty > 0 ? WarnBrush : _groups.Groups.Count > 0 ? OkBrush : Brushes.Black,
        };
    }

    private void OnMappingChanged(object? sender, EventArgs e)
    {
        RefreshTunnels();
        MarkConversionStale();
        RefreshStatus();
        ShowPortDetails(MapView.SelectedPort);
    }

    private void RefreshStatus()
    {
        if (_state == null) return;
        var issues = _state.Problems.Select(p => new StatusLine("● " + p, ErrorBrush)).ToList();
        if (_groups != null)
            issues.AddRange(_groups.Problems.Select(p => new StatusLine("● " + p + " (step 4, Aggregates & switches)", ErrorBrush)));
        var missingKeys = _currentTunnels.Count(t => t.Problem != null);
        if (missingKeys > 0)
            issues.Add(new StatusLine($"● {missingKeys} IPsec tunnel(s) need a new local ID and pre-shared key (step 5, IPsec keys).", ErrorBrush));

        var lines = new List<StatusLine>();
        var mapped = _state.Links.Select(l => l.Source).Distinct().Count();
        if (mapped == 0)
        {
            // Nothing wired yet: one instruction instead of a warning for every port.
            ProblemList.ItemsSource = new List<StatusLine>
            {
                new("Nothing is mapped yet. Drag each port of the current firewall to a port on the new one (or click Auto-map), then assign ports in step 4 (Aggregates & switches).", InfoBrush),
            };
            UpdateGroupsHeader();
            UpdateNav();
            return;
        }
        if (issues.Count == 0)
            lines.Add(new StatusLine(_state.UnmappedInUse.Count == 0
                ? $"✔ Mapping complete – {mapped} ports mapped. Ready to export."
                : $"✔ {mapped} ports mapped. Ready to export.", OkBrush));
        else
        {
            lines.Add(new StatusLine($"{mapped} ports mapped, {issues.Count} issue(s) to resolve:", InfoBrush));
            lines.AddRange(issues);
        }
        foreach (var w in _state.Warnings)
            lines.Add(new StatusLine("⚠ " + w, WarnBrush));
        if (_groups != null)
            foreach (var w in _groups.Warnings)
                lines.Add(new StatusLine("⚠ " + w, WarnBrush));

        if (OriginalDirty)
            lines.Add(new StatusLine("⚠ The original config has edits that are not applied yet (step 6, Compare & edit).", WarnBrush));
        if (!_state.Target.Verified)
            lines.Add(new StatusLine($"⚠ The {_state.Target.Model} port names come from Fortinet documentation and have not been checked against a real config.", WarnBrush));

        ProblemList.ItemsSource = lines;
        UpdateGroupsHeader();
        UpdateNav();
    }

    private void ShowPortDetails(PortRef? port)
    {
        if (port == null || _state == null)
        {
            PortDetails.Text = "Click a port to see how it is used. Hover for a quick summary.";
            return;
        }
        var model = port.Side == PanelSide.Source ? _state.Source : _state.Target;
        var def = model.FindPort(port.Name);
        if (def == null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"{model.DisplayName} – {def.Name}");
        sb.AppendLine($"Front label: {def.Label}");
        sb.AppendLine($"Media: {def.MediaDescription}");
        if (def.SwitchCapable == true) sb.AppendLine("Can be a hardware-switch member");
        sb.AppendLine();
        if (port.Side == PanelSide.Source)
        {
            var links = _state.Links.Where(l => l.Source == def.Name).Select(l => l.Target).ToList();
            var u = _state.Usage[def.Name];
            sb.AppendLine(links.Count > 0 ? $"Goes to: {string.Join(", ", links)}"
                : u.OnlyGroupMember ? $"Only a member of {string.Join(", ", u.Groups)} – choose that group's new ports in step 4 (Aggregates & switches)."
                : "Not mapped – it won't be converted.");
            sb.AppendLine();
            sb.AppendLine(u.Summary);
            var refs = u.References.Where(r => !r.IsDefinition).ToList();
            if (refs.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Referenced in {refs.Count} place(s){(links.Count == 0 ? " – removed with it" : "")}:");
                foreach (var r in refs.Take(25)) sb.AppendLine("  • " + r.Location);
                if (refs.Count > 25) sb.AppendLine($"  … and {refs.Count - 25} more");
            }
        }
        else
        {
            var from = _state.Links.Where(l => l.Target == def.Name).Select(l => l.Source).ToList();
            sb.AppendLine(from.Count switch
            {
                0 => "Free – will be created with default settings.",
                1 => $"Receives the settings of {from[0]}.",
                _ => $"CONFLICT – {string.Join(" and ", from)} are both connected here.",
            });
        }
        PortDetails.Text = sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ step 3b: IPsec keys

    /// <summary>Lists the tunnels that will exist after conversion (unmapped ports remove theirs).</summary>
    private void RefreshTunnels()
    {
        var tunnels = new List<IpsecTunnel>();
        var removedByMapping = 0;
        if (_source != null && _state != null)
        {
            var all = IpsecTunnels.Find(_source).Where(t => t.NeedsPsk).ToList();
            tunnels = all;
            if (!_state.Problems.Any() && GroupsComplete)
            {
                try
                {
                    tunnels = ConversionService.Convert(BuildRequest(requireKeys: false)).Tunnels;
                    removedByMapping = all.Count - tunnels.Count;
                }
                catch
                {
                    // Fall back to the tunnels in the source.
                }
            }
        }

        var rows = new List<TunnelInput>();
        foreach (var t in tunnels)
        {
            if (!_tunnelInputs.TryGetValue(t.Key, out var row))
            {
                row = new TunnelInput(t);
                row.ApplyDefaultLocalId(EffectiveHostname); // before hooking Edited: no refresh storm while listing
                row.Edited += (_, _) => { MarkConversionStale(); RefreshStatus(); UpdateIpsecHeader(); };
                _tunnelInputs[t.Key] = row;
            }
            else row.Update(t);
            rows.Add(row);
        }
        _currentTunnels = rows;
        TunnelList.ItemsSource = rows;

        IpsecWarning.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var removedNote = removedByMapping > 0 ? $" {removedByMapping} more tunnel(s) use ports that aren't mapped, so they won't be converted." : "";
        IpsecIntro.Text = _source == null
            ? "Open a configuration to see its IPsec tunnels."
            : rows.Count == 0
                ? "This configuration has no IPsec tunnels with pre-shared keys to convert. Nothing to do here." + removedNote
                : $"This configuration has {rows.Count} IPsec tunnel(s) with pre-shared keys. The existing keys are encrypted for the old firewall and won't work on the new one, " +
                  "so enter a new local ID and pre-shared key for each tunnel. The converted config can't be generated until every tunnel is filled in. " +
                  "The remote end of each tunnel must be set to the same key." + removedNote;
        UpdateIpsecHeader();
    }

    private void UpdateIpsecHeader()
    {
        var total = _currentTunnels.Count;
        var done = _currentTunnels.Count(t => t.Problem == null);
        // Color only the header text (TabItem.Foreground would also color the tab's content).
        IpsecTab.Header = new TextBlock
        {
            Text = total == 0 ? "IPsec keys" : done == total ? $"IPsec keys ✔ {done}/{total}" : $"IPsec keys ({done}/{total})",
            Foreground = total > 0 && done < total ? ErrorBrush : total > 0 ? OkBrush : Brushes.Black,
        };
    }

    private ConversionRequest BuildRequest(bool requireKeys)
    {
        var hostname = HostnameBox.Text.Trim();
        return new ConversionRequest
        {
            Source = _source!,
            SourceModel = _sourceModel!,
            TargetModel = _state!.Target,
            Mapping = _state.ToMapping(),
            Ipsec = _tunnelInputs.ToDictionary(kv => kv.Key, kv => kv.Value.Credentials),
            GroupMembers = _groups?.ToMembers(),
            RequireIpsecCredentials = requireKeys,
            NewHostname = string.IsNullOrEmpty(hostname) || hostname == _source!.Hostname || !IsValidHostname(hostname) ? null : hostname,
        };
    }

    // ------------------------------------------------------------------ original config (left side)

    private void ApplyOriginal_Click(object sender, RoutedEventArgs e) => ApplyOriginal();

    private bool ApplyOriginal()
    {
        if (!OriginalDirty) return true;
        var error = LoadSource(OriginalEditor.Text, null, keepMapping: true);
        if (error != null) MessageBox.Show(this, error, "Apply changes", MessageBoxButton.OK, MessageBoxImage.Error);
        return error == null;
    }

    private void RevertOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_originalText == null) return;
        OriginalEditor.SetText(_originalText);
        UpdateEditState();
        RestartDiffTimer();
    }

    private void ReloadOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_fileText == null) return;
        if (MessageBox.Show(this, "Discard your edits to the original config and use the file as it was loaded?", "Reload from file",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var error = LoadSource(_fileText, null, keepMapping: true);
        if (error != null) MessageBox.Show(this, error, "Reload from file", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SaveOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_originalText == null) return;
        var dlg = new SaveFileDialog
        {
            Title = "Save original config (as edited)",
            FileName = Path.GetFileNameWithoutExtension(_sourcePath) + "-edited" + (Path.GetExtension(_sourcePath) is { Length: > 0 } ext ? ext : ".conf"),
            Filter = "FortiGate config (*.conf;*.txt)|*.conf;*.txt|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_sourcePath),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, OriginalEditor.Text, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the file:\n\n" + ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ------------------------------------------------------------------ converted config (right side)

    private void MarkConversionStale()
    {
        _conversionStale = true;
        // Keep the comparison current while it is on screen.
        if (Tabs.SelectedItem == CompareTab) Dispatcher.BeginInvoke(RefreshOutputViews);
        else UpdateEditState();
    }

    /// <summary>Runs the conversion if anything changed. Hand edits on the right are never overwritten here.</summary>
    private void EnsureConverted()
    {
        if (!_conversionStale) return;
        _conversionStale = false;
        _conversionError = null;

        if (!ReadyToConvert)
        {
            _request = null;
            _result = null;
            _pendingGenerated = null; // nothing current to compare hand edits against → they are outdated
            if (!ConvertedEdited)
            {
                _generatedText = null;
                ConvertedEditor.SetText("");
            }
            return;
        }

        _request = BuildRequest(requireKeys: true);
        try
        {
            _result = ConversionService.Convert(_request);
        }
        catch (Exception ex)
        {
            _result = null;
            _pendingGenerated = null;
            _conversionError = "Conversion failed: " + (ex is ConversionException ce ? string.Join(" ", ce.Problems) : ex.Message);
            if (!ConvertedEdited)
            {
                _generatedText = null;
                ConvertedEditor.SetText("");
            }
            return;
        }

        var text = FortiConfigWriter.Write(_result.Output);
        if (ConvertedEdited)
        {
            // Keep the hand edits; remember the new output so "Regenerate" can use it.
            _pendingGenerated = text;
            return;
        }
        _generatedText = text;
        _pendingGenerated = text;
        ConvertedEditor.SetText(text);
    }

    private void Regenerate_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Discard your hand edits to the converted config and regenerate it from the current mapping and keys?", "Regenerate",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _conversionStale = true;
        _generatedText = null; // forget edits
        _pendingGenerated = null;
        ConvertedEditor.SetText("");
        RefreshOutputViews();
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || SidePanel == null) return;
        UpdateSidePanel();
        if (Tabs.SelectedItem == CompareTab) RefreshOutputViews();
        if (Tabs.SelectedItem == SummaryStep) RefreshSummary();
        UpdateNav();
    }

    private void RefreshOutputViews()
    {
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try
        {
            EnsureConverted();
            if (Tabs.SelectedItem == CompareTab) RefreshDiff();
            UpdateEditState();
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    // ------------------------------------------------------------------ differences

    private void RestartDiffTimer()
    {
        if (Tabs.SelectedItem != CompareTab) return;
        _diffTimer.Stop();
        _diffTimer.Start();
    }

    /// <summary>Compares the two editors and highlights the differences (mainly on the converted side).</summary>
    private void RefreshDiff()
    {
        var left = OriginalEditor.Text;
        var right = ConvertedEditor.Text;
        _blocks = new List<ChangeBlock>();
        if (left.Length == 0 || right.Length == 0)
        {
            OriginalEditor.SetHighlights(null);
            ConvertedEditor.SetHighlights(null);
            _newToOld = _oldToNew = Array.Empty<int>();
            DiffSummary.Text = _source == null ? "Open a configuration to compare." : "No converted config yet – see the message on the right.";
            return;
        }

        var diff = LineDiff.Compute(left, right, ignoreIndentation: true);
        _blocks = LineDiff.Blocks(diff);
        var hl = new EditorHighlights { SpanBrush = RemovedSpan, MarkerBrush = AddedMarker };
        var hr = new EditorHighlights { SpanBrush = AddedSpan, MarkerBrush = RemovedMarker };
        foreach (var b in _blocks)
        {
            foreach (var a in b.Added) hr.Lines[a.NewLine!.Value] = AddedLine;
            foreach (var r in b.Removed) hl.Lines[r.OldLine!.Value] = RemovedLine;
            for (var i = 0; i < Math.Min(b.Removed.Count, b.Added.Count); i++)
            {
                var (o, n) = LineDiff.InlineChange(b.Removed[i], b.Added[i]);
                if (o != null) hl.Spans.Add((o.Line, o.Column, o.Length));
                if (n != null) hr.Spans.Add((n.Line, n.Column, n.Length));
            }
            // Unpaired added lines are entirely new: highlight their text too.
            foreach (var a in b.Added.Skip(b.Removed.Count))
            {
                var indent = a.Text.Length - a.Text.TrimStart().Length;
                if (a.Text.Trim().Length > 0) hr.Spans.Add((a.NewLine!.Value, indent, a.Text.Trim().Length));
            }
            if (b.Removed.Count > b.Added.Count) hr.DeletionMarkers.Add(b.NewStart + b.Added.Count);
            if (b.Added.Count > b.Removed.Count) hl.DeletionMarkers.Add(b.OldStart + b.Removed.Count);
        }
        OriginalEditor.SetHighlights(hl);
        ConvertedEditor.SetHighlights(hr);
        _newToOld = LineDiff.MapNewToOld(diff, ConvertedEditor.LineCount);
        _oldToNew = LineDiff.MapOldToNew(diff, OriginalEditor.LineCount);

        DiffSummary.Text = diff.Identical
            ? "No differences."
            : $"{_blocks.Count} change(s): {diff.Added} line(s) new or changed on the right, {diff.Removed} on the left. Indentation is ignored." +
              (diff.Approximate ? " The files differ a lot, so highlights are approximate." : "");
    }

    private void NextChange_Click(object sender, RoutedEventArgs e) => GoToChange(true);
    private void PrevChange_Click(object sender, RoutedEventArgs e) => GoToChange(false);

    private void GoToChange(bool forward)
    {
        if (_blocks.Count == 0) return;
        var current = ConvertedEditor.CaretLine;
        var block = forward
            ? _blocks.FirstOrDefault(b => b.NewStart > current) ?? _blocks[0]
            : _blocks.LastOrDefault(b => b.NewStart < current) ?? _blocks[^1];
        var i = _blocks.IndexOf(block);
        ConvertedEditor.GoToLine(block.NewStart, select: block.Added.Count > 0);
        if (SyncScrollBox.IsChecked != true) OriginalEditor.GoToLine(block.OldStart, select: false);
        DiffSummary.Text = $"Change {i + 1} of {_blocks.Count}" + (block.Added.Count == 0 ? $" ({block.Removed.Count} line(s) removed)" : "");
    }

    /// <summary>Keeps the matching line of the other side at the top while scrolling.</summary>
    private void SyncScroll(ConfigEditor from, ConfigEditor to, int[] map)
    {
        if (SyncScrollBox?.IsChecked != true || map.Length == 0) return;
        var top = from.TopLine;
        if (from == _syncIgnoreEditor && Math.Abs(top - _syncIgnoreLine) <= 1) return; // our own scroll echoing back
        if (top <= 0 || top >= map.Length || map[top] <= 0) return;
        _syncIgnoreEditor = to;
        _syncIgnoreLine = map[top];
        to.ScrollLineToTop(map[top]);
    }

    /// <summary>Updates banners, read-only state and headers to match the current edit state.</summary>
    private void UpdateEditState()
    {
        var hasOutput = !string.IsNullOrEmpty(ConvertedEditor.Text);
        OriginalDirtyBanner.Visibility = OriginalDirty ? Visibility.Visible : Visibility.Collapsed;
        OriginalEditedBanner.Visibility = !OriginalDirty && OriginalEdited ? Visibility.Visible : Visibility.Collapsed;
        OriginalHeader.Text = "Original config" + (_sourceModel != null ? $" – {_sourceModel.DisplayName}" : "") +
                              (OriginalDirty ? "  • edits not applied" : OriginalEdited ? "  (edited)" : "");

        var edited = ConvertedEdited;
        var staleEdits = edited && (_conversionStale || _pendingGenerated != _generatedText);
        ConvertedStaleBanner.Visibility = staleEdits ? Visibility.Visible : Visibility.Collapsed;
        ConvertedEditedBanner.Visibility = edited && !staleEdits ? Visibility.Visible : Visibility.Collapsed;
        ConvertedHeader.Text = "Converted config" + (_state != null ? $" – {_state.Target.DisplayName}" : "") + (edited ? "  (edited by hand)" : "");
        ConvertedEditor.IsReadOnly = !hasOutput && !edited;
        CompareTab.Header = "Compare & edit" + (OriginalDirty || edited ? " •" : "");

        ConvertedPlaceholder.Visibility = hasOutput ? Visibility.Collapsed : Visibility.Visible;
        ConvertedPlaceholderText.Text = _conversionError
            ?? (_source == null || _state == null
                ? "Open a configuration and complete the port mapping to see the converted config."
                : !_state.IsComplete
                    ? "Fix the port mapping (no red lines) to see the converted config."
                    : !GroupsComplete
                        ? "Fix the red lines in step 4 (Aggregates & switches) to see the converted config."
                    : !IpsecComplete
                        ? "Enter a new local ID and pre-shared key for every tunnel in step 5 (IPsec keys) to see the converted config."
                        : "Generating…");

        if (_result != null)
        {
            var msgs = _result.Messages.OrderByDescending(m => m.Severity)
                .Select(m => new StatusLine($"{m.Severity}: {m.Message}", m.Severity switch { Severity.Error => ErrorBrush, Severity.Warning => WarnBrush, _ => InfoBrush }))
                .ToList();
            MessageList.ItemsSource = msgs;
            var errors = _result.Messages.Count(m => m.Severity == Severity.Error);
            var warnings = _result.Messages.Count(m => m.Severity == Severity.Warning);
            MessagesExpander.Header = $"Conversion messages – {errors} error(s), {warnings} warning(s), {_result.Renames.Count} references renamed";
            MessagesExpander.Visibility = Visibility.Visible;
            if (errors > 0) MessagesExpander.IsExpanded = true;
        }
        else MessagesExpander.Visibility = Visibility.Collapsed;

        if (_state != null) RefreshStatus();
        UpdateNav();
    }

    // ------------------------------------------------------------------ step 7: summary

    private void RefreshSummary()
    {
        System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        try { EnsureConverted(); }
        finally { System.Windows.Input.Mouse.OverrideCursor = null; }
        UpdateEditState();

        var lines = new List<StatusLine>();
        if (_state == null || _sourceModel == null || _source == null)
        {
            SummaryList.ItemsSource = new[] { new StatusLine("Open a configuration first.", InfoBrush) };
            ExportButton.IsEnabled = false;
            return;
        }
        lines.Add(new StatusLine($"From {_sourceModel.DisplayName} \"{_source.Hostname}\" to {_state.Target.DisplayName} \"{EffectiveHostname}\", keeping FortiOS {_source.FirmwareDescription}.", InfoBrush));
        lines.Add(new StatusLine("Ports: " + string.Join(",  ", _state.Links.Select(l => $"{l.Source} → {l.Target}")), InfoBrush));
        var notConverted = _sourceModel.Ports.Where(p => _state.StatusOfSource(p.Name) == SourcePortStatus.UnmappedInUse).Select(p => p.Name).ToList();
        if (notConverted.Count > 0)
            lines.Add(new StatusLine($"⚠ Not converted (settings removed): {string.Join(", ", notConverted)}", WarnBrush));
        if (_groups != null)
            foreach (var (g, ports) in _groups.ToMembers())
                lines.Add(ports.Count > 0
                    ? new StatusLine($"{g} ({_groups.FindGroup(g)!.KindLabel}): {string.Join(", ", ports)}", InfoBrush)
                    : new StatusLine($"⚠ {g} ({_groups.FindGroup(g)!.KindLabel}): no ports – not converted", WarnBrush));
        if (_currentTunnels.Count > 0)
            lines.Add(new StatusLine($"IPsec: {_currentTunnels.Count} tunnel(s) get a new local ID and pre-shared key ({string.Join(", ", _currentTunnels.Select(t => $"{t.Title} → {t.LocalId}"))}). The keys are in the .conf in clear text.", InfoBrush));
        if (OriginalEdited || OriginalDirty) lines.Add(new StatusLine("⚠ The original config was edited in the app" + (OriginalDirty ? " (and has edits that aren't applied yet)." : "."), WarnBrush));
        if (ConvertedEdited) lines.Add(new StatusLine("⚠ The converted config was edited by hand.", WarnBrush));

        if (_result != null)
        {
            var errors = _result.Messages.Count(m => m.Severity == Severity.Error);
            var warnings = _result.Messages.Count(m => m.Severity == Severity.Warning);
            lines.Add(new StatusLine($"{_result.Renames.Count} interface references renamed, {errors} error(s), {warnings} warning(s):", errors > 0 ? ErrorBrush : InfoBrush));
            foreach (var m in _result.Messages.Where(m => m.Severity != Severity.Info).OrderByDescending(m => m.Severity))
                lines.Add(new StatusLine($"   {(m.Severity == Severity.Error ? "✘" : "⚠")} {m.Message}", m.Severity == Severity.Error ? ErrorBrush : WarnBrush));
        }
        else if (_conversionError != null) lines.Add(new StatusLine("✘ " + _conversionError, ErrorBrush));

        SummaryList.ItemsSource = lines;
        ExportButton.IsEnabled = ReadyToConvert && (_result != null || ConvertedEdited);
    }

    // ------------------------------------------------------------------ step 4: export

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state == null || _source == null || _sourceModel == null || !_state.IsComplete) return;
        if (!IpsecComplete)
        {
            Tabs.SelectedItem = IpsecTab;
            return;
        }

        if (OriginalDirty)
        {
            var a = MessageBox.Show(this, "The original config has edits that are not applied. Apply them before exporting?\n\nYes = apply and continue, No = export without them.",
                "Export", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (a == MessageBoxResult.Cancel) return;
            if (a == MessageBoxResult.Yes && !ApplyOriginal()) return;
            if (!IpsecComplete) { Tabs.SelectedItem = IpsecTab; return; }
        }

        var hostname = HostnameBox.Text.Trim();
        if (hostname.Length > 0 && !IsValidHostname(hostname))
        {
            MessageBox.Show(this, "Hostnames can be at most 35 characters, with no spaces or quotes.", "Hostname", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EnsureConverted();
        UpdateEditState();
        if (_result == null || _request == null)
        {
            MessageBox.Show(this, "The conversion could not be produced. " + (_conversionError ?? ""), "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var edited = ConvertedEdited;
        var text = edited ? ConvertedEditor.Text : _generatedText!;
        if (edited)
        {
            if (ConvertedStaleBanner.Visibility == Visibility.Visible &&
                MessageBox.Show(this, "Your hand edits to the converted config were made before the latest mapping/keys/hostname/original changes, so they don't include them.\n\nExport your edited text anyway?",
                    "Export", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                var check = FortiConfigParser.Parse(text);
                if (check.Platform != _state.Target.Platform &&
                    MessageBox.Show(this, $"The edited config's #platform line is \"{check.Platform}\", not {_state.Target.Platform}. Export anyway?",
                        "Export", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            }
            catch (ConfigParseException ex)
            {
                MessageBox.Show(this, "The edited converted config has a syntax problem and was not exported:\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
                Tabs.SelectedItem = CompareTab;
                return;
            }
        }

        var errors = _result.Messages.Where(m => m.Severity == Severity.Error).ToList();
        if (errors.Count > 0)
        {
            var answer = MessageBox.Show(this,
                "The converted file has problems that must be fixed by hand before it will load cleanly:\n\n" +
                string.Join("\n\n", errors.Select(m => "• " + m.Message)) + "\n\nSave it anyway?",
                "Export", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        var baseName = SafeFileName(_result.Output.Hostname ?? Path.GetFileNameWithoutExtension(_sourcePath) ?? "fortigate");
        var dlg = new SaveFileDialog
        {
            Title = "Save converted configuration",
            FileName = $"{baseName}-{_state.Target.Model}-converted.conf",
            Filter = "FortiGate config (*.conf)|*.conf|All files (*.*)|*.*",
            InitialDirectory = Path.GetDirectoryName(_sourcePath),
        };
        if (dlg.ShowDialog(this) != true) return;

        var notes = new List<string>();
        if (OriginalEdited) notes.Add("The original config was edited in the app before conversion.");
        if (edited) notes.Add("The converted config was edited by hand after generation.");
        var reportPath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileNameWithoutExtension(dlg.FileName) + "-report.html");
        try
        {
            File.WriteAllText(dlg.FileName, text, new UTF8Encoding(false));
            File.WriteAllText(reportPath, ReportWriter.ToHtml(_request, _result, _sourcePath!, dlg.FileName, notes.Count > 0 ? string.Join(" ", notes) : null), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not save the files:\n\n" + ex.Message, "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _unsavedEdits = false;

        var warnings = _result.Messages.Count(m => m.Severity == Severity.Warning);
        var keys = _result.UpdatedTunnels.Count;
        ExportResult.Text = $"Saved {dlg.FileName} and the report.";
        var open = MessageBox.Show(this,
            $"Saved:\n{dlg.FileName}\n{reportPath}\n\n" +
            $"{_result.Renames.Count} interface references renamed, {errors.Count} error(s), {warnings} warning(s)." +
            (notes.Count > 0 ? "\n" + string.Join("\n", notes) : "") +
            (keys > 0 ? $"\n\nThe .conf file contains {keys} IPsec pre-shared key(s) in clear text. Keep it private and delete it once the new firewall is set up." : "") + "\n\n" +
            "A network engineer must review the report and the converted config, and test it on a lab unit, before it is used in production.\n\n" +
            "Open the report now?",
            "Export complete", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (open == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo(reportPath) { UseShellExecute = true }); }
            catch { /* no browser associated */ }
        }
    }

    private static bool IsValidHostname(string h) => h.Length <= 35 && !h.Any(c => char.IsWhiteSpace(c) || c == '"');

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ------------------------------------------------------------------ unsaved work

    private bool HasUnsavedEdits => _unsavedEdits && (OriginalDirty || OriginalEdited || ConvertedEdited);

    private bool ConfirmDiscardEdits() =>
        !HasUnsavedEdits ||
        MessageBox.Show(this, "You have edited the configuration and not exported or saved it. Discard your edits?", "Unsaved edits",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscardEdits()) e.Cancel = true;
    }
}

/// <summary>One row of the IPsec keys tab: a tunnel plus what the user typed for it.</summary>
public sealed class TunnelInput : INotifyPropertyChanged
{
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush Missing = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush OkRow = Brushes.White;
    private static readonly Brush MissingRow = new SolidColorBrush(Color.FromRgb(0xFF, 0xFB, 0xEB));

    private string _localId = "";
    private string _psk = "";

    public TunnelInput(IpsecTunnel tunnel) => Tunnel = tunnel;

    public IpsecTunnel Tunnel { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Edited;

    public string LocalId
    {
        get => _localId;
        set { if (_localId == value) return; _localId = value; _localIdIsDefault = false; OnEdited(); }
    }

    /// <summary>True while the local ID is the automatic default (the new hostname), i.e. not typed by the user.</summary>
    private bool _localIdIsDefault = true;

    /// <summary>Sets the local ID to <paramref name="hostname"/> unless the user has typed their own.</summary>
    public void ApplyDefaultLocalId(string? hostname)
    {
        if (!_localIdIsDefault || string.IsNullOrEmpty(hostname) || hostname == _localId) return;
        _localId = hostname;
        OnEdited();
    }

    public string PreSharedKey
    {
        get => _psk;
        set { if (_psk == value) return; _psk = value; OnEdited(); }
    }

    public IpsecCredentials Credentials => new(_localId, _psk);
    public string? Problem => IpsecTunnels.Validate(Credentials);

    public string Title => Tunnel.Name;

    public string Details
    {
        get
        {
            var remote = Tunnel.RemoteGateway ?? (Tunnel.Type == "dynamic" ? "dial-up (any)" : "?");
            var s = $"{Tunnel.Kind} · interface {Tunnel.Interface ?? "?"} · remote gateway {remote}";
            return string.IsNullOrWhiteSpace(Tunnel.Comments) ? s : s + $"\n{Tunnel.Comments}";
        }
    }

    public string CurrentLocalId => string.IsNullOrEmpty(Tunnel.LocalId) ? "Currently: (none)" : $"Currently: {Tunnel.LocalId}";
    public string Status => Problem == null ? "✔ Ready" : Problem;
    public Brush StatusBrush => Problem == null ? Ok : Missing;
    public Brush RowBackground => Problem == null ? OkRow : MissingRow;

    public void Update(IpsecTunnel tunnel)
    {
        Tunnel = tunnel;
        Notify(nameof(Title), nameof(Details), nameof(CurrentLocalId));
    }

    private void OnEdited()
    {
        Notify(nameof(LocalId), nameof(PreSharedKey), nameof(Status), nameof(StatusBrush), nameof(RowBackground));
        Edited?.Invoke(this, EventArgs.Empty);
    }

    private void Notify(params string[] names)
    {
        foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
