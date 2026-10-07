namespace SpatialEditor.Domain;

public sealed record ImportLayerCount(
    string LayerName,
    long EntityCount,
    long BlockInstanceCount);