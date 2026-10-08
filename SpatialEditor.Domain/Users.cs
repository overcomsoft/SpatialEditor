namespace SpatialEditor.Domain;

/// <summary>Permission keys checked by the app. Roles are sets of these keys.</summary>
public static class Permissions
{
    public const string UserManage = "user.manage";
    public const string LibraryView = "library.view";
    public const string LibraryCreate = "library.create";
    public const string LibraryEdit = "library.edit";
    public const string LibraryDelete = "library.delete";
    public const string LibraryPublish = "library.publish";
    public const string DrawingView = "drawing.view";
    public const string DrawingEdit = "drawing.edit";
    public const string DrawingDelete = "drawing.delete";
    public const string DrawingExport = "drawing.export";
    public const string ImportDxf = "import.dxf";
    public const string AuditView = "audit.view";

    /// <summary>Every key with a display label and group, in the order the role editor lists them.</summary>
    public static readonly IReadOnlyList<(string Key, string Label, string Group)> All = new[]
    {
        (UserManage, "Manage users and roles", "Users"),
        (LibraryView, "View the block library", "Library"),
        (LibraryCreate, "Register library blocks", "Library"),
        (LibraryEdit, "Edit library blocks", "Library"),
        (LibraryDelete, "Delete library blocks", "Library"),
        (LibraryPublish, "Publish library versions", "Library"),
        (DrawingView, "View drawings", "Drawings"),
        (DrawingEdit, "Edit drawings (edit mode, save)", "Drawings"),
        (DrawingDelete, "Delete drawing objects", "Drawings"),
        (DrawingExport, "Export drawings", "Drawings"),
        (ImportDxf, "Import / delete DXF data", "Data"),
        (AuditView, "View the audit log", "Data")
    };

    /// <summary>Built-in roles created on first run. Administrators may change their permissions later.</summary>
    public static readonly IReadOnlyList<(string Name, string Description, string[] Keys)> DefaultRoles = new[]
    {
        ("Admin", "Full access", All.Select(item => item.Key).ToArray()),
        ("LibraryManager", "Manages the block library", new[]
        {
            LibraryView, LibraryCreate, LibraryEdit, LibraryDelete, LibraryPublish,
            DrawingView, DrawingExport, ImportDxf
        }),
        ("Designer", "Creates and edits drawings", new[]
        {
            LibraryView, DrawingView, DrawingEdit, DrawingDelete, DrawingExport, ImportDxf
        }),
        ("Viewer", "Read-only access", new[] { LibraryView, DrawingView })
    };
}

public sealed record AppUser(
    long Id,
    string LoginId,
    string DisplayName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LockedUntil,
    DateTime? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlySet<string> Granted)
{
    public bool Has(string permission) => Granted.Contains(permission);
}

public sealed record AppRole(
    long Id,
    string Name,
    string? Description,
    bool IsSystem,
    IReadOnlyList<string> PermissionKeys,
    int UserCount);

public sealed record AuditEntry(
    long Id,
    DateTime At,
    string? UserName,
    string Action,
    string? EntityType,
    string? EntityId,
    string? Summary);

/// <summary>Outcome of a login attempt: either a user or a message to show.</summary>
public sealed record AuthResult(AppUser? User, string? Error);
