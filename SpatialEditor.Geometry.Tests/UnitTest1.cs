using NetTopologySuite.Geometries;
using SpatialEditor.Geometry;

namespace SpatialEditor.Geometry.Tests;

public class UnitTest1
{
    [Fact]
    public void PolygonHasConfiguredSridAndIsValid()
    {
        var service = new GeometryService();
        var polygon = service.CreatePolygon(new[]
        {
            new Coordinate(0, 0), new Coordinate(10, 0),
            new Coordinate(10, 10), new Coordinate(0, 10),
            new Coordinate(0, 0)
        });

        Assert.Equal(5186, polygon.SRID);
        Assert.True(polygon.IsValid);
    }

    [Fact]
    public void PolylinePolygonizerCreatesOnePolygonFromSegmentedRing()
    {
        var service = new GeometryService();
        var polygonizer = new PolylinePolygonizer();
        var lines = new[]
        {
            service.CreateLine(new[] { new Coordinate(0, 0), new Coordinate(10, 0) }),
            service.CreateLine(new[] { new Coordinate(10, 0), new Coordinate(10, 10) }),
            service.CreateLine(new[] { new Coordinate(10, 10), new Coordinate(0, 10) }),
            service.CreateLine(new[] { new Coordinate(0, 10), new Coordinate(0, 0) })
        };

        var result = polygonizer.CreateOuterGeometry(lines);

        Assert.Equal("Polygon", result.GeometryType);
        Assert.Equal(100, result.Area);
        Assert.True(result.IsValid);
    }
}