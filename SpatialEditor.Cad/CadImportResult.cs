using NetTopologySuite.Geometries;

namespace SpatialEditor.Cad;

public sealed record CadImportResult(
    string BlockName,
    string? Layer,
    Geometry ActualGeometry,
    Polygon OuterPolygon,
    IReadOnlyDictionary<string, string?> Attributes,
    string FeatureType = "dxf_layer",
    int EntityCount = 0);