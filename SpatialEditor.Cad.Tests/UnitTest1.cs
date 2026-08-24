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

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal("dxf_layer", result.FeatureType));
        Assert.Equal(results.Count, results.Select(result => result.Layer).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(results, result =>
        {
            Assert.True(result.ActualGeometry.NumGeometries > 0);
            Assert.True(result.OuterPolygon.IsValid);
            Assert.Equal(5186, result.ActualGeometry.SRID);
        });
    }
}
