using System.Windows;
using System.Windows.Controls;

// ============================================================================
// PromptWindow : 한 줄 텍스트 입력 대화상자(코드로만 구성, XAML 없음)
// ----------------------------------------------------------------------------
// 흐름도
//   Ask(owner, title, label, initial)
//     → TextBox/확인/취소 버튼/안내 문구를 코드로 조립하여 Window 생성(소유자 중앙, 높이 자동)
//     → 확인 클릭 시 DialogResult=true, 취소(IsCancel)/닫기는 false
//     → Loaded 에서 입력창 포커스 + 전체 선택
//     → ShowDialog 결과가 true 면 입력 텍스트, 아니면 null 반환
// ============================================================================
namespace SpatialEditor.App;

/// <summary>최소 기능의 한 줄 텍스트 입력 대화상자.</summary>
internal static class PromptWindow
{
    /// <summary>모달 입력 창을 띄워 사용자가 입력한 한 줄 텍스트를 받습니다.</summary>
    /// <param name="owner">소유(부모) 창. 가운데 정렬 기준. null 불가.</param>
    /// <param name="title">창 제목에 붙일 이름(앞에 "OmniDT SE - " 가 붙음).</param>
    /// <param name="label">입력창 위에 보여줄 안내 문구.</param>
    /// <param name="initial">입력창 초기 텍스트(열릴 때 전체 선택됨). 기본 빈 문자열.</param>
    /// <returns>확인을 누르면 입력 텍스트, 취소/닫기면 null.</returns>
    public static string? Ask(Window owner, string title, string label, string initial = "")
    {
        // 1) 입력창, 확인(Enter 기본)/취소(Esc) 버튼, 안내 문구를 코드로 조립합니다.
        var input = new TextBox { Text = initial, Margin = new Thickness(0, 4, 0, 12), Padding = new Thickness(3) };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(20, 4, 20, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(input);
        panel.Children.Add(buttons);
        // 2) 크기는 가로 고정·세로 내용에 맞춤, 크기 조절 불가인 모달 창을 구성합니다.
        var window = new Window
        {
            Title = $"OmniDT SE - {title}",
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = System.Windows.Media.Brushes.WhiteSmoke
        };
        // 3) 확인 → DialogResult=true(창 닫힘), 열릴 때 입력창에 포커스를 주고 전체 선택해 바로 덮어쓸 수 있게 합니다.
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        // 4) 모달 표시. 확인으로 닫혔을 때만 입력값을 돌려줍니다.
        return window.ShowDialog() == true ? input.Text : null;
    }
}
