using System.Text.Json;
using System.Text.Json.Serialization;

namespace FortiGateUpgrader.Core.Models;

/// <summary>Physical media of a front-panel port.</summary>
public enum PortMedia
{
    RJ45,
    SFP,
    SFPPlus,
    /// <summary>One interface that can be used through an RJ45 jack or an SFP cage.</summary>
    SharedRJ45SFP,
    /// <summary>One interface that can be used through an RJ45 jack or an SFP+ cage.</summary>
    SharedRJ45SFPPlus,
}

public sealed class PortDefinition
{
    /// <summary>FortiOS interface name, e.g. "internal1", "x1", "port3".</summary>
    public string Name { get; set; } = "";

    /// <summary>Label printed on the front panel, e.g. "WAN1", "3", "A".</summary>
    public string Label { get; set; } = "";

    public PortMedia Media { get; set; } = PortMedia.RJ45;

    /// <summary>Maximum speed in Mbps.</summary>
    public int MaxSpeedMbps { get; set; } = 1000;

    /// <summary>Whether the port can be a member of the hardware (virtual) switch. Null = unknown.</summary>
    public bool? SwitchCapable { get; set; }

    /// <summary>Panel row (0 = top, 1 = bottom).</summary>
    public int Row { get; set; }

    /// <summary>Panel column in port units; fractional values create gaps between port groups.</summary>
    public double Column { get; set; }

    public string MediaDescription => Media switch
    {
        PortMedia.RJ45 => $"RJ45 {SpeedText}",
        PortMedia.SFP => $"SFP {SpeedText}",
        PortMedia.SFPPlus => $"SFP+ {SpeedText}",
        PortMedia.SharedRJ45SFP => $"RJ45 or SFP {SpeedText} (shared)",
        PortMedia.SharedRJ45SFPPlus => $"RJ45 or SFP+ {SpeedText} (shared)",
        _ => SpeedText,
    };

    private string SpeedText => MaxSpeedMbps >= 1000 ? $"{MaxSpeedMbps / 1000}G" : $"{MaxSpeedMbps}M";

    public bool IsOptical => Media is PortMedia.SFP or PortMedia.SFPPlus;

    /// <summary>Speed settings ("set speed ...") that FortiOS accepts on this kind of port. Approximate.</summary>
    public IReadOnlySet<string> AllowedSpeeds
    {
        get
        {
            var s = new HashSet<string> { "auto" };
            var copper = Media is PortMedia.RJ45 or PortMedia.SharedRJ45SFP or PortMedia.SharedRJ45SFPPlus;
            if (copper)
            {
                s.UnionWith(new[] { "10half", "10full", "100half", "100full", "1000full", "1000auto" });
                if (MaxSpeedMbps >= 2500) s.Add("2500full");
                if (MaxSpeedMbps >= 5000) s.Add("5000full");
                if (MaxSpeedMbps >= 10000) s.Add("10000full");
            }
            if (Media is PortMedia.SFP or PortMedia.SharedRJ45SFP)
                s.UnionWith(new[] { "1000full", "1000auto" });
            if (Media is PortMedia.SFPPlus or PortMedia.SharedRJ45SFPPlus)
                s.UnionWith(new[] { "1000full", "1000auto", "10000full", "10000auto" });
            return s;
        }
    }
}

public sealed class ModelProfile
{
    /// <summary>Short model name, e.g. "61F".</summary>
    public string Model { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>Value of the "#platform=" header, e.g. "FORTIGATE-61F".</summary>
    public string Platform { get; set; } = "";

    /// <summary>Prefix of "#config-version=", e.g. "FGT61F".</summary>
    public string ConfigPrefix { get; set; } = "";

    /// <summary>False when the profile was built from documentation and not checked against a real config.</summary>
    public bool Verified { get; set; }

    public string? Notes { get; set; }

    /// <summary>Optional product photo, relative to the profile directory.</summary>
    public string? Image { get; set; }

    public List<PortDefinition> Ports { get; set; } = new();

    /// <summary>Interfaces that exist on the box but are not front-panel ports (e.g. "modem").</summary>
    public List<string> VirtualPorts { get; set; } = new();

    public PortDefinition? FindPort(string name) => Ports.FirstOrDefault(p => p.Name == name);

    public bool HasInterface(string name) => FindPort(name) != null || VirtualPorts.Contains(name);

    public override string ToString() => DisplayName;
}

public sealed class UpgradePath
{
    public string Source { get; set; } = "";
    public List<string> Targets { get; set; } = new();
}

/// <summary>
/// Model profiles and allowed upgrade paths, loaded from JSON files: either a folder on disk
/// or an in-memory file set (the copy embedded in the exe).
/// </summary>
public sealed class ProfileCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyDictionary<string, byte[]> _files;

    private ProfileCatalog(string source, IReadOnlyDictionary<string, byte[]> files, IEnumerable<ModelProfile> profiles, IEnumerable<UpgradePath> paths)
    {
        Source = source;
        _files = files;
        Profiles = profiles.ToList();
        Paths = paths.ToList();
    }

    /// <summary>Where the profiles came from, for messages (a folder path or "built-in").</summary>
    public string Source { get; }
    public IReadOnlyList<ModelProfile> Profiles { get; }
    public IReadOnlyList<UpgradePath> Paths { get; }

    /// <summary>Loads every file under <paramref name="directory"/>.</summary>
    public static ProfileCatalog Load(string directory)
    {
        var files = System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(directory, f), File.ReadAllBytes);
        return Load(files, directory);
    }

    /// <summary>Loads from relative path → content. Paths may use / or \ and are case-insensitive.</summary>
    public static ProfileCatalog Load(IEnumerable<KeyValuePair<string, byte[]>> files, string source)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in files) map[Normalize(name)] = data;

        var profiles = new List<ModelProfile>();
        foreach (var name in map.Keys.Where(k => !k.Contains('/') && k.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Equals("upgrade-paths.json", StringComparison.OrdinalIgnoreCase)) continue;
            var p = JsonSerializer.Deserialize<ModelProfile>(map[name], JsonOptions)
                    ?? throw new InvalidDataException($"Empty profile: {name}");
            Validate(p, name);
            profiles.Add(p);
        }
        if (profiles.Count == 0) throw new InvalidDataException($"No model profiles found in {source}.");

        var paths = map.TryGetValue("upgrade-paths.json", out var pathsJson)
            ? JsonSerializer.Deserialize<List<UpgradePath>>(pathsJson, JsonOptions) ?? new()
            : new List<UpgradePath>();
        return new ProfileCatalog(source, map, profiles, paths);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static void Validate(ModelProfile p, string file)
    {
        if (string.IsNullOrWhiteSpace(p.Model) || string.IsNullOrWhiteSpace(p.Platform) || string.IsNullOrWhiteSpace(p.ConfigPrefix))
            throw new InvalidDataException($"{file}: model, platform and configPrefix are required.");
        var dup = p.Ports.Select(x => x.Name).Concat(p.VirtualPorts).GroupBy(n => n).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidDataException($"{file}: interface \"{dup.Key}\" is listed twice.");
    }

    public ModelProfile? FindByModel(string model) =>
        Profiles.FirstOrDefault(p => p.Model.Equals(model, StringComparison.OrdinalIgnoreCase));

    public ModelProfile? FindByPlatform(string? platform) =>
        platform == null ? null : Profiles.FirstOrDefault(p => p.Platform.Equals(platform.Trim(), StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ModelProfile> TargetsFor(ModelProfile source) =>
        Paths.Where(x => x.Source.Equals(source.Model, StringComparison.OrdinalIgnoreCase))
             .SelectMany(x => x.Targets)
             .Select(FindByModel)
             .Where(p => p != null)
             .Cast<ModelProfile>()
             .ToList();

    /// <summary>The product photo's bytes, or null when the profile has none.</summary>
    public byte[]? ImageBytes(ModelProfile p) =>
        p.Image != null && _files.TryGetValue(Normalize(p.Image), out var data) ? data : null;
}
