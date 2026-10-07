using NetTopologySuite.Geometries;

namespace SpatialEditor.Cad;

public sealed record CadBlockInstanceResult(
    string BlockName,
    string? Layer,
    Geometry ActualGeometry,
    Geometry OuterGeometry,
    IReadOnlyDictionary<string, string?> Attributes,
    bool OuterGeometryIsFallback = false);