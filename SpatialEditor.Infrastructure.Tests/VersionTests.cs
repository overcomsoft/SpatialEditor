using NetTopologySuite;
using NetTopologySuite.Geometries;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.Infrastructure.Tests;

/// <summary>Runs only when SPATIAL_TEST_CONNECTION points at an empty PostgreSQL/PostGIS test database.</summary>
public sealed class FactIfDatabaseAttribute : FactAttribute
{
    public const string Variable = "SPATIAL_TEST_CONNECTION";

    public FactIfDatabaseAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(Variable)))
        {
            Skip = $"Set {Variable} to an empty PostGIS test database to run this test.";
        }
    }
}

public class VersionDifferTests
{
    private static readonly GeometryFactory Factory = NtsGeometryServices.Instance.CreateGeometryFactory(5186);

    private static VersionBlockItem Block(Guid uid, string name, double x, double y, double rot = 0, string layer = "Equipment",
        double scale = 1, Dictionary<string, string?>? attributes = null)
    {
        var square = Factory.CreatePolygon(new[]
        {
            new Coordinate(x, y), new Coordinate(x + 10, y), new Coordinate(x + 10, y + 10), new Coordinate(x, y + 10), new Coordinate(x, y)
        });
        return new VersionBlockItem(uid, name, layer, "f.dxf", x, y, rot, scale, scale, attributes ?? new Dictionary<string, string?>(), square, square);
    }

    private static VersionDiff Compare(IEnumerable<VersionBlockItem> a, IEnumerable<VersionBlockItem> b) =>
        VersionDiffer.Compare(a.ToList(), b.ToList(), Array.Empty<VersionFeatureItem>(), Array.Empty<VersionFeatureItem>());

    [Fact]
    public void IdenticalStatesHaveNoDifferences()
    {
        var uid = Guid.NewGuid();
        Assert.True(Compare(new[] { Block(uid, "A", 0, 0) }, new[] { Block(uid, "A", 0, 0) }).IsEmpty);
    }

    [Fact]
    public void AddedAndRemovedObjectsAreFoundByUid()
    {
        var kept = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var added = Guid.NewGuid();
        var diff = Compare(new[] { Block(kept, "A", 0, 0), Block(removed, "B", 50, 0) }, new[] { Block(kept, "A", 0, 0), Block(added, "C", 100, 0) });

        Assert.Equal(1, diff.Count(DiffKind.Added));
        Assert.Equal(1, diff.Count(DiffKind.Removed));
        Assert.Equal(2, diff.Entries.Count);
    }

    [Fact]
    public void MoveAndRotationAreReportedSeparately()
    {
        var uid = Guid.NewGuid();
        var diff = Compare(new[] { Block(uid, "A", 0, 0, 0) }, new[] { Block(uid, "A", 5, 0, 90) });

        Assert.Equal(1, diff.Count(DiffKind.Moved));
        Assert.Equal(1, diff.Count(DiffKind.Rotated));
        Assert.Equal(0, diff.Count(DiffKind.Added));
    }

    [Fact]
    public void TinyPositionNoiseAndAngleWrapAroundAreNotChanges()
    {
        var uid = Guid.NewGuid();
        var diff = Compare(new[] { Block(uid, "A", 0, 0, 359.99999999) }, new[] { Block(uid, "A", 0.00001, 0, 0) });
        Assert.True(diff.IsEmpty);
        Assert.Equal(20, VersionDiffer.AngleDifference(350, 10), 6);
    }

    [Fact]
    public void ChangedBlockNameLayerAndAttributesAreReported()
    {
        var uid = Guid.NewGuid();
        var a = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["owner"] = "kim", ["block_insert_x"] = "0" });
        var b = Block(uid, "B", 0, 0, layer: "Other", attributes: new Dictionary<string, string?> { ["owner"] = "lee", ["block_insert_x"] = "0" });
        var diff = Compare(new[] { a }, new[] { b });

        Assert.Equal(1, diff.Count(DiffKind.Reshaped));
        Assert.Equal(1, diff.Count(DiffKind.AttributesChanged));
    }

    [Fact]
    public void PlacementAttributesAloneAreNotAttributeChanges()
    {
        var uid = Guid.NewGuid();
        var a = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["block_insert_x"] = "0", ["block_rotation_deg"] = "0" });
        var b = Block(uid, "A", 0, 0, attributes: new Dictionary<string, string?> { ["block_insert_x"] = "5", ["block_rotation_deg"] = "90" });
        Assert.True(Compare(new[] { a }, new[] { b }).IsEmpty);
    }
}

public class VersionRepositoryTests
{
    private static readonly GeometryFactory Factory = NtsGeometryServices.Instance.CreateGeometryFactory(5186);

    private static string Connection => Environment.GetEnvironmentVariable(FactIfDatabaseAttribute.Variable)!;

    private static Geometry Square(double x, double y) => Factory.CreatePolygon(new[]
    {
        new Coordinate(x, y), new Coordinate(x + 10, y), new Coordinate(x + 10, y + 10), new Coordinate(x, y + 10), new Coordinate(x, y)
    });

    private static Dictionary<string, string?> Attributes(double x, double y) => new()
    {
        ["block_name"] = "EQ", ["block_insert_x"] = x.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ["block_insert_y"] = y.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
        ["block_rotation_deg"] = "0", ["block_scale_x"] = "1", ["block_scale_y"] = "1"
    };

    private static async Task ResetAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(Connection);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand("""
            DROP TABLE IF EXISTS layout_version_blocks, layout_version_features, layout_versions, change_log,
                spatial_block_instances, spatial_features, block_definitions, layouts CASCADE;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    [FactIfDatabase]
    public async Task VersionLifecycle_SaveCompareRestoreBranchBaselineDelete()
    {
        await ResetAsync();
        var context = new ChangeContext(1, "tester", Guid.NewGuid());
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();

        // Two pieces of equipment on layer "Equipment".
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[]
            {
                ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)),
                ("EQ", "Equipment", Square(100, 0), Square(100, 0), Attributes(100, 0))
            }, "test.dxf", changeContext: context);

        // The first save creates version 1; saving again without changes is refused.
        var v1 = await versions.CreateVersionAsync(1, "첫 배치", "처음", VersionKind.Manual, context);
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.VersionNo);
        Assert.Equal(2, v1.BlockCount);
        Assert.Null(await versions.CreateVersionAsync(1, "same", null, VersionKind.Manual, context));
        var status = await versions.GetStatusAsync(1);
        Assert.False(status!.IsModified);
        Assert.Equal(1, status.BaseVersionNo);

        // Move one piece of equipment through the normal save path.
        var objects = await repo.FindEditableBlockInstancesAsync();
        var moved = objects.Single(o => o.Geometry.EnvelopeInternal.MinX == 100);
        var shifted = new NetTopologySuite.Geometries.Utilities.AffineTransformation().Translate(25, 0);
        var attributes = new Dictionary<string, string?>(moved.Attributes) { ["block_insert_x"] = "125" };
        await repo.ApplyLayerEditsAsync(
            new[] { (moved.Id, shifted.Transform(moved.Geometry), shifted.Transform(moved.OuterGeometry), moved.UpdatedAt, true, (IReadOnlyDictionary<string, string?>?)attributes) },
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            Array.Empty<(long, DateTime, bool)>(), changeContext: context);

        Assert.True((await versions.GetStatusAsync(1))!.IsModified);

        // Working copy vs v1: exactly one move; the object kept its uid.
        var diff = await versions.DiffAsync(1, v1.Id, null);
        Assert.Equal(1, diff.Count(DiffKind.Moved));
        Assert.Single(diff.Entries);

        var v2 = await versions.CreateVersionAsync(1, "오른쪽으로", null, VersionKind.Manual, context);
        Assert.Equal(2, v2!.VersionNo);
        Assert.Equal(v1.Id, v2.ParentVersionId);
        Assert.Equal(1, (await versions.DiffAsync(1, v1.Id, v2.Id)).Count(DiffKind.Moved));

        // Restore v1: the working copy equals v1 again and the uids are unchanged.
        var uidsBefore = (await versions.GetWorkingBlocksAsync(1)).Select(b => b.ObjectUid).OrderBy(u => u).ToList();
        var safety = await versions.RestoreVersionAsync(1, v1.Id, context);
        Assert.Null(safety); // working copy already equalled v2 (the newest version): nothing extra to save
        Assert.True((await versions.DiffAsync(1, v1.Id, null)).IsEmpty);
        Assert.Equal(uidsBefore, (await versions.GetWorkingBlocksAsync(1)).Select(b => b.ObjectUid).OrderBy(u => u).ToList());
        Assert.False((await versions.GetStatusAsync(1))!.IsModified);

        // Edit after the restore, then restore again: the unsaved state is kept as a pre-restore version.
        objects = await repo.FindEditableBlockInstancesAsync();
        var again = objects.First();
        await repo.ApplyLayerEditsAsync(
            new[] { (again.Id, shifted.Transform(again.Geometry), shifted.Transform(again.OuterGeometry), again.UpdatedAt, true,
                (IReadOnlyDictionary<string, string?>?)new Dictionary<string, string?>(again.Attributes) { ["block_insert_x"] = "25" }) },
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            Array.Empty<(long, DateTime, bool)>(), changeContext: context);
        var pre = await versions.RestoreVersionAsync(1, v2.Id, context);
        Assert.NotNull(pre);
        Assert.Equal(VersionKind.PreRestore, pre!.Kind);
        Assert.True((await versions.DiffAsync(1, v2.Id, null)).IsEmpty);

        // Branch: a second layout starts as a copy of v1 and is edited independently.
        var branchId = await versions.CreateLayoutFromVersionAsync(v1.Id, "B안", null, context);
        Assert.NotEqual(1, branchId);
        await using var branchRepo = new PostGisFeatureRepository(Connection, branchId);
        var branchObjects = await branchRepo.FindEditableBlockInstancesAsync();
        Assert.Equal(2, branchObjects.Count);
        Assert.True((await versions.DiffAsync(branchId, v1.Id, null)).IsEmpty);
        await branchRepo.ApplyLayerEditsAsync(
            Array.Empty<(long, Geometry, Geometry, DateTime, bool, IReadOnlyDictionary<string, string?>?)>(),
            Array.Empty<(EditableLayerObject, Geometry, Geometry)>(),
            new[] { (branchObjects[0].Id, branchObjects[0].UpdatedAt, true) }, changeContext: context);
        Assert.Equal(2, (await repo.FindEditableBlockInstancesAsync()).Count);   // layout 1 untouched
        Assert.Single(await branchRepo.FindEditableBlockInstancesAsync());
        Assert.Equal(1, (await versions.DiffAsync(branchId, v1.Id, null)).Count(DiffKind.Removed));

        // Baseline protection, moving the baseline, and delete with re-parenting.
        await versions.SetStatusAsync(v1.Id, VersionStatus.Baseline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => versions.DeleteVersionAsync(v1.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => versions.UpdateVersionAsync(v1.Id, "x", null));
        await versions.SetStatusAsync(v2.Id, VersionStatus.Baseline);
        Assert.Equal(VersionStatus.Released, (await versions.GetVersionAsync(v1.Id))!.Status);
        await versions.DeleteVersionAsync(v1.Id);
        Assert.Null(await versions.GetVersionAsync(v1.Id));
        Assert.Null((await versions.GetVersionAsync(v2.Id))!.ParentVersionId);

        // Deleting a layout removes its working copy and versions; the last layout is protected.
        Assert.True(await versions.DeleteLayoutAsync(branchId));
        Assert.False(await versions.DeleteLayoutAsync(1));
    }

    [FactIfDatabase]
    public async Task ExistingDatabaseWithoutLayoutsIsMigratedAndGetsUniqueUids()
    {
        await ResetAsync();
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[]
            {
                ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)),
                ("EQ", "Equipment", Square(50, 0), Square(50, 0), Attributes(50, 0))
            }, "old.dxf");

        // Turn it back into the pre-versioning schema: no layouts, no layout_id / object_uid columns.
        await using (var connection = new Npgsql.NpgsqlConnection(Connection))
        {
            await connection.OpenAsync();
            await using var command = new Npgsql.NpgsqlCommand("""
                DROP INDEX IF EXISTS uq_spatial_block_instances_uid, uq_spatial_features_uid;
                ALTER TABLE spatial_block_instances DROP COLUMN layout_id, DROP COLUMN object_uid;
                ALTER TABLE spatial_features DROP COLUMN layout_id, DROP COLUMN object_uid;
                ALTER TABLE change_log DROP COLUMN layout_id;
                DROP TABLE layouts CASCADE;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        // Running the schema step again (what the app does at start) upgrades it in place.
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();

        var blocks = await versions.GetWorkingBlocksAsync(1);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(2, blocks.Select(b => b.ObjectUid).Distinct().Count());
        Assert.Single(await versions.ListLayoutsAsync());
        Assert.Equal(2, (await repo.FindEditableBlockInstancesAsync()).Count);
    }

    [FactIfDatabase]
    public async Task EditsAndLayerOperationsStayInsideTheirLayout()
    {
        await ResetAsync();
        var context = new ChangeContext(1, "tester", Guid.NewGuid());
        await using var repo = new PostGisFeatureRepository(Connection);
        await repo.EnsureSchemaAsync();
        await using var versions = new VersionRepository(Connection);
        await versions.EnsureSchemaAsync();
        await repo.ImportAsync(
            Array.Empty<(string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>, string)>(),
            new (string, string?, Geometry, Geometry, IReadOnlyDictionary<string, string?>)[] { ("EQ", "Equipment", Square(0, 0), Square(0, 0), Attributes(0, 0)) },
            "t.dxf", changeContext: context);
        var v = await versions.CreateVersionAsync(1, "v", null, VersionKind.Manual, context);
        var other = await versions.CreateLayoutFromVersionAsync(v!.Id, "Other", null, context);

        await using var otherRepo = new PostGisFeatureRepository(Connection, other);
        Assert.Equal("Equipment", (await otherRepo.FindImportedLayersAsync()).Single().LayerName);

        // Delete and rename on one layout never touch the other.
        Assert.True(await otherRepo.LayerExistsAsync("Equipment"));
        await otherRepo.RenameLayerAsync("Equipment", "Renamed", changeContext: context);
        Assert.Equal("Renamed", (await otherRepo.FindImportedLayersAsync()).Single().LayerName);
        Assert.Equal("Equipment", (await repo.FindImportedLayersAsync()).Single().LayerName);
        await repo.DeleteLayerAsync("Equipment", changeContext: context);
        Assert.Empty(await repo.FindImportedLayersAsync());
        Assert.Single(await otherRepo.FindImportedLayersAsync());

        // Changes are logged per layout, so another layout's session is not told about them.
        await using var feed = new ChangeFeedRepository(Connection);
        var changes = await feed.GetChangesSinceAsync(0, Guid.NewGuid(), null, layoutId: other);
        Assert.Contains(changes.Summaries, summary => summary.LayerName == "Renamed");
        var firstLayout = await feed.GetChangesSinceAsync(0, Guid.NewGuid(), null, layoutId: 1);
        Assert.DoesNotContain(firstLayout.Summaries, summary => summary.LayerName == "Renamed");
    }
}
