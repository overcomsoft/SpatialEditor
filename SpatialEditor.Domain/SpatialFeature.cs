using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

public sealed record SpatialFeature(
    long Id,
    string FeatureType,
    Geometry Geometry,
    IReadOnlyDictionary<string, string?> Attributes);