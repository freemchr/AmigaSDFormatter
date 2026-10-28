using System.Text.Json;
using System.Text.Json.Serialization;
using AmigaSdCreator.Core.Partitioning;

namespace AmigaSdCreator.Core.Profiles;

/// <summary>How well a profile or interface has been checked against authoritative sources and hardware.</summary>
public enum VerificationStatus
{
    /// <summary>Requirements confirmed from official documentation.</summary>
    Verified,
    /// <summary>Structures validated with tools; some hardware behaviour still unconfirmed.</summary>
    Partial,
    /// <summary>Plausible but unconfirmed. Usable with explicit acknowledgement.</summary>
    Experimental,
    /// <summary>Not implemented; shown for information only.</summary>
    Planned,
    /// <summary>Holds a system installation: backup and authorised image restore only.</summary>
    Protected,
}

public enum InterfaceKind { SdSlot, Ide, Scsi, Usb }

public enum IntendedUse { DataExchange, HardDrive, BootableSystem }

public enum LayoutKind
{
    /// <summary>MBR with one FAT32 partition.</summary>
    MbrFat32,
    /// <summary>Amiga Rigid Disk Block with Amiga partitions.</summary>
    Rdb,
    /// <summary>Restore a complete, authorised disk image; no layout is generated.</summary>
    ImageRestore,
    /// <summary>PiStorm / Emu68: MBR with a FAT32 boot partition and an Amiga partition (type 0x76) holding an RDB.</summary>
    Emu68,
}

public enum Bootability { No, Yes, Unverified }

public sealed record SourceReference(string Id, string Title, string Url, string? Retrieved = null, string? Note = null);

public sealed record SourcedFact(string Text, string Source);

public sealed record InterfaceDefaults
{
    public string? FatLabel { get; init; }
    public string? DosType { get; init; }
    public string? MaxTransfer { get; init; }
    public string? Mask { get; init; }
    public string? DeviceNamePrefix { get; init; }
    public string? VolumeNamePrefix { get; init; }

    public DosType DefaultDosType => DosType is null ? Partitioning.DosType.FfsIntl : Partitioning.DosType.Parse(DosType);
    public uint DefaultMaxTransfer => ParseHex(MaxTransfer, 0x1FE00);
    public uint DefaultMask => ParseHex(Mask, 0x7FFFFFFE);

    private static uint ParseHex(string? s, uint fallback) =>
        string.IsNullOrWhiteSpace(s) ? fallback : Convert.ToUInt32(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, 16);
}

public sealed record InterfaceConstraints
{
    public int MaxSectorSize { get; init; } = 512;
    public long? RecommendedMaxCapacityBytes { get; init; }
    public long? MaxFatClusterBytes { get; init; }
    public bool RequiresMbr { get; init; }
    public long? MaxPartitionEndBytes { get; init; }
    public long? MaxFfsPartitionBytes { get; init; }
    public bool RestoreOnly { get; init; }
}

public sealed record CompatibilityMode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public long? MaxPartitionEndBytes { get; init; }
    public long? MaxFfsPartitionBytes { get; init; }
    public long? BootPartitionMaxEndBytes { get; init; }
    public IReadOnlyList<string> RomFileSystems { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record StorageInterfaceProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Short title and subtitle for the home-screen tile.</summary>
    public string? TileTitle { get; init; }
    public string? TileSubtitle { get; init; }
    public InterfaceKind Kind { get; init; }
    public VerificationStatus Status { get; init; }
    public IReadOnlyList<IntendedUse> Uses { get; init; } = [];
    public IReadOnlyList<LayoutKind> Layouts { get; init; } = [];
    public Bootability Bootable { get; init; }
    public InterfaceDefaults Defaults { get; init; } = new();
    public InterfaceConstraints Constraints { get; init; } = new();
    public IReadOnlyList<string> RomFileSystems { get; init; } = [];
    public IReadOnlyList<CompatibilityMode> CompatibilityModes { get; init; } = [];
    public IReadOnlyList<SourcedFact> Facts { get; init; } = [];
    public IReadOnlyList<string> Unverified { get; init; } = [];

    /// <summary>Effective limits: the selected compatibility mode overrides interface constraints.</summary>
    public (long? MaxEnd, long? MaxFfs, long? BootMaxEnd, IReadOnlyList<DosType> RomFs) EffectiveLimits(string? modeId)
    {
        var mode = CompatibilityModes.FirstOrDefault(m => m.Id == modeId) ?? CompatibilityModes.FirstOrDefault();
        var romFs = (mode?.RomFileSystems.Count > 0 ? mode.RomFileSystems : RomFileSystems).Select(DosType.Parse).ToList();
        return mode is null
            ? (Constraints.MaxPartitionEndBytes, Constraints.MaxFfsPartitionBytes, null, romFs)
            : (mode.MaxPartitionEndBytes, mode.MaxFfsPartitionBytes ?? Constraints.MaxFfsPartitionBytes, mode.BootPartitionMaxEndBytes, romFs);
    }
}

public sealed record HardwareProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Family { get; init; } = "";
    public VerificationStatus Status { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<StorageInterfaceProfile> Interfaces { get; init; } = [];

    public override string ToString() => Name;
}

public sealed record ProfileCatalogDocument
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<SourceReference> Sources { get; init; } = [];
    public IReadOnlyList<HardwareProfile> Profiles { get; init; } = [];
}

/// <summary>
/// Loads the built-in hardware profiles, then merges any *.json profile files from a user
/// directory (a user profile with the same id replaces the built-in one).
/// </summary>
public sealed class ProfileCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    public IReadOnlyList<HardwareProfile> Profiles { get; }
    public IReadOnlyDictionary<string, SourceReference> Sources { get; }
    public IReadOnlyList<string> LoadErrors { get; }

    private ProfileCatalog(IReadOnlyList<HardwareProfile> profiles, IReadOnlyDictionary<string, SourceReference> sources, IReadOnlyList<string> errors)
    {
        Profiles = profiles;
        Sources = sources;
        LoadErrors = errors;
    }

    public HardwareProfile? Find(string id) => Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public (HardwareProfile Profile, StorageInterfaceProfile Interface)? FindInterface(string interfaceId)
    {
        foreach (var p in Profiles)
            foreach (var i in p.Interfaces)
                if (i.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase)) return (p, i);
        return null;
    }

    public static ProfileCatalog LoadBuiltIn() => Load(null);

    public static ProfileCatalog Load(string? userDirectory)
    {
        var errors = new List<string>();
        using var stream = typeof(ProfileCatalog).Assembly.GetManifestResourceStream("AmigaSdCreator.Core.Profiles.profiles.json")
            ?? throw new InvalidOperationException("Built-in profile resource is missing.");
        var builtIn = JsonSerializer.Deserialize<ProfileCatalogDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Built-in profiles could not be parsed.");

        var profiles = builtIn.Profiles.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var order = builtIn.Profiles.Select(p => p.Id).ToList();
        var sources = builtIn.Sources.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);

        if (userDirectory is not null && Directory.Exists(userDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(userDirectory, "*.json").Order())
            {
                try
                {
                    var doc = JsonSerializer.Deserialize<ProfileCatalogDocument>(File.ReadAllText(file), JsonOptions);
                    if (doc is null) continue;
                    foreach (var s in doc.Sources) sources[s.Id] = s;
                    foreach (var p in doc.Profiles)
                    {
                        if (!profiles.ContainsKey(p.Id)) order.Add(p.Id);
                        profiles[p.Id] = p;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or FormatException)
                {
                    errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        return new ProfileCatalog(order.Select(id => profiles[id]).ToList(), sources, errors);
    }
}
