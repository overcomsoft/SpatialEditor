using System.Windows;

// ============================================================================
// SaveVersionWindow : 버전 이름(라벨)과 선택 설명을 입력받는 대화상자
// ----------------------------------------------------------------------------
// [호출 위치]
//   1) MainWindow.Versions.cs SaveVersion_Click (메뉴 Version > Save version...)
//        new SaveVersionWindow(activeLayoutName, $"{activeLayoutName} {DateTime.Now:MM-dd HH:mm}")
//   2) VersionManagerWindow.xaml.cs Save_Click ("Save new version…" 버튼): 위와 같은 기본 인자
//   3) VersionManagerWindow.xaml.cs Edit_Click ("Rename…" 버튼):
//        new SaveVersionWindow(layoutName, version.Label, version.Comment, "Rename version", "OK")
// [생성자 매개변수]
//   layoutName     : 대상 도면 이름(상단에 "Drawing: 이름" 으로 표시).
//   suggestedLabel : 라벨 칸에 미리 채울 제안 이름.
//   comment        : 설명 칸에 미리 채울 기존 설명(이름 변경 시). 기본 null.
//   title          : 창 제목. 기본 "Save version".
//   okText         : OK 버튼 문구. 기본 "Save version".
// [흐름]
//   열기 → 제목/버튼 문구/도면 이름/제안 라벨/설명 설정 → Loaded 시 라벨 칸에 포커스하고 전체 선택
//   OK(Ok_Click, Enter) → 라벨이 비어 있으면 경고 후 유지, 아니면 DialogResult = true
//   Cancel(IsCancel) → 닫힘.
//   호출자는 true 일 때 Label / Comment 를 읽어 버전을 저장(또는 이름 변경)한다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>새 버전의 이름과 선택 설명을 입력받는 창.</summary>
public partial class SaveVersionWindow : Window
{
    /// <summary>입력된 버전 이름(앞뒤 공백 제거, 필수).</summary>
    public string Label => LabelBox.Text.Trim();
    /// <summary>입력된 설명(앞뒤 공백 제거). 비어 있으면 null.</summary>
    public string? Comment => string.IsNullOrWhiteSpace(CommentBox.Text) ? null : CommentBox.Text.Trim();

    /// <summary>버전 저장/이름 변경 창을 구성한다.</summary>
    /// <param name="layoutName">대상 도면 이름(헤더에 표시).</param>
    /// <param name="suggestedLabel">라벨 칸에 미리 넣을 제안 이름.</param>
    /// <param name="comment">설명 칸에 미리 넣을 기존 설명(없으면 null).</param>
    /// <param name="title">창 제목.</param>
    /// <param name="okText">OK 버튼 문구.</param>
    public SaveVersionWindow(string layoutName, string suggestedLabel, string? comment = null, string title = "Save version", string okText = "Save version")
    {
        InitializeComponent();
        // 1) 호출 목적(저장/이름 변경)에 맞게 제목, 버튼 문구, 초기값을 설정한다.
        Title = title;
        OkButton.Content = okText;
        HeaderText.Text = $"Drawing: {layoutName}";
        LabelBox.Text = suggestedLabel;
        CommentBox.Text = comment ?? string.Empty;
        // 2) 표시되면 라벨 칸에 포커스를 주고 전체 선택하여 바로 덮어쓸 수 있게 한다.
        Loaded += (_, _) =>
        {
            LabelBox.Focus();
            LabelBox.SelectAll();
        };
    }

    /// <summary>[OK] 버튼 Click 핸들러. 버전 이름이 비어 있으면 경고하고, 아니면 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Label.Length == 0)
        {
            MessageBox.Show(this, "Enter a version name.", "Save version", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
