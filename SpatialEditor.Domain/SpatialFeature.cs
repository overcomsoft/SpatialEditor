using NetTopologySuite.Geometries;

namespace SpatialEditor.Domain;

public sealed record SpatialFeature(
    long Id,
    string FeatureType,
    Geometry Geometry,
    IReadOnlyDictionary<string, string?> Attributes,
    string? LayerName = null);

public sealed record LayerInfo(
    string LayerName,
    string GeometryTypes,
    long EntityCount,
    long InstanceCount = 0);

public sealed record EditableLayerObject(
    long Id,
    string FeatureType,
    string? BlockName,
    string? SourceFile,
    string? LayerName,
    Geometry Geometry,
    Geometry OuterGeometry,
    IReadOnlyDictionary<string, string?> Attributes,
    DateTime UpdatedAt);