// ============================================================================
// PostgresConnectionSettings 전체 흐름도
//
//   앱 시작 → LoadAsync(JSON 파일 읽기; 없으면 기본값 객체)
//          → 로그인 화면이 Host/Port/Database/Username/Password·LastLogin* 사용
//          → ConnectionString(NpgsqlConnectionStringBuilder로 조립) → 각 Repository가 NpgsqlConnection 생성
//          → 설정 변경(레이어 선택/스타일, 마지막 로그인 ID/암호) → SaveAsync(JSON 파일 쓰기)
//
// 저장 위치: %LOCALAPPDATA%\SpatialEditor\postgres-connection.json (DB 테이블 사용 없음).
// 비밀번호 보호: 로그인 비밀번호는 LastLoginPassword setter에서 Windows DPAPI(현재 사용자 범위)로
//   암호화 + Base64 하여 LastLoginPasswordProtected에만 저장한다(평문은 JSON에 남지 않음).
//   복호화는 같은 Windows 계정/PC에서만 가능하며 실패 시 빈 문자열을 돌려준다.
// 트랜잭션·락 정책: 없음(파일 읽기/쓰기만, 동시 쓰기 보호 없음).
// ============================================================================
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace SpatialEditor.Infrastructure;

/// <summary>A layer's display style: <see cref="Color"/> as "#RRGGBB" and line <see cref="Thickness"/> in screen pixels.</summary>
/// <remarks>레이어 표시 스타일 설정. 선 색상("#RRGGBB")과 선 두께(화면 픽셀), 면 채우기 색/불투명도를 담는다.</remarks>
public sealed class LayerStyleSetting
{
    /// <summary>선 색상("#RRGGBB"). null이면 기본 색을 사용한다.</summary>
    public string? Color { get; set; }
    /// <summary>선 두께(화면 픽셀 단위, 기본 1.0). 지도 축척과 무관한 화면 기준 값이다.</summary>
    public double Thickness { get; set; } = 1.0;
    /// <summary>Polygon fill "#RRGGBB"; null means the fill follows <see cref="Color"/>.</summary>
    /// <remarks>폴리곤 채우기 색. null이면 <see cref="Color"/>를 따른다.</remarks>
    public string? FillColor { get; set; }
    /// <summary>Polygon fill opacity, 0 (transparent) to 1 (solid).</summary>
    /// <remarks>폴리곤 채우기 불투명도(0 투명 ~ 1 불투명). 기본값은 60/255(약 0.235).</remarks>
    public double FillOpacity { get; set; } = 60.0 / 255.0;
}

/// <summary>
/// PostgreSQL 접속 정보와 사용자별 UI 설정(선택 레이어, 레이어 스타일, 마지막 로그인 정보)을
/// JSON 파일로 저장/복원하는 설정 클래스.
/// </summary>
public sealed class PostgresConnectionSettings
{
    /// <summary>DB 서버 호스트(기본 localhost).</summary>
    public string Host { get; set; } = "localhost";
    /// <summary>DB 서버 포트(기본 5432).</summary>
    public int Port { get; set; } = 5432;
    /// <summary>접속할 데이터베이스 이름(기본 dinno).</summary>
    public string Database { get; set; } = "dinno";
    /// <summary>DB 접속 계정(기본 postgres). 앱 사용자 로그인 ID와는 별개이다.</summary>
    public string Username { get; set; } = "postgres";
    /// <summary>DB 접속 비밀번호(기본 빈 문자열). 파일에 평문으로 직렬화될 수 있으므로 취급에 유의한다.</summary>
    public string Password { get; set; } = string.Empty;
    /// <summary>true이면 Npgsql 예외에 서버 상세 오류 정보를 포함한다(기본 true, 진단용).</summary>
    public bool IncludeErrorDetail { get; set; } = true;
    /// <summary>사용자가 마지막으로 선택(표시)한 레이어 이름 목록.</summary>
    public List<string> SelectedLayers { get; set; } = new();

    /// <summary>The ID typed at the last successful login (never the password).</summary>
    /// <remarks>마지막 로그인 성공 시 입력한 앱 사용자 ID(비밀번호는 포함하지 않음).</remarks>
    public string LastLoginId { get; set; } = string.Empty;

    /// <summary>The login password, encrypted with Windows DPAPI (current user) and Base64-encoded. Empty when not remembered.</summary>
    /// <remarks>로그인 비밀번호를 Windows DPAPI(현재 사용자)로 암호화한 뒤 Base64로 인코딩한 값. 기억하지 않으면 빈 문자열.</remarks>
    public string LastLoginPasswordProtected { get; set; } = string.Empty;

    /// <summary>Decrypted last login password; empty if none or if it cannot be decrypted on this account/machine.</summary>
    /// <remarks>
    /// 복호화된 마지막 로그인 비밀번호. 값이 없거나 다른 계정/PC에서 복호화할 수 없으면 빈 문자열.
    /// 설정(set) 시 평문을 즉시 DPAPI로 암호화하여 <see cref="LastLoginPasswordProtected"/>에 저장한다.
    /// JSON에는 직렬화되지 않는다([JsonIgnore]).
    /// </remarks>
    [JsonIgnore]
    public string LastLoginPassword
    {
        get
        {
            // 저장된 값이 없으면 복호화 시도 없이 빈 문자열
            if (string.IsNullOrEmpty(LastLoginPasswordProtected))
            {
                return string.Empty;
            }

            try
            {
                // Base64 해제 → DPAPI(현재 사용자 범위) 복호화 → UTF-8 문자열
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    Convert.FromBase64String(LastLoginPasswordProtected), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception)
            {
                // 다른 사용자/PC 이거나 손상된 값: 오류 대신 빈 문자열로 처리(자동 로그인만 포기)
                return string.Empty;
            }
        }
        set
        {
            // 빈 값이면 저장 값을 지우고, 아니면 UTF-8 → DPAPI 암호화 → Base64
            LastLoginPasswordProtected = string.IsNullOrEmpty(value)
                ? string.Empty
                : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
                    System.Text.Encoding.UTF8.GetBytes(value), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
    }

    /// <summary>Per-layer line color/thickness chosen in the layer style dialog, keyed by layer name.</summary>
    /// <remarks>레이어 스타일 대화상자에서 지정한 레이어별 선 색상/두께 등. 키는 레이어 이름.</remarks>
    public Dictionary<string, LayerStyleSetting> LayerStyles { get; set; } = new();

    /// <summary>
    /// 현재 설정으로 만든 Npgsql 연결 문자열(읽기 전용, JSON 비직렬화).
    /// 연결 타임아웃 10초, 명령 타임아웃 60초로 고정한다.
    /// </summary>
    [JsonIgnore]
    public string ConnectionString
    {
        get
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Database = Database,
                Username = Username,
                Password = Password,
                IncludeErrorDetail = IncludeErrorDetail,
                Timeout = 10,
                CommandTimeout = 60
            };
            return builder.ConnectionString;
        }
    }

    /// <summary>기본 설정 파일 경로: %LOCALAPPDATA%\SpatialEditor\postgres-connection.json.</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "postgres-connection.json");

    /// <summary>
    /// JSON 설정 파일을 읽어 설정 객체를 만든다. 파일이 없거나 내용이 null이면 기본값 객체를 반환한다.
    /// </summary>
    /// <param name="filePath">읽을 파일 경로. null이면 <see cref="DefaultFilePath"/>.</param>
    /// <returns>복원된 설정(항상 null이 아님).</returns>
    /// <exception cref="JsonException">JSON 형식이 손상된 경우 발생할 수 있다.</exception>
    public static async Task<PostgresConnectionSettings> LoadAsync(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        // 최초 실행 등 파일이 없으면 기본값으로 시작
        if (!File.Exists(path))
        {
            return new PostgresConnectionSettings();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<PostgresConnectionSettings>(stream)
            ?? new PostgresConnectionSettings();
    }

    /// <summary>
    /// 현재 설정을 들여쓰기된 JSON으로 저장한다(기존 파일은 덮어씀, 폴더가 없으면 생성).
    /// </summary>
    /// <param name="filePath">저장할 파일 경로. null이면 <see cref="DefaultFilePath"/>.</param>
    public async Task SaveAsync(string? filePath = null)
    {
        var path = filePath ?? DefaultFilePath;
        // 1) 저장 폴더 보장  2) 파일 생성(덮어쓰기)  3) 들여쓰기 JSON 직렬화
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, new JsonSerializerOptions { WriteIndented = true });
    }
}