using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

/// <summary>A node of the library classification tree.</summary>
public sealed record BlockCategory(long Id, long? ParentId, string Name);

/// <summary>
/// One reusable equipment block in the global library. Geometry is in the block's own local
/// coordinate system (insertion point at the origin) and belongs to the current version.
/// </summary>
public sealed record LibraryItem(
    long Id,
    string Code,
    string Name,
    long? CategoryId,
    string? CategoryName,
    string? Vendor,
    string? ModelNo,
    string? Description,
    double? Width,
    double? Depth,
    double? Height,
    string Status,
    int CurrentVersion,
    int InstanceCount,
    Geometry? LocalGeometry,
    Geometry? LocalOuterGeometry,
    DateTime UpdatedAt);

/// <summary>Editable catalogue fields of a library item.</summary>
public sealed record LibraryItemEdit(
    string Name,
    long? CategoryId,
    string? Vendor,
    string? ModelNo,
    string? Description,
    double? Height,
    string Status);

public enum LibraryRegisterOutcome
{
    /// <summary>A new library item (and version 1) was created.</summary>
    Created,

    /// <summary>An item with the same shape already exists; the instance was linked to it.</summary>
    LinkedToExisting
}

public sealed record LibraryRegisterResult(LibraryRegisterOutcome Outcome, long LibraryId, string Code);

public static class LibraryStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Deprecated = "deprecated";

    public static readonly IReadOnlyList<string> All = new[] { Draft, Released, Deprecated };
}
