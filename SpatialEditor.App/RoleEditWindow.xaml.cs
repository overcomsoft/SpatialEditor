using System.Windows;
using System.Windows.Controls;
using SpatialEditor.Domain;

// ============================================================================
// RoleEditWindow : 역할(Role) 추가/수정 대화상자 (이름, 설명, 권한 체크 목록)
// ----------------------------------------------------------------------------
// [호출 위치] UserManagementWindow.xaml.cs
//   · AddRole_Click  : new RoleEditWindow(null)  { Owner = this }  → 새 역할 추가
//   · EditRole_Click : new RoleEditWindow(role)  { Owner = this }  → 기존 역할 수정
// [생성자 매개변수]
//   existing : 수정할 역할(AppRole). null 이면 "Add role" 모드(빈 입력, 체크 없음).
// [흐름]
//   열기 → 제목 설정("Add role" / "Edit role: 이름")
//     → Permissions.All 을 그룹(Group)별로 묶어 그룹 제목(TextBlock) + 권한마다 CheckBox("라벨  (키)")를 PermissionsPanel 에 생성
//        (수정 모드면 기존 역할이 가진 권한 키만 체크)
//     → 수정 모드면 이름/설명 칸을 기존 값으로 채움
//   사용자 동작: 이름/설명 입력, 권한 체크 변경
//   OK(Ok_Click) → DialogResult = true (검증은 호출자가 수행: 이름 비어 있는지, 중복 등은 서버/호출자 책임)
//   Cancel(IsCancel) → 변경 없이 닫힘.
//   호출자는 true 일 때 RoleName / Description / SelectedPermissions 를 읽어 저장한다.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>역할을 만들거나 수정한다: 이름, 설명, 부여할 권한 키 집합.</summary>
public partial class RoleEditWindow : Window
{
    /// <summary>권한 키 → 해당 체크박스 매핑(선택된 권한 수집용).</summary>
    private readonly Dictionary<string, CheckBox> permissionChecks = new();

    /// <summary>입력된 역할 이름(앞뒤 공백 제거).</summary>
    public string RoleName => NameTextBox.Text.Trim();
    /// <summary>입력된 설명. 공백뿐이면 null.</summary>
    public string? Description => string.IsNullOrWhiteSpace(DescriptionTextBox.Text) ? null : DescriptionTextBox.Text.Trim();
    /// <summary>체크된 권한 키 목록.</summary>
    public IReadOnlyList<string> SelectedPermissions =>
        permissionChecks.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToList();

    /// <summary>역할 편집 창을 구성하고 권한 체크박스를 그룹별로 만든다.</summary>
    /// <param name="existing">수정할 기존 역할. null 이면 새 역할 추가 모드.</param>
    public RoleEditWindow(AppRole? existing)
    {
        InitializeComponent();
        // 1) 추가/수정 모드에 맞게 제목 설정.
        Title = existing is null ? "Add role" : $"Edit role: {existing.Name}";
        // 2) 권한을 그룹별로 묶어 그룹 제목 아래에 권한 체크박스를 나열한다(수정 시 보유 권한은 체크).
        foreach (var group in Permissions.All.GroupBy(item => item.Group))
        {
            PermissionsPanel.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2)
            });
            foreach (var (key, label, _) in group)
            {
                var check = new CheckBox
                {
                    Content = $"{label}  ({key})",
                    IsChecked = existing?.PermissionKeys.Contains(key) == true,
                    Margin = new Thickness(8, 1, 0, 1)
                };
                permissionChecks[key] = check;
                PermissionsPanel.Children.Add(check);
            }
        }

        // 3) 수정 모드면 기존 이름/설명을 입력 칸에 채운다.
        if (existing is not null)
        {
            NameTextBox.Text = existing.Name;
            DescriptionTextBox.Text = existing.Description ?? string.Empty;
        }
    }

    /// <summary>[OK] 버튼 Click 핸들러. 대화상자를 true 로 닫는다(입력 검증은 호출자 책임).</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
