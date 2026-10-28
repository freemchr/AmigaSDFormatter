using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Diagnostics;

public enum FindingSeverity { Ok, Info, Warning, Problem }

public enum Confidence { Low, Medium, High }

public enum CompatibilityVerdict
{
    /// <summary>Layout matches the documented requirements of the selected interface.</summary>
    Compatible,
    /// <summary>No problems found, but some requirements are unverified.</summary>
    LikelyCompatible,
    /// <summary>Usable, but the Amiga will show partitions as Uninitialized until formatted there.</summary>
    NeedsAmigaFormat,
    /// <summary>At least one finding indicates the card will not work as intended.</summary>
    Incompatible,
    /// <summary>The layout could not be identified.</summary>
    Unknown,
    /// <summary>No hardware profile selected: structure only.</summary>
    NotAssessed,
}

public sealed record Finding
{
    public required string Code { get; init; }
    public required FindingSeverity Severity { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public Confidence Confidence { get; init; } = Confidence.High;
    /// <summary>True when this finding is a plausible cause of an "Uninitialized" volume on the Amiga.</summary>
    public bool ExplainsUninitialized { get; init; }
}

public sealed record Recommendation(string Text, RecommendedAction Action = RecommendedAction.None);

public enum RecommendedAction { None, PrepareDataCard, PrepareHardDriveCard, FormatOnAmiga, BackupFirst, RestoreAuthorisedImage, NoChangeNeeded }

public sealed record DiagnosticReport
{
    public required DiskInspection Inspection { get; init; }
    public string? DeviceName { get; init; }
    public HardwareProfile? Profile { get; init; }
    public StorageInterfaceProfile? Interface { get; init; }
    public IntendedUse? Use { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<Recommendation> Recommendations { get; init; }
    public required CompatibilityVerdict Verdict { get; init; }
    public required Confidence Confidence { get; init; }
    public required string Summary { get; init; }
    public string ApplicationVersion { get; init; } = typeof(DiagnosticReport).Assembly.GetName().Version?.ToString() ?? "";

    public IEnumerable<Finding> UninitializedCauses =>
        Findings.Where(f => f.ExplainsUninitialized).OrderByDescending(f => f.Confidence);
}
