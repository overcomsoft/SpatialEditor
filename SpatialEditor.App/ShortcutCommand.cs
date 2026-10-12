using System.Windows.Input;

// ============================================================================
// ShortcutCommand : 단축키 시스템의 데이터 모델(키 조합 + 바인딩 가능한 명령)
// ----------------------------------------------------------------------------
// 흐름도
//   명령 등록(ShortcutCommand 생성: id/분류/이름/기본 제스처/실행 동작)
//     → 사용자 설정(AppSettings.Shortcuts)이 있으면 Gesture 를 덮어씀, 없으면 기본값 유지
//     → 키 입력 시 KeyGesture2.TryParse(Gesture 문자열) 로 키 조합을 해석해 비교
//     → 일치하면 CanExecute() 확인 후 Run(reverse) 실행
//        (ShiftReverses 명령은 Shift/CapsLock 이 '역방향' 의미라 매칭에서 Shift 를 무시)
//   키 조합 → 문자열(ToString) 은 설정 저장·메뉴 표시용
// ============================================================================
namespace SpatialEditor.App;

/// <summary>
/// 키 하나와 보조키(Ctrl/Alt/Shift/Win) 조합. "Ctrl+Shift+S" 같은 텍스트로 표기·저장됩니다.
/// </summary>
/// <param name="Key">주 키(None 이 아님).</param>
/// <param name="Modifiers">함께 눌린 보조키 플래그 조합.</param>
public readonly record struct KeyGesture2(Key Key, ModifierKeys Modifiers)
{
    /// <summary>"Ctrl+Shift+S" 형식의 텍스트를 키 조합으로 해석합니다(대소문자 무시, '+' 구분).</summary>
    /// <param name="text">해석할 텍스트. null/공백이면 실패.</param>
    /// <param name="gesture">성공 시 해석된 키 조합, 실패 시 default.</param>
    /// <returns>해석에 성공하면 true. 알 수 없는 키 이름이거나 주 키가 없으면 false.</returns>
    public static bool TryParse(string? text, out KeyGesture2 gesture)
    {
        // 1) 실패 기본값을 먼저 채우고, 빈 입력은 즉시 거부합니다.
        gesture = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // 2) '+' 로 쪼개 각 토큰이 보조키인지 주 키인지 판별합니다(보조키 이름이 아니면 Key 열거형으로 해석).
        var modifiers = ModifierKeys.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Key? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var parsed) || parsed == Key.None)
                    {
                        return false;
                    }

                    key = parsed;
                    break;
            }
        }

        // 3) 주 키가 하나도 없으면(보조키만 있음) 유효한 단축키가 아닙니다.
        if (key is null)
        {
            return false;
        }

        gesture = new KeyGesture2(key.Value, modifiers);
        return true;
    }

    /// <summary>키 조합을 "Ctrl+Alt+Shift+Win+Key" 순서의 텍스트로 만듭니다(TryParse 의 역변환).</summary>
    /// <returns>설정 저장/메뉴 표시에 쓰는 문자열.</returns>
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>
/// 단축키에 연결할 수 있는 프로그램 기능 1개. <see cref="ShiftReverses"/> 가 true 면 Shift(또는 Caps Lock)가
/// 반대 방향(예: 회전)을 뜻하므로 매칭 시 Shift 를 무시합니다.
/// </summary>
public sealed class ShortcutCommand
{
    /// <summary>명령을 만들고, 현재 제스처를 기본 제스처로 초기화합니다.</summary>
    /// <param name="id">명령의 고유 식별자(설정 파일의 키). null 불가.</param>
    /// <param name="category">설정 화면에 표시할 분류 이름.</param>
    /// <param name="name">설정 화면에 표시할 명령 이름.</param>
    /// <param name="defaultGesture">기본 단축키 텍스트(예: "Ctrl+S"). 빈 문자열이면 기본 단축키 없음.</param>
    /// <param name="run">실행 동작. 인수는 역방향 요청 여부(true=역방향).</param>
    /// <param name="canExecute">실행 가능 여부 판단 함수. null 이면 항상 가능.</param>
    /// <param name="menuHeader">이 단축키를 표시할 메뉴 항목 머리글('|' 로 여러 개 구분). null 가능.</param>
    /// <param name="shiftReverses">Shift 가 역방향을 뜻하는 명령이면 true. 기본 false.</param>
    public ShortcutCommand(string id, string category, string name, string defaultGesture,
        Action<bool> run, Func<bool>? canExecute = null, string? menuHeader = null, bool shiftReverses = false)
    {
        Id = id;
        Category = category;
        Name = name;
        DefaultGesture = defaultGesture;
        Run = run;
        CanExecute = canExecute ?? (() => true);
        MenuHeader = menuHeader;
        ShiftReverses = shiftReverses;
        Gesture = defaultGesture;
    }

    /// <summary>명령 고유 식별자(설정 저장 키).</summary>
    public string Id { get; }
    /// <summary>설정 화면 분류 이름.</summary>
    public string Category { get; }
    /// <summary>설정 화면에 표시되는 명령 이름.</summary>
    public string Name { get; }
    /// <summary>출하 기본 단축키 텍스트("" = 없음). 설정 초기화 시 복원 대상.</summary>
    public string DefaultGesture { get; }

    /// <summary>현재 적용 중인 단축키 텍스트("" = 없음). 사용자 설정이 있으면 기본값을 덮어씁니다.</summary>
    public string Gesture { get; set; }

    /// <summary>명령을 실행합니다. 인수는 역방향이 요청되었는지 여부(true=역방향).</summary>
    public Action<bool> Run { get; }

    /// <summary>현재 상태에서 실행 가능한지 판단하는 함수(예: 파일이 열려 있을 때만 가능).</summary>
    public Func<bool> CanExecute { get; }

    /// <summary>이 단축키를 표시할 메뉴 항목의 머리글('|' 로 구분된 여러 개 가능). 표시할 메뉴가 없으면 null.</summary>
    public string? MenuHeader { get; }

    /// <summary>true 면 Shift/Caps Lock 이 반대 방향을 뜻하므로 단축키 매칭에서 Shift 를 무시합니다.</summary>
    public bool ShiftReverses { get; }
}
