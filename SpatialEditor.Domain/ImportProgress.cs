namespace SpatialEditor.Domain;

public sealed record ImportProgress(string Stage, int Completed, int Total);
