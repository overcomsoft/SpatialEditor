using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

/// <summary>A drawing (layout) whose working copy and saved versions are managed together.</summary>
public sealed record LayoutInfo(
    long Id,
    string Name,
    string? Description,
    long? BaseVersionId,
    DateTime CreatedAt,
    string? CreatedByName,
    int ObjectCount);

public static class VersionStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Baseline = "baseline";
    public const string Archived = "archived";
}

public static class VersionKind
{
    /// <summary>Saved by a user on purpose.</summary>
    public const string Manual = "manual";
    /// <summary>Saved automatically after an edit save.</summary>
    public const string Auto = "auto";
    /// <summary>Saved automatically before a restore replaced the working copy.</summary>
    public const string PreRestore = "pre-restore";
}

/// <summary>One saved snapshot of a layout.</summary>
public sealed record LayoutVersion(
    long Id,
    long LayoutId,
    int VersionNo,
    string Label,
    string? Comment,
    long? ParentVersionId,
    string Status,
    string Kind,
    long? CreatedBy,
    string? CreatedByName,
    DateTime CreatedAt,
    int BlockCount,
    int FeatureCount,
    string ContentHash);

/// <summary>An equipment block instance as stored in a version (or the working copy).</summary>
public sealed record VersionBlockItem(
    Guid ObjectUid,
    string BlockName,
    string? LayerName,
    string? SourceFile,
    double? InsertX,
    double? InsertY,
    double RotationDeg,
    double ScaleX,
    double ScaleY,
    IReadOnlyDictionary<string, string?> Attributes,
    Geometry Geometry,
    Geometry OuterGeometry);

/// <summary>A general (non-block) object as stored in a version (or the working copy).</summary>
public sealed record VersionFeatureItem(
    Guid ObjectUid,
    string FeatureType,
    string? BlockName,
    string? LayerName,
    string? SourceFile,
    IReadOnlyDictionary<string, string?> Attributes,
    Geometry Geometry,
    Geometry? OuterGeometry);

public enum DiffKind
{
    Added,
    Removed,
    Moved,
    Rotated,
    Reshaped,
    AttributesChanged
}

/// <summary>One difference between two states (version vs version, or version vs working copy).</summary>
public sealed record DiffEntry(
    DiffKind Kind,
    bool IsBlock,
    Guid ObjectUid,
    string Name,
    string? LayerName,
    string? Before,
    string? After,
    Geometry? BeforeGeometry,
    Geometry? AfterGeometry);

public sealed record VersionDiff(IReadOnlyList<DiffEntry> Entries)
{
    public int Count(DiffKind kind) => Entries.Count(entry => entry.Kind == kind);
    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>Where the working copy of a layout stands relative to the version it started from.</summary>
public sealed record LayoutStatus(long LayoutId, string LayoutName, int? BaseVersionNo, string? BaseVersionLabel, bool IsModified);
