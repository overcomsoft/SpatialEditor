using SpatialEditor.Cad;

namespace SpatialEditor.Cad.Tests;

public class UnitTest1
{
    [Fact]
    public void SmartLayoutContainsImportableLayers()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        Assert.True(File.Exists(path), $"DXF file not found: {path}");

        var results = new DxfBlockImporter().Import(path);
        var instances = new DxfBlockImporter().ImportBlockInstances(path);

        Assert.NotEmpty(results);
        Assert.NotEmpty(instances);
        Assert.All(instances, instance => Assert.True(instance.ActualGeometry.NumGeometries > 0));
        Assert.All(results, result => Assert.Equal("dxf_layer", result.FeatureType));
        Assert.Equal(results.Count, results.Select(result => result.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(results, result =>
        {
            Assert.True(result.ActualGeometry.NumGeometries > 0);
            Assert.True(result.OuterGeometry.IsValid);
            Assert.Equal(5186, result.ActualGeometry.SRID);
        });
    }

    [Fact]
    public void EquipmentImportYieldsOnlyEquipmentBlockObjects()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var importer = new DxfBlockImporter();

        var objects = importer.ImportEquipmentBlockObjects(path);

        Assert.Equal(126, objects.Count);
        Assert.All(objects, o =>
        {
            Assert.Equal("Equipment", o.Layer);
            Assert.True(o.Attributes.ContainsKey("block_handle"));
            Assert.True(o.Attributes.ContainsKey("block_insert_x") && o.Attributes.ContainsKey("block_insert_y"));
            Assert.False(o.OuterGeometry.IsEmpty);
            Assert.Equal(5186, o.ActualGeometry.SRID);
        });
    }

    [Fact]
    public void FallbackOuterGeometryIsReportedWithAReason()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var results = new DxfBlockImporter().Import(path);
        var instances = new DxfBlockImporter().ImportBlockInstances(path);

        // SmartLayout.dxf is known to contain layers/blocks whose linework does not
        // polygonize into a closed region, so the importer falls back to an envelope.
        // Guard that this fallback is always explained, never silent.
        Assert.Contains(instances, instance => instance.OuterGeometryIsFallback);
        Assert.All(instances.Where(instance => instance.OuterGeometryIsFallback), instance =>
            Assert.True(instance.Attributes.TryGetValue("outer_geom_fallback_reason", out var reason) && !string.IsNullOrEmpty(reason)));

        Assert.Contains(results, result => result.OuterGeometryIsFallback);
        Assert.All(results.Where(result => result.OuterGeometryIsFallback), result =>
            Assert.True(result.Attributes.TryGetValue("outer_geom_fallback_reason", out var reason) && !string.IsNullOrEmpty(reason)));
    }

    [Fact]
    public void BlockInstancesAlwaysCarryPlacementAttributes()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data", "SmartLayout.dxf"));
        var instances = new DxfBlockImporter().ImportBlockInstances(path);
        var equipmentBoxes = new DxfBlockImporter().ImportEquipmentBoundingBoxes(path);

        Assert.NotEmpty(instances);
        Assert.All(instances, instance =>
        {
            Assert.True(instance.Attributes.TryGetValue("block_name", out var name) && !string.IsNullOrEmpty(name));
            Assert.True(instance.Attributes.TryGetValue("block_handle", out var handle) && !string.IsNullOrEmpty(handle));
            Assert.True(instance.Attributes.TryGetValue("block_insert_x", out var x) && double.TryParse(x, out _));
            Assert.True(instance.Attributes.TryGetValue("block_insert_y", out var y) && double.TryParse(y, out _));
            Assert.True(instance.Attributes.TryGetValue("block_rotation_deg", out var rotation) && double.TryParse(rotation, out _));
            Assert.True(instance.Attributes.TryGetValue("block_scale_x", out var scaleX) && double.TryParse(scaleX, out _));
            Assert.True(instance.Attributes.TryGetValue("block_scale_y", out var scaleY) && double.TryParse(scaleY, out _));
        });

        Assert.All(equipmentBoxes, box =>
        {
            Assert.True(box.Attributes.TryGetValue("block_name", out var name) && !string.IsNullOrEmpty(name));
            Assert.True(box.Attributes.TryGetValue("block_handle", out var handle) && !string.IsNullOrEmpty(handle));
            Assert.True(box.Attributes.TryGetValue("block_insert_x", out var x) && double.TryParse(x, out _));
            Assert.True(box.Attributes.TryGetValue("block_rotation_deg", out var rotation) && double.TryParse(rotation, out _));
        });
    }
}
