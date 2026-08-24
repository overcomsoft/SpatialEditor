using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace SpatialEditor.Geometry;

public sealed class GeometryService
{
    public const int DefaultSrid = 5186;
    private readonly GeometryFactory factory;

    public GeometryService(int srid = DefaultSrid)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
    }

    public Point CreatePoint(double x, double y) => factory.CreatePoint(new Coordinate(x, y));

    public LineString CreateLine(IEnumerable<Coordinate> coordinates)
    {
        var values = coordinates.ToArray();
        if (values.Length < 2)
        {
            throw new ArgumentException("A line requires at least two coordinates.", nameof(coordinates));
        }

        return factory.CreateLineString(values);
    }

    public Polygon CreatePolygon(IEnumerable<Coordinate> coordinates)
    {
        var values = coordinates.ToArray();
        if (values.Length < 4 || !values[0].Equals2D(values[^1]))
        {
            throw new ArgumentException("A polygon requires a closed ring with at least four coordinates.", nameof(coordinates));
        }

        var polygon = factory.CreatePolygon(factory.CreateLinearRing(values));
        if (!polygon.IsValid)
        {
            throw new ArgumentException("The polygon is not valid.", nameof(coordinates));
        }

        return polygon;
    }
}