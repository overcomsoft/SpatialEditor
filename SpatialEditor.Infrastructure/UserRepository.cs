using Npgsql;
using SpatialEditor.Domain;

namespace SpatialEditor.Infrastructure;

/// <summary>
/// Application users, roles, permissions and the audit log, stored in the same PostgreSQL
/// database. Rule violations (last administrator, duplicate names, weak passwords...) are raised
/// as <see cref="InvalidOperationException"/> with a message that is safe to show to the user.
/// </summary>
public sealed class UserRepository : IAsyncDisposable
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private readonly NpgsqlDataSource dataSource;

    public UserRepository(string connectionString)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS app_users (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                login_id VARCHAR(64) NOT NULL,
                display_name VARCHAR(128) NOT NULL,
                password_hash TEXT NOT NULL,
                must_change_password BOOLEAN NOT NULL DEFAULT FALSE,
                is_active BOOLEAN NOT NULL DEFAULT TRUE,
                failed_attempts INT NOT NULL DEFAULT 0,
                locked_until TIMESTAMPTZ,
                last_login_at TIMESTAMPTZ,
                created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                created_by BIGINT,
                updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_app_users_login ON app_users (lower(login_id));

            CREATE TABLE IF NOT EXISTS roles (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name VARCHAR(64) NOT NULL UNIQUE,
                description TEXT,
                is_system BOOLEAN NOT NULL DEFAULT FALSE
            );

            CREATE TABLE IF NOT EXISTS role_permissions (
                role_id BIGINT NOT NULL REFERENCES roles (id) ON DELETE CASCADE,
                permission_key VARCHAR(64) NOT NULL,
                PRIMARY KEY (role_id, permission_key)
            );

            CREATE TABLE IF NOT EXISTS user_roles (
                user_id BIGINT NOT NULL REFERENCES app_users (id) ON DELETE CASCADE,
                role_id BIGINT NOT NULL REFERENCES roles (id),
                PRIMARY KEY (user_id, role_id)
            );

            -- No foreign key on user_id and the name is copied, so history survives user deletion.
            CREATE TABLE IF NOT EXISTS audit_log (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
                user_id BIGINT,
                user_name VARCHAR(128),
                action VARCHAR(64) NOT NULL,
                entity_type VARCHAR(64),
                entity_id VARCHAR(128),
                summary TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_audit_log_at ON audit_log (at DESC);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Built-in roles are created once; later permission changes made by administrators stick.
        foreach (var (name, description, keys) in Permissions.DefaultRoles)
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO roles (name, description, is_system) VALUES ($1, $2, TRUE) ON CONFLICT (name) DO NOTHING RETURNING id;",
                connection);
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(description);
            if (await insert.ExecuteScalarAsync(cancellationToken) is long roleId)
            {
                await InsertPermissionsAsync(connection, null, roleId, keys, cancellationToken);
            }
        }
    }

    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM app_users);");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // ---------------------------------------------------------------- login

    /// <summary>Creates the first administrator; only allowed while there are no users at all.</summary>
    public async Task<AppUser> CreateInitialAdminAsync(
        string loginId, string displayName, string password, CancellationToken cancellationToken = default)
    {
        ValidateNewUser(loginId, displayName, password);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var lockCommand = new NpgsqlCommand("LOCK TABLE app_users IN EXCLUSIVE MODE;", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM app_users);", connection, transaction))
        {
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("An administrator already exists. Log in instead.");
            }
        }

        var userId = await InsertUserAsync(connection, transaction, loginId.Trim(), displayName.Trim(), password, false, null, cancellationToken);
        await using (var assign = new NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role_id) SELECT $1, id FROM roles WHERE name = 'Admin';", connection, transaction))
        {
            assign.Parameters.AddWithValue(userId);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, user, "user.create", "user", user.Id.ToString(), $"initial administrator '{user.LoginId}'", cancellationToken);
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    public async Task<AuthResult> AuthenticateAsync(string loginId, string password, CancellationToken cancellationToken = default)
    {
        const string invalid = "Invalid ID or password.";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        long? userId = null;
        string? hash = null;
        bool isActive = false;
        DateTime? lockedUntil = null;
        await using (var find = new NpgsqlCommand(
            "SELECT id, password_hash, is_active, locked_until FROM app_users WHERE lower(login_id) = lower($1) FOR UPDATE;",
            connection, transaction))
        {
            find.Parameters.AddWithValue(loginId.Trim());
            await using var reader = await find.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                userId = reader.GetInt64(0);
                hash = reader.GetString(1);
                isActive = reader.GetBoolean(2);
                lockedUntil = reader.IsDBNull(3) ? null : reader.GetDateTime(3);
            }
        }

        if (userId is null || hash is null)
        {
            await WriteAuditAsync(connection, transaction, null, "login.failed", "user", null, $"unknown id '{loginId.Trim()}'", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, invalid);
        }

        if (!isActive)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, "This account is disabled. Contact an administrator.");
        }

        if (lockedUntil is { } until && until > DateTime.UtcNow)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, $"This account is locked until {until.ToLocalTime():HH:mm}. Try again later or ask an administrator to unlock it.");
        }

        if (!PasswordHasher.Verify(password, hash))
        {
            await using var fail = new NpgsqlCommand("""
                UPDATE app_users SET
                    failed_attempts = CASE WHEN failed_attempts + 1 >= $2 THEN 0 ELSE failed_attempts + 1 END,
                    locked_until = CASE WHEN failed_attempts + 1 >= $2 THEN CURRENT_TIMESTAMP + $3 * INTERVAL '1 minute' ELSE locked_until END
                WHERE id = $1
                RETURNING locked_until;
                """, connection, transaction);
            fail.Parameters.AddWithValue(userId.Value);
            fail.Parameters.AddWithValue(MaxFailedAttempts);
            fail.Parameters.AddWithValue((int)LockoutDuration.TotalMinutes);
            var newLock = await fail.ExecuteScalarAsync(cancellationToken);
            await WriteAuditAsync(connection, transaction, null, "login.failed", "user", userId.Value.ToString(), $"wrong password for '{loginId.Trim()}'", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return newLock is DateTime lockTime && lockTime > DateTime.UtcNow
                ? new AuthResult(null, $"Too many failed attempts. The account is locked for {(int)LockoutDuration.TotalMinutes} minutes.")
                : new AuthResult(null, invalid);
        }

        await using (var ok = new NpgsqlCommand(
            "UPDATE app_users SET failed_attempts = 0, locked_until = NULL, last_login_at = CURRENT_TIMESTAMP WHERE id = $1;",
            connection, transaction))
        {
            ok.Parameters.AddWithValue(userId.Value);
            await ok.ExecuteNonQueryAsync(cancellationToken);
        }

        var user = (await LoadUsersAsync(connection, transaction, userId.Value, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, user, "login", "user", user.Id.ToString(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthResult(user, null);
    }

    // ---------------------------------------------------------------- users

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadUsersAsync(connection, null, null, cancellationToken);
    }

    public async Task<AppUser?> GetUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await LoadUsersAsync(connection, null, userId, cancellationToken)).SingleOrDefault();
    }

    public async Task<AppUser> CreateUserAsync(
        AppUser actor, string loginId, string displayName, string password, bool mustChangePassword,
        IReadOnlyCollection<long> roleIds, CancellationToken cancellationToken = default)
    {
        ValidateNewUser(loginId, displayName, password);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long userId;
        try
        {
            userId = await InsertUserAsync(connection, transaction, loginId.Trim(), displayName.Trim(), password, mustChangePassword, actor.Id, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"The ID '{loginId.Trim()}' is already in use.");
        }

        await ReplaceRolesAsync(connection, transaction, userId, roleIds, cancellationToken);
        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, actor, "user.create", "user", userId.ToString(),
            $"'{user.LoginId}' roles=[{string.Join(", ", user.Roles)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    public async Task UpdateUserAsync(
        AppUser actor, long userId, string displayName, bool isActive,
        IReadOnlyCollection<long> roleIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Name is required.");
        }

        if (!isActive && userId == actor.Id)
        {
            throw new InvalidOperationException("You cannot deactivate your own account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var update = new NpgsqlCommand(
            "UPDATE app_users SET display_name = $2, is_active = $3, updated_at = CURRENT_TIMESTAMP WHERE id = $1;",
            connection, transaction))
        {
            update.Parameters.AddWithValue(userId);
            update.Parameters.AddWithValue(displayName.Trim());
            update.Parameters.AddWithValue(isActive);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("The user no longer exists.");
            }
        }

        await ReplaceRolesAsync(connection, transaction, userId, roleIds, cancellationToken);
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, actor, "user.update", "user", userId.ToString(),
            $"'{user.LoginId}' active={isActive} roles=[{string.Join(", ", user.Roles)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetPasswordAsync(
        AppUser actor, long userId, string newPassword, bool mustChangePassword, CancellationToken cancellationToken = default)
    {
        if (PasswordHasher.ValidatePolicy(newPassword) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("""
            UPDATE app_users SET password_hash = $2, must_change_password = $3, failed_attempts = 0, locked_until = NULL,
                   updated_at = CURRENT_TIMESTAMP
            WHERE id = $1;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            command.Parameters.AddWithValue(PasswordHasher.Hash(newPassword));
            command.Parameters.AddWithValue(mustChangePassword);
            if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("The user no longer exists.");
            }
        }

        await WriteAuditAsync(connection, transaction, actor, "user.password-reset", "user", userId.ToString(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ChangeOwnPasswordAsync(
        AppUser user, string oldPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        if (PasswordHasher.ValidatePolicy(newPassword) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? hash;
        await using (var read = new NpgsqlCommand("SELECT password_hash FROM app_users WHERE id = $1 FOR UPDATE;", connection, transaction))
        {
            read.Parameters.AddWithValue(user.Id);
            hash = await read.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (hash is null || !PasswordHasher.Verify(oldPassword, hash))
        {
            throw new InvalidOperationException("The current password is incorrect.");
        }

        await using (var update = new NpgsqlCommand(
            "UPDATE app_users SET password_hash = $2, must_change_password = FALSE, updated_at = CURRENT_TIMESTAMP WHERE id = $1;",
            connection, transaction))
        {
            update.Parameters.AddWithValue(user.Id);
            update.Parameters.AddWithValue(PasswordHasher.Hash(newPassword));
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(connection, transaction, user, "user.password-change", "user", user.Id.ToString(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UnlockUserAsync(AppUser actor, long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(
            "UPDATE app_users SET failed_attempts = 0, locked_until = NULL WHERE id = $1;", connection, transaction))
        {
            command.Parameters.AddWithValue(userId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(connection, transaction, actor, "user.unlock", "user", userId.ToString(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Permanently deletes a user. Only accounts without any recorded activity can be removed;
    /// everyone else must be deactivated so the audit history keeps its author.
    /// </summary>
    public async Task DeleteUserAsync(AppUser actor, long userId, CancellationToken cancellationToken = default)
    {
        if (userId == actor.Id)
        {
            throw new InvalidOperationException("You cannot delete your own account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var history = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM audit_log WHERE user_id = $1);", connection, transaction))
        {
            history.Parameters.AddWithValue(userId);
            if ((bool)(await history.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("This user has activity history, so it cannot be deleted. Deactivate the account instead.");
            }
        }

        string? loginId;
        await using (var delete = new NpgsqlCommand("DELETE FROM app_users WHERE id = $1 RETURNING login_id;", connection, transaction))
        {
            delete.Parameters.AddWithValue(userId);
            loginId = await delete.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (loginId is null)
        {
            throw new InvalidOperationException("The user no longer exists.");
        }

        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        await WriteAuditAsync(connection, transaction, actor, "user.delete", "user", userId.ToString(), $"'{loginId}'", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- roles

    public async Task<IReadOnlyList<AppRole>> ListRolesAsync(CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT r.id, r.name, r.description, r.is_system,
                   COALESCE((SELECT array_agg(rp.permission_key ORDER BY rp.permission_key) FROM role_permissions rp WHERE rp.role_id = r.id), '{}'::text[]),
                   (SELECT COUNT(*) FROM user_roles ur WHERE ur.role_id = r.id)
            FROM roles r
            ORDER BY r.is_system DESC, r.name;
            """;
        await using var command = dataSource.CreateCommand(sql);
        var roles = new List<AppRole>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new AppRole(
                reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3), reader.GetFieldValue<string[]>(4), (int)reader.GetInt64(5)));
        }

        return roles;
    }

    public async Task<long> CreateRoleAsync(
        AppUser actor, string name, string? description, IReadOnlyCollection<string> permissionKeys,
        CancellationToken cancellationToken = default)
    {
        ValidateRoleName(name);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        long roleId;
        try
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO roles (name, description, is_system) VALUES ($1, $2, FALSE) RETURNING id;", connection, transaction);
            insert.Parameters.AddWithValue(name.Trim());
            insert.Parameters.AddWithValue((object?)description?.Trim() ?? DBNull.Value);
            roleId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A role named '{name.Trim()}' already exists.");
        }

        await InsertPermissionsAsync(connection, transaction, roleId, permissionKeys, cancellationToken);
        await WriteAuditAsync(connection, transaction, actor, "role.create", "role", roleId.ToString(),
            $"'{name.Trim()}' permissions=[{string.Join(", ", permissionKeys)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return roleId;
    }

    public async Task UpdateRoleAsync(
        AppUser actor, long roleId, string name, string? description, IReadOnlyCollection<string> permissionKeys,
        CancellationToken cancellationToken = default)
    {
        ValidateRoleName(name);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var update = new NpgsqlCommand(
                "UPDATE roles SET name = $2, description = $3 WHERE id = $1;", connection, transaction);
            update.Parameters.AddWithValue(roleId);
            update.Parameters.AddWithValue(name.Trim());
            update.Parameters.AddWithValue((object?)description?.Trim() ?? DBNull.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                throw new InvalidOperationException("The role no longer exists.");
            }
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"A role named '{name.Trim()}' already exists.");
        }

        await using (var clear = new NpgsqlCommand("DELETE FROM role_permissions WHERE role_id = $1;", connection, transaction))
        {
            clear.Parameters.AddWithValue(roleId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertPermissionsAsync(connection, transaction, roleId, permissionKeys, cancellationToken);
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        await WriteAuditAsync(connection, transaction, actor, "role.update", "role", roleId.ToString(),
            $"'{name.Trim()}' permissions=[{string.Join(", ", permissionKeys)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeleteRoleAsync(AppUser actor, long roleId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        string? name;
        bool isSystem;
        long userCount;
        await using (var read = new NpgsqlCommand(
            "SELECT name, is_system, (SELECT COUNT(*) FROM user_roles WHERE role_id = $1) FROM roles WHERE id = $1;",
            connection, transaction))
        {
            read.Parameters.AddWithValue(roleId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("The role no longer exists.");
            }

            name = reader.GetString(0);
            isSystem = reader.GetBoolean(1);
            userCount = reader.GetInt64(2);
        }

        if (isSystem)
        {
            throw new InvalidOperationException("Built-in roles cannot be deleted.");
        }

        if (userCount > 0)
        {
            throw new InvalidOperationException($"{userCount} user(s) still have this role. Assign them another role first.");
        }

        await using (var delete = new NpgsqlCommand("DELETE FROM roles WHERE id = $1;", connection, transaction))
        {
            delete.Parameters.AddWithValue(roleId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await WriteAuditAsync(connection, transaction, actor, "role.delete", "role", roleId.ToString(), $"'{name}'", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- audit

    /// <summary>Records an event for <paramref name="actor"/>; failures are swallowed so auditing never blocks work.</summary>
    public async Task TryWriteAuditAsync(
        AppUser? actor, string action, string? entityType, string? entityId, string? summary,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await WriteAuditAsync(connection, null, actor, action, entityType, entityId, summary, cancellationToken);
        }
        catch (Exception)
        {
            // Audit is best-effort for app-level events.
        }
    }

    public async Task<IReadOnlyList<AuditEntry>> ListAuditAsync(int limit = 500, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, at, user_name, action, entity_type, entity_id, summary FROM audit_log ORDER BY id DESC LIMIT $1;");
        command.Parameters.AddWithValue(limit);
        var entries = new List<AuditEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new AuditEntry(
                reader.GetInt64(0), reader.GetDateTime(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return entries;
    }

    // ---------------------------------------------------------------- helpers

    private static void ValidateNewUser(string loginId, string displayName, string password)
    {
        if (string.IsNullOrWhiteSpace(loginId) || loginId.Trim().Length > 64)
        {
            throw new InvalidOperationException("ID is required (up to 64 characters).");
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Name is required.");
        }

        if (PasswordHasher.ValidatePolicy(password) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    private static void ValidateRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
        {
            throw new InvalidOperationException("Role name is required (up to 64 characters).");
        }
    }

    private static async Task<long> InsertUserAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string loginId, string displayName,
        string password, bool mustChangePassword, long? createdBy, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO app_users (login_id, display_name, password_hash, must_change_password, created_by)
            VALUES ($1, $2, $3, $4, $5) RETURNING id;
            """, connection, transaction);
        command.Parameters.AddWithValue(loginId);
        command.Parameters.AddWithValue(displayName);
        command.Parameters.AddWithValue(PasswordHasher.Hash(password));
        command.Parameters.AddWithValue(mustChangePassword);
        command.Parameters.AddWithValue((object?)createdBy ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task ReplaceRolesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long userId,
        IReadOnlyCollection<long> roleIds, CancellationToken cancellationToken)
    {
        await using (var clear = new NpgsqlCommand("DELETE FROM user_roles WHERE user_id = $1;", connection, transaction))
        {
            clear.Parameters.AddWithValue(userId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var roleId in roleIds.Distinct())
        {
            await using var insert = new NpgsqlCommand("INSERT INTO user_roles (user_id, role_id) VALUES ($1, $2);", connection, transaction);
            insert.Parameters.AddWithValue(userId);
            insert.Parameters.AddWithValue(roleId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertPermissionsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long roleId,
        IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var known = Permissions.All.Select(item => item.Key).ToHashSet();
        foreach (var key in keys.Distinct().Where(known.Contains))
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO role_permissions (role_id, permission_key) VALUES ($1, $2) ON CONFLICT DO NOTHING;", connection, transaction);
            insert.Parameters.AddWithValue(roleId);
            insert.Parameters.AddWithValue(key);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Rolls the surrounding change back when it would leave nobody able to manage users: at least
    /// one active user must keep the user.manage permission.
    /// </summary>
    private static async Task EnsureManagerRemainsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COUNT(*) FROM app_users u
            WHERE u.is_active AND EXISTS (
                SELECT 1 FROM user_roles ur JOIN role_permissions rp ON rp.role_id = ur.role_id
                WHERE ur.user_id = u.id AND rp.permission_key = $1);
            """, connection, transaction);
        command.Parameters.AddWithValue(Permissions.UserManage);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0)
        {
            throw new InvalidOperationException(
                $"At least one active user must keep the '{Permissions.UserManage}' permission (the last administrator is protected).");
        }
    }

    private static async Task WriteAuditAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, AppUser? actor, string action,
        string? entityType, string? entityId, string? summary, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO audit_log (user_id, user_name, action, entity_type, entity_id, summary)
            VALUES ($1, $2, $3, $4, $5, $6);
            """, connection, transaction);
        command.Parameters.AddWithValue((object?)actor?.Id ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)actor?.DisplayName ?? DBNull.Value);
        command.Parameters.AddWithValue(action);
        command.Parameters.AddWithValue((object?)entityType ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)entityId ?? DBNull.Value);
        command.Parameters.AddWithValue((object?)summary ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<AppUser>> LoadUsersAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, long? onlyUserId, CancellationToken cancellationToken)
    {
        var sql = """
            SELECT u.id, u.login_id, u.display_name, u.is_active, u.must_change_password, u.locked_until, u.last_login_at,
                   COALESCE((SELECT array_agg(r.name ORDER BY r.name) FROM user_roles ur JOIN roles r ON r.id = ur.role_id
                             WHERE ur.user_id = u.id), '{}'::text[]),
                   COALESCE((SELECT array_agg(DISTINCT rp.permission_key) FROM user_roles ur JOIN role_permissions rp ON rp.role_id = ur.role_id
                             WHERE ur.user_id = u.id), '{}'::text[])
            FROM app_users u
            """;
        if (onlyUserId is not null)
        {
            sql += " WHERE u.id = $1";
        }

        sql += " ORDER BY lower(u.login_id);";
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (onlyUserId is { } id)
        {
            command.Parameters.AddWithValue(id);
        }

        var users = new List<AppUser>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new AppUser(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5), reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                reader.GetFieldValue<string[]>(7), reader.GetFieldValue<string[]>(8).ToHashSet()));
        }

        return users;
    }
}
