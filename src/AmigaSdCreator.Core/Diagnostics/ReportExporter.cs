using System.Text;
using System.Text.Json;
using AmigaSdCreator.Core.Layout;
using AmigaSdCreator.Core.Profiles;

namespace AmigaSdCreator.Core.Diagnostics;

/// <summary>
/// Exports diagnostic reports. Reports contain layout information only: no serial numbers,
/// file names or file contents, so they are safe to share on forums.
/// </summary>
public static class ReportExporter
{
    public static string ToMarkdown(DiagnosticReport report)
    {
        var d = report.Inspection;
        var sb = new StringBuilder();
        sb.AppendLine("# Amiga SD Creator - Diagnostic report");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {d.InspectedUtc:yyyy-MM-dd HH:mm} UTC (Amiga SD Creator {report.ApplicationVersion})");
        if (report.DeviceName is not null) sb.AppendLine($"- Device: {report.DeviceName}");
        sb.AppendLine($"- Capacity: {Format.Bytes(d.LengthBytes)} ({d.LengthBytes:N0} bytes), {d.SectorSize}-byte sectors");
        sb.AppendLine($"- Partition table: {d.SchemeDescription}");
        if (report.Profile is not null) sb.AppendLine($"- Hardware: {report.Profile.Name} - {report.Interface?.Name} ({report.Interface?.Status})");
        if (report.Use is not null) sb.AppendLine($"- Intended use: {report.Use}");
        sb.AppendLine();
        sb.AppendLine($"## Result: {VerdictText(report.Verdict)}");
        sb.AppendLine();
        sb.AppendLine($"{report.Summary} (confidence: {report.Confidence})");
        sb.AppendLine();

        var causes = report.UninitializedCauses.ToList();
        if (causes.Count > 0)
        {
            sb.AppendLine("### Likely reasons for an \"Uninitialized\" volume");
            sb.AppendLine();
            foreach (var c in causes) sb.AppendLine($"- **{c.Title}** (confidence: {c.Confidence}) - {c.Detail}");
            sb.AppendLine();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        foreach (var f in report.Findings)
        {
            sb.AppendLine($"- [{f.Severity}] **{f.Title}** (confidence: {f.Confidence})");
            if (!string.IsNullOrWhiteSpace(f.Detail)) sb.AppendLine($"  {f.Detail}");
        }
        sb.AppendLine();

        if (report.Recommendations.Count > 0)
        {
            sb.AppendLine("## Recommendations");
            sb.AppendLine();
            int i = 1;
            foreach (var r in report.Recommendations) sb.AppendLine($"{i++}. {r.Text}");
            sb.AppendLine();
        }

        sb.AppendLine("## Partitions");
        sb.AppendLine();
        if (d.Partitions.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| # | Table | Name | Start | Size | Type | Filesystem | Label | Boot |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            foreach (var p in d.Partitions)
                sb.AppendLine($"| {p.Index} | {p.Table} | {p.Name} | {Format.Bytes(p.StartByte)} | {Format.Bytes(p.LengthBytes)} | {p.TypeDescription} | {p.Filesystem.DisplayName} | {p.Filesystem.Label ?? ""} | {(p.Bootable ? $"yes ({p.BootPriority})" : "")} |");
        }
        sb.AppendLine();

        if (d.Rdb is { } rdb)
        {
            var g = rdb.Rdsk.Geometry;
            sb.AppendLine("## RDB details");
            sb.AppendLine();
            sb.AppendLine($"- RDSK at block {rdb.Rdsk.Location}, checksum {(rdb.Rdsk.ChecksumValid ? "valid" : "INVALID")}");
            sb.AppendLine($"- Geometry: {g.Cylinders} cylinders, {g.Heads} heads, {g.Sectors} sectors ({g.CylinderBlocks} blocks/cylinder)");
            sb.AppendLine($"- Reserved RDB area: blocks {rdb.Rdsk.RdbBlocksLo}-{rdb.Rdsk.RdbBlocksHi}, partitions from cylinder {rdb.Rdsk.LoCylinder}");
            sb.AppendLine($"- Disk: {rdb.Rdsk.DiskVendor} {rdb.Rdsk.DiskProduct} {rdb.Rdsk.DiskRevision}".TrimEnd());
            foreach (var p in rdb.Partitions)
                sb.AppendLine($"- {p.DriveName}: cyl {p.LowCyl}-{p.HighCyl}, {p.DosType} ({p.DosType.FriendlyName}), MaxTransfer {Format.Hex(p.MaxTransfer)}, Mask {Format.Hex(p.Mask)}, Buffers {p.NumBuffers}, Reserved {p.Reserved}, BootPri {p.BootPri}{(p.IsBootable ? ", bootable" : "")}{(p.IsNoMount ? ", no-mount" : "")}");
            foreach (var fs in rdb.FileSystems)
                sb.AppendLine($"- Filesystem {fs.DosType} ({fs.DosType.FriendlyName}) version {fs.VersionText}, {fs.LoadSegBlockCount} LSEG blocks");
            sb.AppendLine();
        }

        if (report.Interface is { Unverified.Count: > 0 } iface)
        {
            sb.AppendLine("## Unverified requirements for this interface");
            sb.AppendLine();
            foreach (var u in iface.Unverified) sb.AppendLine($"- {u}");
            sb.AppendLine();
        }
        sb.AppendLine("_This report was produced by read-only inspection. No data on the card was changed._");
        return sb.ToString();
    }

    public static string ToJson(DiagnosticReport report)
    {
        var d = report.Inspection;
        var dto = new
        {
            generatedUtc = d.InspectedUtc,
            report.ApplicationVersion,
            device = report.DeviceName,
            capacityBytes = d.LengthBytes,
            sectorSize = d.SectorSize,
            scheme = d.Scheme.ToString(),
            profile = report.Profile?.Id,
            @interface = report.Interface?.Id,
            use = report.Use?.ToString(),
            verdict = report.Verdict.ToString(),
            confidence = report.Confidence.ToString(),
            report.Summary,
            findings = report.Findings.Select(f => new { f.Code, severity = f.Severity.ToString(), f.Title, f.Detail, confidence = f.Confidence.ToString(), f.ExplainsUninitialized }),
            recommendations = report.Recommendations.Select(r => new { r.Text, action = r.Action.ToString() }),
            partitions = d.Partitions.Select(p => new
            {
                p.Index, p.Table, p.Name, p.StartByte, p.LengthBytes, p.TypeDescription,
                filesystem = p.Filesystem.Kind.ToString(), p.Filesystem.Label, dosType = p.DosType?.ToString(), p.Bootable, p.BootPriority,
            }),
            problems = d.Problems,
        };
        return JsonSerializer.Serialize(dto, ProfileCatalog.JsonOptions);
    }

    public static string VerdictText(CompatibilityVerdict v) => v switch
    {
        CompatibilityVerdict.Compatible => "Compatible",
        CompatibilityVerdict.LikelyCompatible => "Likely compatible",
        CompatibilityVerdict.NeedsAmigaFormat => "Ready - format partitions on the Amiga",
        CompatibilityVerdict.Incompatible => "Not compatible as configured",
        CompatibilityVerdict.Unknown => "Unknown layout",
        _ => "Structure only (no hardware selected)",
    };
}
