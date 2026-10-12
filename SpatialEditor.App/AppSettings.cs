using System.IO;
using System.Text.Json;

// ============================================================================
// AppSettings : 편집기 사용자 환경설정(로그인/단축키/자동저장/선택 표시/자동 버전)
// ----------------------------------------------------------------------------
// 흐름도
//   시작 시 Load()
//     → %LocalAppData%\SpatialEditor\app-settings.json 이 있으면 JSON 역직렬화
//     → 범위 보정(자동저장 간격 1~120분, 선택 두께 0.5~8) → 인스턴스 반환
//     → 파일이 없거나 손상(예외)이면 기본값 인스턴스 반환
//   설정 창: Clone() 으로 복사본을 편집 → 확인 시 Save() 로 JSON 기록
//   (저장된 로그인 ID/암호는 연결 설정 파일에 따로 있고, 이 파일은 옵션만 보관)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 편집기의 사용자 환경설정(로그인, 단축키, 자동저장). %LocalAppData%\SpatialEditor\app-settings.json 에
/// 보관됩니다. 저장된 로그인 ID/암호는 연결 설정 파일에 남고, 이 파일에는 옵션만 있습니다.
/// </summary>
public sealed class AppSettings
{
    /// <summary>true 면 시작 시 로그인 창 없이 저장된 ID/암호로 자동 로그인합니다. 기본 false.</summary>
    public bool AutoLogin { get; set; }

    /// <summary>true 면 로그인 성공 후 (DPAPI 로 암호화된) 암호를 보관합니다. 기본 true.</summary>
    public bool RememberPassword { get; set; } = true;

    /// <summary>단축키 재정의 사전: 명령 id → 제스처 텍스트("" = 단축키 없음). 사전에 없는 id 는 기본 단축키를 씁니다.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    /// <summary>true 면 편집 모드에서 미저장 변경을 자동으로 저장합니다. 기본 false.</summary>
    public bool AutoSaveEnabled { get; set; }

    /// <summary>자동 저장 간격(분). 기본 5, 불러올 때 1~120 으로 보정됩니다.</summary>
    public int AutoSaveMinutes { get; set; } = 5;

    /// <summary>선택된 객체의 윤곽선 색("#RRGGBB"). 기본 "#FFD700"(금색).</summary>
    public string SelectionColor { get; set; } = "#FFD700";

    /// <summary>선택된 객체 윤곽선 두께(화면 픽셀). 기본 1.5, 불러올 때 0.5~8 로 보정됩니다.</summary>
    public double SelectionThickness { get; set; } = 1.5;

    /// <summary>true 면 편집 저장마다 도면의 자동 버전(종류 "auto")을 남깁니다. 기본 true.</summary>
    public bool AutoVersionOnSave { get; set; } = true;

    /// <summary>설정 파일의 전체 경로: %LocalAppData%\SpatialEditor\app-settings.json.</summary>
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SpatialEditor",
        "app-settings.json");

    /// <summary>설정 창에서 취소 가능한 편집을 하도록 모든 값을 복사한 새 인스턴스를 만듭니다(단축키 사전도 복사).</summary>
    /// <returns>독립된 복사본.</returns>
    public AppSettings Clone() => new()
    {
        AutoLogin = AutoLogin,
        RememberPassword = RememberPassword,
        Shortcuts = new Dictionary<string, string>(Shortcuts),
        AutoSaveEnabled = AutoSaveEnabled,
        AutoSaveMinutes = AutoSaveMinutes,
        SelectionColor = SelectionColor,
        SelectionThickness = SelectionThickness,
        AutoVersionOnSave = AutoVersionOnSave
    };

    /// <summary>설정 파일을 읽습니다. 파일이 없거나 손상되었으면 기본값을 돌려줍니다.</summary>
    /// <returns>불러온(또는 기본) 설정. null 이 아닙니다.</returns>
    public static AppSettings Load()
    {
        try
        {
            // 1) 파일이 있으면 JSON 을 역직렬화하고, 수동 편집 등으로 범위를 벗어난 값을 보정합니다.
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded is not null)
                {
                    loaded.AutoSaveMinutes = Math.Clamp(loaded.AutoSaveMinutes, 1, 120);
                    loaded.SelectionThickness = Math.Clamp(loaded.SelectionThickness, 0.5, 8);
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
        // 읽기/파싱 실패는 치명적이지 않으므로, 손상된 설정 파일은 아래에서 기본값으로 대체합니다.
        }

        return new AppSettings();
    }

    /// <summary>현재 설정을 들여쓰기된 JSON 으로 설정 파일에 기록합니다(폴더가 없으면 생성).</summary>
    /// <exception cref="IOException">파일을 쓸 수 없을 때(호출자가 처리).</exception>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
