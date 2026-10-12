using Npgsql;
using SpatialEditor.Domain;

// ============================================================================
// UserRepository.cs : 사용자 / 역할 / 권한 / 감사 로그 저장소 (PostgreSQL, Npgsql)
//
// [사용 테이블]
//   app_users        사용자(login_id 는 lower() 고유 인덱스, 비밀번호는 해시만 저장, 실패횟수/잠금시각 포함)
//   roles            역할(is_system=true 는 기본 제공 역할)
//   role_permissions 역할별 권한 키(Permissions 상수)
//   user_roles       사용자-역할 다대다
//   audit_log        감사 로그(user_id 에 FK 없음 + 이름을 복사 저장 -> 사용자를 지워도 이력 유지)
//
// [흐름]
//   앱 시작:  EnsureSchemaAsync(테이블 생성 + 기본 역할 최초 1회 생성)
//             -> HasUsersAsync 가 false 면 CreateInitialAdminAsync(최초 관리자, 테이블 잠금으로 동시 생성 방지)
//             -> 있으면 로그인 화면
//   로그인:   AuthenticateAsync
//             행 잠금(FOR UPDATE) 조회 -> 미존재/비활성/잠금 검사 -> PasswordHasher.Verify
//             -> 실패: 누적 횟수 증가, MaxFailedAttempts(5) 도달 시 LockoutDuration(15분) 잠금
//             -> 성공: 실패 횟수/잠금 초기화, 마지막 로그인 갱신, AppUser(권한 집합 포함) 반환
//   관리:     사용자 생성/수정/비밀번호 초기화/잠금 해제/삭제, 역할 생성/수정/삭제
//             모든 변경은 한 트랜잭션 안에서 본 작업 + 감사 로그 기록을 함께 커밋
//             "사용자 관리 권한을 가진 활성 사용자가 최소 1명" 규칙(EnsureManagerRemainsAsync)을 위반하면 롤백
//   감사:     TryWriteAuditAsync(앱 이벤트, 실패해도 무시) / ListAuditAsync(최신순 조회)
//
// 규칙 위반(마지막 관리자, 중복 이름, 약한 비밀번호 등)은 사용자에게 보여도 안전한 메시지의
// InvalidOperationException 으로 던진다. 비밀번호 해시/정책은 PasswordHasher 가 담당한다.
// ============================================================================
namespace SpatialEditor.Infrastructure;

/// <summary>
/// 응용 프로그램의 사용자, 역할, 권한, 감사 로그를 같은 PostgreSQL 데이터베이스에 저장하는 저장소입니다.
/// 규칙 위반(마지막 관리자, 중복 이름, 약한 비밀번호 등)은 사용자에게 그대로 보여줘도 안전한 메시지를 가진
/// <see cref="InvalidOperationException"/> 으로 던집니다. 사용이 끝나면 <see cref="DisposeAsync"/> 로 연결 풀을 해제해야 합니다.
/// </summary>
public sealed class UserRepository : IAsyncDisposable
{
    /// <summary>연속 로그인 실패가 이 횟수에 도달하면 계정을 잠급니다.</summary>
    public const int MaxFailedAttempts = 5;
    /// <summary>실패 횟수 초과로 계정이 잠기는 시간(15분). 이후 자동으로 풀리며, 관리자는 즉시 해제할 수도 있습니다.</summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>연결 풀을 관리하는 Npgsql 데이터 소스. 모든 쿼리의 연결은 여기서 얻습니다.</summary>
    private readonly NpgsqlDataSource dataSource;

    /// <summary>저장소를 만들고 연결 풀을 준비합니다(이 시점에는 DB 에 접속하지 않습니다).</summary>
    /// <param name="connectionString">PostgreSQL 연결 문자열. null/빈 값이면 안 됩니다.</param>
    public UserRepository(string connectionString)
    {
        dataSource = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>연결 풀을 해제합니다.</summary>
    /// <returns>해제 작업을 나타내는 ValueTask.</returns>
    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    /// <summary>
    /// 사용자/역할/권한/감사 로그 테이블이 없으면 만들고, 기본 제공 역할(<see cref="Permissions.DefaultRoles"/>)을 최초 1회 생성합니다.
    /// 여러 번 호출해도 안전합니다(멱등).
    /// </summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>작업 완료를 나타내는 Task.</returns>
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

        // 1) 스키마 생성 SQL 을 실행합니다(IF NOT EXISTS 이므로 이미 있으면 아무 일도 하지 않음).
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(sql, connection))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Built-in roles are created once; later permission changes made by administrators stick.
        // 2) 기본 역할 생성: ON CONFLICT DO NOTHING 이므로 이미 있는 역할은 건드리지 않습니다.
        //    RETURNING id 가 돌아온 경우(= 이번에 새로 만들어진 경우)에만 권한을 채워 넣어,
        //    관리자가 나중에 바꾼 기본 역할의 권한이 재시작 때 덮어써지지 않게 합니다.
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

    /// <summary>등록된 사용자가 한 명이라도 있는지 확인합니다. false 이면 최초 관리자 생성 화면을 보여줍니다.</summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>사용자가 1명 이상이면 true.</returns>
    public async Task<bool> HasUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM app_users);");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // ---------------------------------------------------------------- login

    /// <summary>최초 관리자를 만듭니다. 사용자가 전혀 없을 때만 허용됩니다.</summary>
    /// <param name="loginId">로그인 아이디. 공백 제외 1~64자(앞뒤 공백은 제거됨).</param>
    /// <param name="displayName">표시 이름. 필수.</param>
    /// <param name="password">초기 비밀번호. <see cref="PasswordHasher.ValidatePolicy"/> 정책을 통과해야 합니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>생성된 관리자 사용자(권한 집합 포함).</returns>
    /// <exception cref="InvalidOperationException">입력 검증 실패, 또는 이미 사용자가 존재할 때.</exception>
    public async Task<AppUser> CreateInitialAdminAsync(
        string loginId, string displayName, string password, CancellationToken cancellationToken = default)
    {
        ValidateNewUser(loginId, displayName, password);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 1) 테이블을 배타 잠금하여, 두 클라이언트가 동시에 "최초 관리자"를 만드는 경합을 막습니다.
        await using (var lockCommand = new NpgsqlCommand("LOCK TABLE app_users IN EXCLUSIVE MODE;", connection, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2) 잠금을 잡은 뒤 다시 사용자 존재 여부를 확인합니다(그 사이 다른 쪽이 먼저 만들었을 수 있음).
        await using (var check = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM app_users);", connection, transaction))
        {
            if ((bool)(await check.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("An administrator already exists. Log in instead.");
            }
        }

        // 3) 사용자 행 삽입(비밀번호는 해시로 저장, 다음 로그인 때 변경 강제 안 함, 생성자 없음).
        var userId = await InsertUserAsync(connection, transaction, loginId.Trim(), displayName.Trim(), password, false, null, cancellationToken);
        // 4) 기본 역할 'Admin' 을 부여합니다(EnsureSchemaAsync 가 미리 만들어 둔 역할).
        await using (var assign = new NpgsqlCommand(
            "INSERT INTO user_roles (user_id, role_id) SELECT $1, id FROM roles WHERE name = 'Admin';", connection, transaction))
        {
            assign.Parameters.AddWithValue(userId);
            await assign.ExecuteNonQueryAsync(cancellationToken);
        }

        // 5) 방금 만든 사용자를 역할/권한 포함 형태로 다시 읽고, 감사 로그를 남깁니다.
        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, user, "user.create", "user", user.Id.ToString(), $"initial administrator '{user.LoginId}'", cancellationToken);
        // 6) 안전장치: 사용자 관리 권한을 가진 활성 사용자가 있는지 확인한 뒤 커밋합니다(없으면 롤백).
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    /// <summary>
    /// 아이디/비밀번호로 로그인을 시도합니다. 실패 횟수 누적과 계정 잠금도 여기서 처리합니다.
    /// 모든 결과(성공/실패)는 한 트랜잭션 안에서 처리되고 감사 로그에 기록됩니다.
    /// </summary>
    /// <param name="loginId">로그인 아이디(대소문자 구분 안 함, 앞뒤 공백 제거).</param>
    /// <param name="password">입력한 평문 비밀번호.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>성공이면 User 가 채워진 <see cref="AuthResult"/>, 실패면 Error 메시지가 채워진 결과.</returns>
    public async Task<AuthResult> AuthenticateAsync(string loginId, string password, CancellationToken cancellationToken = default)
    {
        // 아이디 존재 여부를 추측할 수 없도록, 미존재/비밀번호 불일치에 같은 문구를 사용합니다.
        const string invalid = "Invalid ID or password.";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // 1) 사용자 행을 FOR UPDATE 로 잠그며 조회합니다. 같은 계정에 대한 동시 로그인 시도가
        //    직렬화되므로 실패 횟수 증가/잠금 판정이 어긋나지 않습니다.
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

        // 2) 존재하지 않는 아이디: 감사 로그만 남기고 일반 실패 메시지를 돌려줍니다.
        if (userId is null || hash is null)
        {
            await WriteAuditAsync(connection, transaction, null, "login.failed", "user", null, $"unknown id '{loginId.Trim()}'", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, invalid);
        }

        // 3) 비활성 계정: 비밀번호를 검사하지 않고 거부합니다(실패 횟수도 올리지 않음).
        if (!isActive)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, "This account is disabled. Contact an administrator.");
        }

        // 4) 잠금 시간이 아직 남은 계정: 비밀번호 검사 없이 거부하고 해제 예정 시각(로컬 시간)을 알려줍니다.
        if (lockedUntil is { } until && until > DateTime.UtcNow)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AuthResult(null, $"This account is locked until {until.ToLocalTime():HH:mm}. Try again later or ask an administrator to unlock it.");
        }

        // 5) 비밀번호 검증 실패: 실패 횟수를 1 올립니다. 올린 값이 MaxFailedAttempts 에 도달하면
        //    횟수를 0 으로 되돌리고 locked_until 을 현재+잠금분(15분)으로 설정합니다(한 SQL 에서 원자적으로 처리).
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
            // 갱신 결과의 locked_until 이 미래라면 이번 실패로 잠긴 것이므로 잠금 안내 메시지를 줍니다.
            var newLock = await fail.ExecuteScalarAsync(cancellationToken);
            await WriteAuditAsync(connection, transaction, null, "login.failed", "user", userId.Value.ToString(), $"wrong password for '{loginId.Trim()}'", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return newLock is DateTime lockTime && lockTime > DateTime.UtcNow
                ? new AuthResult(null, $"Too many failed attempts. The account is locked for {(int)LockoutDuration.TotalMinutes} minutes.")
                : new AuthResult(null, invalid);
        }

        // 6) 로그인 성공: 실패 횟수와 잠금을 초기화하고 마지막 로그인 시각을 갱신합니다.
        await using (var ok = new NpgsqlCommand(
            "UPDATE app_users SET failed_attempts = 0, locked_until = NULL, last_login_at = CURRENT_TIMESTAMP WHERE id = $1;",
            connection, transaction))
        {
            ok.Parameters.AddWithValue(userId.Value);
            await ok.ExecuteNonQueryAsync(cancellationToken);
        }

        // 7) 역할/권한을 포함한 사용자 정보를 읽어 로그인 감사 로그를 남기고 반환합니다.
        var user = (await LoadUsersAsync(connection, transaction, userId.Value, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, user, "login", "user", user.Id.ToString(), null, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuthResult(user, null);
    }

    // ---------------------------------------------------------------- users

    /// <summary>모든 사용자를 로그인 아이디 순(대소문자 무시)으로 조회합니다.</summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>역할/권한이 채워진 사용자 목록.</returns>
    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await LoadUsersAsync(connection, null, null, cancellationToken);
    }

    /// <summary>사용자 한 명을 조회합니다.</summary>
    /// <param name="userId">조회할 사용자 DB id.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>해당 사용자. 존재하지 않으면 null.</returns>
    public async Task<AppUser?> GetUserAsync(long userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await LoadUsersAsync(connection, null, userId, cancellationToken)).SingleOrDefault();
    }

    /// <summary>새 사용자를 만들고 역할을 부여합니다.</summary>
    /// <param name="actor">작업을 수행하는 관리자(감사 로그의 행위자, 생성자 기록).</param>
    /// <param name="loginId">로그인 아이디. 1~64자, 대소문자 구분 없이 고유해야 합니다.</param>
    /// <param name="displayName">표시 이름. 필수.</param>
    /// <param name="password">초기 비밀번호. 비밀번호 정책을 통과해야 합니다.</param>
    /// <param name="mustChangePassword">true 이면 첫 로그인 때 비밀번호를 바꾸도록 강제합니다.</param>
    /// <param name="roleIds">부여할 역할 id 목록(중복은 무시, 비어 있을 수 있음).</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>생성된 사용자(역할/권한 포함).</returns>
    /// <exception cref="InvalidOperationException">입력 검증 실패 또는 아이디 중복.</exception>
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
            // 1) 사용자 삽입. 대소문자 무시 고유 인덱스 위반은 "이미 사용 중인 ID" 메시지로 바꿔 던집니다.
            userId = await InsertUserAsync(connection, transaction, loginId.Trim(), displayName.Trim(), password, mustChangePassword, actor.Id, cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException($"The ID '{loginId.Trim()}' is already in use.");
        }

        // 2) 역할 부여 후 최종 상태를 읽어 감사 로그에 남깁니다.
        await ReplaceRolesAsync(connection, transaction, userId, roleIds, cancellationToken);
        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, actor, "user.create", "user", userId.ToString(),
            $"'{user.LoginId}' roles=[{string.Join(", ", user.Roles)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return user;
    }

    /// <summary>사용자의 표시 이름, 활성 여부, 역할을 변경합니다(아이디는 변경 불가).</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="userId">수정할 사용자 id.</param>
    /// <param name="displayName">새 표시 이름. 필수.</param>
    /// <param name="isActive">활성 여부. 자기 자신을 비활성화할 수 없습니다.</param>
    /// <param name="roleIds">새 역할 id 목록. 기존 역할은 모두 교체됩니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">이름 누락, 자기 비활성화, 사용자 없음, 마지막 관리자 보호 위반.</exception>
    public async Task UpdateUserAsync(
        AppUser actor, long userId, string displayName, bool isActive,
        IReadOnlyCollection<long> roleIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new InvalidOperationException("Name is required.");
        }

        // 자기 자신을 비활성화하면 즉시 로그아웃되어 복구가 어려우므로 막습니다.
        if (!isActive && userId == actor.Id)
        {
            throw new InvalidOperationException("You cannot deactivate your own account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 1) 이름/활성 여부 갱신. 영향받은 행이 0 이면 그 사이 삭제된 사용자입니다.
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
        // 2) 역할 교체나 비활성화로 관리자가 0명이 되면 예외로 전체 롤백합니다(마지막 관리자 보호).
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        var user = (await LoadUsersAsync(connection, transaction, userId, cancellationToken)).Single();
        await WriteAuditAsync(connection, transaction, actor, "user.update", "user", userId.ToString(),
            $"'{user.LoginId}' active={isActive} roles=[{string.Join(", ", user.Roles)}]", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>관리자가 다른 사용자의 비밀번호를 새 값으로 재설정합니다. 실패 횟수와 잠금도 함께 풀립니다.</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="userId">대상 사용자 id.</param>
    /// <param name="newPassword">새 평문 비밀번호(정책 검사 후 해시로 저장).</param>
    /// <param name="mustChangePassword">true 이면 다음 로그인 때 본인이 다시 바꾸도록 강제.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">정책 위반 또는 사용자 없음.</exception>
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

    /// <summary>사용자가 자신의 비밀번호를 변경합니다(현재 비밀번호 확인 필요).</summary>
    /// <param name="user">비밀번호를 바꾸는 본인.</param>
    /// <param name="oldPassword">현재 평문 비밀번호.</param>
    /// <param name="newPassword">새 평문 비밀번호(정책 검사 후 해시로 저장).</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">정책 위반 또는 현재 비밀번호 불일치.</exception>
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
        // 1) 해시를 행 잠금과 함께 읽어, 확인과 변경 사이에 다른 변경이 끼어들지 않게 합니다.
        await using (var read = new NpgsqlCommand("SELECT password_hash FROM app_users WHERE id = $1 FOR UPDATE;", connection, transaction))
        {
            read.Parameters.AddWithValue(user.Id);
            hash = await read.ExecuteScalarAsync(cancellationToken) as string;
        }

        // 2) 현재 비밀번호가 맞아야만 변경합니다(자리 비운 사이 세션 탈취 방지).
        if (hash is null || !PasswordHasher.Verify(oldPassword, hash))
        {
            throw new InvalidOperationException("The current password is incorrect.");
        }

        // 3) 새 해시 저장 및 "변경 강제" 플래그 해제.
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

    /// <summary>로그인 실패로 잠긴 계정을 즉시 풀고 실패 횟수를 0 으로 되돌립니다.</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="userId">대상 사용자 id.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
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
    /// 사용자를 영구 삭제합니다. 활동 기록이 전혀 없는 계정만 삭제할 수 있으며,
    /// 그 외에는 감사 이력의 작성자가 남도록 비활성화해야 합니다.
    /// </summary>
    /// <param name="actor">작업을 수행하는 관리자. 자기 자신은 삭제할 수 없습니다.</param>
    /// <param name="userId">삭제할 사용자 id.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">자기 삭제, 활동 이력 존재, 사용자 없음, 마지막 관리자 보호 위반.</exception>
    public async Task DeleteUserAsync(AppUser actor, long userId, CancellationToken cancellationToken = default)
    {
        if (userId == actor.Id)
        {
            throw new InvalidOperationException("You cannot delete your own account.");
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        // 1) 감사 로그에 이 사용자의 행위가 하나라도 있으면 삭제 대신 비활성화를 안내합니다.
        await using (var history = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM audit_log WHERE user_id = $1);", connection, transaction))
        {
            history.Parameters.AddWithValue(userId);
            if ((bool)(await history.ExecuteScalarAsync(cancellationToken))!)
            {
                throw new InvalidOperationException("This user has activity history, so it cannot be deleted. Deactivate the account instead.");
            }
        }

        string? loginId;
        // 2) 삭제하고 로그 기록용 로그인 아이디를 돌려받습니다(user_roles 는 ON DELETE CASCADE).
        await using (var delete = new NpgsqlCommand("DELETE FROM app_users WHERE id = $1 RETURNING login_id;", connection, transaction))
        {
            delete.Parameters.AddWithValue(userId);
            loginId = await delete.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (loginId is null)
        {
            throw new InvalidOperationException("The user no longer exists.");
        }

        // 3) 삭제 후에도 사용자 관리 권한 보유 활성 사용자가 남는지 확인합니다.
        await EnsureManagerRemainsAsync(connection, transaction, cancellationToken);
        await WriteAuditAsync(connection, transaction, actor, "user.delete", "user", userId.ToString(), $"'{loginId}'", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- roles

    /// <summary>모든 역할을 조회합니다(기본 제공 역할 먼저, 그다음 이름순). 권한 키 목록과 사용자 수를 함께 계산합니다.</summary>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>역할 목록.</returns>
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

    /// <summary>사용자 정의 역할을 만듭니다(시스템 역할 아님).</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="name">역할 이름. 1~64자, 고유해야 합니다.</param>
    /// <param name="description">설명. 없으면 null.</param>
    /// <param name="permissionKeys">부여할 권한 키 목록. 알 수 없는 키는 무시됩니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>새 역할의 id.</returns>
    /// <exception cref="InvalidOperationException">이름 검증 실패 또는 이름 중복.</exception>
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

    /// <summary>역할의 이름, 설명, 권한 집합을 변경합니다. 권한은 전부 지우고 다시 넣는 방식으로 교체합니다.</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="roleId">수정할 역할 id.</param>
    /// <param name="name">새 이름. 1~64자, 고유해야 합니다.</param>
    /// <param name="description">새 설명. 없으면 null.</param>
    /// <param name="permissionKeys">새 권한 키 목록. 알 수 없는 키는 무시됩니다.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">이름 검증 실패/중복, 역할 없음, 마지막 관리자 보호 위반.</exception>
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

        // 1) 기존 권한을 모두 지우고 2) 새 권한을 넣은 뒤 3) 관리 권한 보유자가 남는지 검사합니다
        //    (모두 같은 트랜잭션이므로 검사에 실패하면 권한 변경 전체가 취소됩니다).
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

    /// <summary>역할을 삭제합니다. 기본 제공 역할이거나 사용 중인 역할은 삭제할 수 없습니다.</summary>
    /// <param name="actor">작업을 수행하는 관리자.</param>
    /// <param name="roleId">삭제할 역할 id.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <exception cref="InvalidOperationException">역할 없음, 시스템 역할, 사용자가 아직 보유 중.</exception>
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

        // 시스템 역할 삭제 금지 -> 사용 중인 역할 삭제 금지(사용자가 권한을 잃지 않도록 먼저 다른 역할 배정 요구).
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

    /// <summary>
    /// 응용 프로그램 이벤트를 감사 로그에 남깁니다. 감사가 본 작업을 막아서는 안 되므로 실패는 삼키고 무시합니다.
    /// </summary>
    /// <param name="actor">행위자. 로그인 전/시스템 이벤트이면 null.</param>
    /// <param name="action">작업 코드(예: "login", "user.create").</param>
    /// <param name="entityType">대상 종류. 없으면 null.</param>
    /// <param name="entityId">대상 식별자 문자열. 없으면 null.</param>
    /// <param name="summary">사람이 읽을 요약. 없으면 null.</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
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

    /// <summary>감사 로그를 최신순으로 조회합니다.</summary>
    /// <param name="limit">가져올 최대 건수(기본 500).</param>
    /// <param name="cancellationToken">작업 취소 토큰. 기본값은 취소 불가(default).</param>
    /// <returns>최신 로그가 앞에 오는 목록.</returns>
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

    /// <summary>새 사용자 입력(아이디 길이, 이름 필수, 비밀번호 정책)을 검증합니다.</summary>
    /// <param name="loginId">로그인 아이디.</param>
    /// <param name="displayName">표시 이름.</param>
    /// <param name="password">평문 비밀번호.</param>
    /// <exception cref="InvalidOperationException">검증 실패(메시지는 사용자에게 표시 가능).</exception>
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

    /// <summary>역할 이름(필수, 최대 64자)을 검증합니다.</summary>
    /// <param name="name">역할 이름.</param>
    /// <exception cref="InvalidOperationException">검증 실패.</exception>
    private static void ValidateRoleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 64)
        {
            throw new InvalidOperationException("Role name is required (up to 64 characters).");
        }
    }

    /// <summary>사용자 행을 삽입하고 새 id 를 돌려줍니다. 비밀번호는 해시로 변환해 저장합니다.</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션.</param>
    /// <param name="loginId">이미 공백 제거된 로그인 아이디.</param>
    /// <param name="displayName">이미 공백 제거된 표시 이름.</param>
    /// <param name="password">평문 비밀번호(해시되어 저장).</param>
    /// <param name="mustChangePassword">다음 로그인 때 변경 강제 여부.</param>
    /// <param name="createdBy">생성한 사용자 id. 최초 관리자이면 null.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>새 사용자 id.</returns>
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

    /// <summary>사용자의 역할을 주어진 목록으로 통째로 교체합니다(전부 삭제 후 중복 제거하여 재삽입).</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션.</param>
    /// <param name="userId">대상 사용자 id.</param>
    /// <param name="roleIds">새 역할 id 목록.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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

    /// <summary>역할에 권한 키들을 넣습니다. 정의되지 않은 키는 걸러내고, 이미 있는 조합은 무시합니다.</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션. 트랜잭션 밖에서 호출하면 null.</param>
    /// <param name="roleId">대상 역할 id.</param>
    /// <param name="keys">추가할 권한 키들.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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
    /// 사용자 관리 권한(user.manage)을 가진 활성 사용자가 한 명도 남지 않게 되는 변경이면 예외를 던져
    /// 바깥 트랜잭션이 롤백되게 합니다(마지막 관리자 보호).
    /// </summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">변경이 속한 트랜잭션(변경 반영 후 상태를 검사해야 하므로 같은 트랜잭션).</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <exception cref="InvalidOperationException">관리 권한 보유 활성 사용자가 0명일 때.</exception>
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

    /// <summary>감사 로그 한 줄을 삽입합니다. 이름은 복사해서 저장하므로 나중에 사용자가 삭제되어도 이력이 남습니다.</summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션. 없으면 null(독립 실행).</param>
    /// <param name="actor">행위자. 없으면 null(id/이름이 NULL 로 저장).</param>
    /// <param name="action">작업 코드.</param>
    /// <param name="entityType">대상 종류. 없으면 null.</param>
    /// <param name="entityId">대상 식별자. 없으면 null.</param>
    /// <param name="summary">요약 설명. 없으면 null.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
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

    /// <summary>
    /// 사용자와 소속 역할 이름, 역할들로부터 합산된(중복 제거) 권한 키를 한 번의 쿼리로 읽어 <see cref="AppUser"/> 로 만듭니다.
    /// </summary>
    /// <param name="connection">열린 연결.</param>
    /// <param name="transaction">소속 트랜잭션. 없으면 null.</param>
    /// <param name="onlyUserId">특정 사용자만 읽으려면 그 id, 전체이면 null.</param>
    /// <param name="cancellationToken">취소 토큰.</param>
    /// <returns>로그인 아이디 순(대소문자 무시)으로 정렬된 사용자 목록.</returns>
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
        // 단일 사용자 조회일 때만 WHERE 조건을 덧붙이고, 항상 아이디 순으로 정렬합니다.
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
