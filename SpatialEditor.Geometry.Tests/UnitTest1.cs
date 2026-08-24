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
}