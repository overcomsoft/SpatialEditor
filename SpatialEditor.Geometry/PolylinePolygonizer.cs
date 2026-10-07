using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace SpatialEditor.Geometry;

public sealed class PolylinePolygonizer
{
    private readonly GeometryFactory factory;

    public PolylinePolygonizer(int srid = GeometryService.DefaultSrid)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
    }

    public IReadOnlyList<Polygon> CreatePolygons(IEnumerable<LineString> polylines)
    {
        var lines = polylines
            .Where(line => !line.IsEmpty && line.NumPoints >= 2)
            .ToArray();
        if (lines.Length == 0)
        {
            return Array.Empty<Polygon>();
        }

        var polygonizer = new Polygonizer();
        polygonizer.Add(factory.CreateMultiLineString(lines));
        return polygonizer.GetPolygons()
            .Cast<Polygon>()
            .Where(polygon => polygon.IsValid && !polygon.IsEmpty)
            .ToArray();
    }

    public NtsGeometry CreateOuterGeometry(IEnumerable<LineString> polylines)
    {
        var polygons = CreatePolygons(polylines);
        return polygons.Count switch
        {
            0 => factory.CreateGeometryCollection(),
            1 => polygons[0],
            _ => factory.CreateMultiPolygon(polygons.ToArray()).Union()
        };
    }

    public NtsGeometry CreateOuterGeometry(IEnumerable<NtsGeometry> geometries)
    {
        var lines = geometries.SelectMany(ExtractLineStrings).ToArray();
        return CreateOuterGeometry(lines);
    }

    private static IEnumerable<LineString> ExtractLineStrings(NtsGeometry geometry)
    {
        switch (geometry)
        {
            case LineString line:
                yield return line;
                break;
            case Polygon polygon:
                yield return polygon.ExteriorRing;
                for (var index = 0; index < polygon.NumInteriorRings; index++)
                {
                    yield return polygon.GetInteriorRingN(index);
                }
                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    foreach (var line in ExtractLineStrings(child))
                    {
                        yield return line;
                    }
                }
                break;
        }
    }
}