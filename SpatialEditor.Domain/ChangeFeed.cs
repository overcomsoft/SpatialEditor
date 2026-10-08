namespace SpatialEditor.Domain;

/// <summary>Who is saving: recorded with every change so other sessions can show "who changed what".</summary>
public sealed record ChangeContext(long? UserId, string? UserName, Guid SessionId);

/// <summary>One saved change to an object (or a whole-layer import/delete when <see cref="EntityId"/> is null).</summary>
public sealed record ChangeEntry(string LayerName, string EntityType, long? EntityId, char Operation);

/// <summary>Changes by one user on one layer, summarised for the "data changed" message.</summary>
public sealed record ChangeSummary(string LayerName, string? UserName, int Added, int Updated, int Deleted, bool WholeLayer);

/// <summary>Everything that changed after a sequence number. <see cref="LatestSeq"/> is the new baseline.</summary>
public sealed record ChangeBatch(long LatestSeq, IReadOnlyList<ChangeSummary> Summaries)
{
    public bool HasChanges => Summaries.Count > 0;
}

public static class ChangeOperation
{
    public const char Insert = 'I';
    public const char Update = 'U';
    public const char Delete = 'D';
}

public static class ChangeEntityType
{
    public const string BlockInstance = "block_instance";
    public const string Feature = "feature";
    public const string Import = "import";
}
