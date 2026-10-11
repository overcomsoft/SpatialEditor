using NetTopologySuite.Geometries;
using netDxf;
using netDxf.Entities;

namespace SpatialEditor.Cad.Tests;

public class DxfBlockExporterTests
{
    [Fact]
    public void ExportWritesBlocksWithNameRotationAndPositionAttributes()
    {
        var factory = new GeometryFactory();
        // Local 10x4 rectangle, placed at (100, 200) rotated 90° CCW => world rect spans x 96..100, y 200..210.
        var world = factory.CreatePolygon(new[]
        {
            new Coordinate(100, 200), new Coordinate(100, 210), new Coordinate(96, 210),
            new Coordinate(96, 200), new Coordinate(100, 200)
        });
        var path = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.dxf");
        try
        {
            var count = new DxfBlockExporter().Export(path, new[]
            {
                new CadExportInstance("EQ-A", "Equipment", 100, 200, 90, 1, 1, world)
            });
            Assert.Equal(1, count);

            var doc = DxfDocument.Load(path);
            var insert = Assert.Single(doc.Entities.Inserts);
            Assert.Equal("EQ-A", insert.Block.Name);
            Assert.Equal(90, insert.Rotation, 6);
            Assert.Equal(100, insert.Position.X, 6);
            Assert.Equal(200, insert.Position.Y, 6);
            string Value(string tag) => insert.Attributes.Single(a => a.Tag == tag).Value;
            Assert.Equal("EQ-A", Value("NAME"));
            Assert.Equal("90", Value("ROTATION"));
            Assert.Equal("100", Value("POS_X"));
            Assert.Equal("200", Value("POS_Y"));

            // Re-importing yields the original world geometry back.
            var back = new DxfBlockImporter().ImportEquipmentBlockObjects(path);
            var env = Assert.Single(back).ActualGeometry.EnvelopeInternal;
            Assert.Equal(96, env.MinX, 4);
            Assert.Equal(100, env.MaxX, 4);
            Assert.Equal(200, env.MinY, 4);
            Assert.Equal(210, env.MaxY, 4);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
