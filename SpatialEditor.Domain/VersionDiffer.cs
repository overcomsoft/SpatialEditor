using System.Globalization;
using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

/// <summary>
/// Compares two states of a drawing (version vs version, or version vs working copy) by the
/// permanent <c>object_uid</c> of each object, so a moved or rotated object is recognised as the same
/// object instead of "one removed, one added".
/// </summary>
public static class VersionDiffer
{
    /// <summary>Positions closer than this (drawing units) count as unmoved.</summary>
    public const double PositionTolerance = 1e-4;

    /// <summary>Angles closer than this (degrees) count as unrotated.</summary>
    public const double AngleTolerance = 1e-4;

    // Placement keys change with every move/rotation and are compared as real numbers instead;
    // the DXF handle only exists on objects that came from a DXF.
    private static readonly HashSet<string> PlacementKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "block_insert_x", "block_insert_y", "block_rotation_deg", "block_scale_x", "block_scale_y", "block_handle"
    };

    public static VersionDiff Compare(
        IReadOnlyCollection<VersionBlockItem> beforeBlocks, IReadOnlyCollection<VersionBlockItem> afterBlocks,
        IReadOnlyCollection<VersionFeatureItem> beforeFeatures, IReadOnlyCollection<VersionFeatureItem> afterFeatures)
    {
        var entries = new List<DiffEntry>();
        CompareBlocks(beforeBlocks, afterBlocks, entries);
        CompareFeatures(beforeFeatures, afterFeatures, entries);
        return new VersionDiff(entries);
    }

    private static void CompareBlocks(
        IReadOnlyCollection<VersionBlockItem> before, IReadOnlyCollection<VersionBlockItem> after, List<DiffEntry> entries)
    {
        var beforeByUid = before.ToDictionary(item => item.ObjectUid);
        var afterByUid = after.ToDictionary(item => item.ObjectUid);

        foreach (var item in after.Where(item => !beforeByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Added, true, item.ObjectUid, item.BlockName, item.LayerName,
                null, Describe(item), null, item.OuterGeometry));
        }

        foreach (var item in before.Where(item => !afterByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Removed, true, item.ObjectUid, item.BlockName, item.LayerName,
                Describe(item), null, item.OuterGeometry, null));
        }

        foreach (var (uid, a) in beforeByUid)
        {
            if (!afterByUid.TryGetValue(uid, out var b))
            {
                continue;
            }

            if (a.InsertX is not null && a.InsertY is not null && b.InsertX is not null && b.InsertY is not null
                && Math.Sqrt(Math.Pow(a.InsertX.Value - b.InsertX.Value, 2) + Math.Pow(a.InsertY.Value - b.InsertY.Value, 2)) > PositionTolerance)
            {
                entries.Add(new DiffEntry(DiffKind.Moved, true, uid, b.BlockName, b.LayerName,
                    $"({Num(a.InsertX.Value)}, {Num(a.InsertY.Value)})", $"({Num(b.InsertX.Value)}, {Num(b.InsertY.Value)})",
                    a.OuterGeometry, b.OuterGeometry));
            }

            if (AngleDifference(a.RotationDeg, b.RotationDeg) > AngleTolerance)
            {
                entries.Add(new DiffEntry(DiffKind.Rotated, true, uid, b.BlockName, b.LayerName,
                    $"{Num(a.RotationDeg)}°", $"{Num(b.RotationDeg)}°", a.OuterGeometry, b.OuterGeometry));
            }

            if (!string.Equals(a.BlockName, b.BlockName, StringComparison.Ordinal)
                || Math.Abs(a.ScaleX - b.ScaleX) > 1e-9 || Math.Abs(a.ScaleY - b.ScaleY) > 1e-9)
            {
                entries.Add(new DiffEntry(DiffKind.Reshaped, true, uid, b.BlockName, b.LayerName,
                    $"{a.BlockName} ×({Num(a.ScaleX)}, {Num(a.ScaleY)})", $"{b.BlockName} ×({Num(b.ScaleX)}, {Num(b.ScaleY)})",
                    a.OuterGeometry, b.OuterGeometry));
            }

            var change = DescribeAttributeChange(a.LayerName, b.LayerName, a.Attributes, b.Attributes);
            if (change is not null)
            {
                entries.Add(new DiffEntry(DiffKind.AttributesChanged, true, uid, b.BlockName, b.LayerName,
                    change.Value.Before, change.Value.After, a.OuterGeometry, b.OuterGeometry));
            }
        }
    }

    private static void CompareFeatures(
        IReadOnlyCollection<VersionFeatureItem> before, IReadOnlyCollection<VersionFeatureItem> after, List<DiffEntry> entries)
    {
        var beforeByUid = before.ToDictionary(item => item.ObjectUid);
        var afterByUid = after.ToDictionary(item => item.ObjectUid);

        foreach (var item in after.Where(item => !beforeByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Added, false, item.ObjectUid, FeatureName(item), item.LayerName,
                null, item.FeatureType, null, item.Geometry));
        }

        foreach (var item in before.Where(item => !afterByUid.ContainsKey(item.ObjectUid)))
        {
            entries.Add(new DiffEntry(DiffKind.Removed, false, item.ObjectUid, FeatureName(item), item.LayerName,
                item.FeatureType, null, item.Geometry, null));
        }

        foreach (var (uid, a) in beforeByUid)
        {
            if (!afterByUid.TryGetValue(uid, out var b))
            {
                continue;
            }

            if (!SameGeometry(a.Geometry, b.Geometry) || !SameGeometry(a.OuterGeometry, b.OuterGeometry))
            {
                entries.Add(new DiffEntry(DiffKind.Reshaped, false, uid, FeatureName(b), b.LayerName,
                    "shape", "shape changed", a.Geometry, b.Geometry));
            }

            var change = DescribeAttributeChange(a.LayerName, b.LayerName, a.Attributes, b.Attributes);
            if (change is not null)
            {
                entries.Add(new DiffEntry(DiffKind.AttributesChanged, false, uid, FeatureName(b), b.LayerName,
                    change.Value.Before, change.Value.After, a.Geometry, b.Geometry));
            }
        }
    }

    private static string FeatureName(VersionFeatureItem item) =>
        string.IsNullOrWhiteSpace(item.BlockName) ? item.FeatureType : item.BlockName!;

    private static string Describe(VersionBlockItem item) =>
        item.InsertX is { } x && item.InsertY is { } y
            ? $"({Num(x)}, {Num(y)}) {Num(item.RotationDeg)}°"
            : $"{Num(item.RotationDeg)}°";

    private static string Num(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Smallest absolute difference between two angles in degrees (0..180), wrap-around aware.</summary>
    public static double AngleDifference(double a, double b)
    {
        var difference = Math.Abs(a - b) % 360.0;
        return difference > 180.0 ? 360.0 - difference : difference;
    }

    private static bool SameGeometry(Geometry? a, Geometry? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.EqualsExact(b, 1e-6);
    }

    private static (string Before, string After)? DescribeAttributeChange(
        string? layerBefore, string? layerAfter,
        IReadOnlyDictionary<string, string?> before, IReadOnlyDictionary<string, string?> after)
    {
        var changes = new List<string>();
        var beforeParts = new List<string>();
        var afterParts = new List<string>();
        if (!string.Equals(layerBefore ?? string.Empty, layerAfter ?? string.Empty, StringComparison.Ordinal))
        {
            beforeParts.Add($"layer={layerBefore}");
            afterParts.Add($"layer={layerAfter}");
            changes.Add("layer");
        }

        var keys = before.Keys.Concat(after.Keys)
            .Where(key => !PlacementKeys.Contains(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            before.TryGetValue(key, out var valueBefore);
            after.TryGetValue(key, out var valueAfter);
            if (!string.Equals(valueBefore, valueAfter, StringComparison.Ordinal))
            {
                beforeParts.Add($"{key}={valueBefore}");
                afterParts.Add($"{key}={valueAfter}");
            }
        }

        return beforeParts.Count == 0 ? null : (string.Join("; ", beforeParts), string.Join("; ", afterParts));
    }
}
