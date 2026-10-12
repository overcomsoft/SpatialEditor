using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SpatialEditor.Domain;

// ============================================================================
// LibraryItemWindow : 라이브러리 항목(블록 카탈로그) 입력/수정 대화상자
// ----------------------------------------------------------------------------
// [생성 방식] 생성자는 private 이며 정적 팩터리 두 개로만 만든다.
//   · ForRegister(categories, suggestedCode)  : 신규 등록 모드
//        호출: MainWindow.xaml.cs RegisterInLibrary_Click (메뉴 "Register selected block in library...")
//        코드/이름이 블록 이름으로 미리 채워지고 코드 편집 가능. 높이/상태/설명 영역은 숨김.
//   · ForEdit(categories, item)               : 수정 모드
//        호출: LibraryWindow.xaml.cs 의 항목 수정 버튼. 코드(Code)는 고정(편집 불가).
// [매개변수]
//   categories    : 블록 분류 목록(부모-자식 계층). "상위 / 하위" 경로 문자열로 콤보박스에 표시.
//   suggestedCode : 신규 등록 시 제안할 코드(보통 선택된 블록 이름).
//   item          : 수정할 라이브러리 항목.
// [흐름]
//   열기 → 생성자: 분류 콤보 채움(BuildChoices, 첫 분류 선택), 상태 콤보 채움(기본 Released),
//          Loaded 시 입력 가능한 첫 칸에 포커스
//     → 팩터리가 모드별 제목/문구/값/표시 여부를 설정
//   사용자 동작: 코드, 이름, 분류, 제조사, 모델, (수정 시) 높이/상태/설명 입력
//   OK(Save/Register) → Ok_Click → 코드·이름 필수 검사 → (수정 모드) 높이가 있으면 0 이상 숫자 검사
//     → 통과하면 ItemHeight 저장, DialogResult = true ; 실패하면 MessageText 에 빨간 오류 문구만 표시하고 유지
//   호출자는 ShowDialog() == true 일 때 Code/ItemName/CategoryId/Vendor/ModelNo/Description/ItemHeight/Status 를 읽는다.
//   Cancel(IsCancel) → 아무 것도 저장하지 않고 닫힘.
// ============================================================================
namespace SpatialEditor.App;

/// <summary>분류 콤보박스의 선택지 하나. 전체 "상위 / 하위" 경로 문자열을 함께 가진다.</summary>
/// <param name="Id">분류 ID.</param>
/// <param name="Path">"상위 / 하위" 형태의 전체 경로(콤보박스에 표시되는 문자열).</param>
public sealed record CategoryChoice(long Id, string Path);

/// <summary>라이브러리 항목의 카탈로그 필드를 입력받는 창. 신규 등록과 기존 항목 수정에 모두 쓰인다.</summary>
public partial class LibraryItemWindow : Window
{
    /// <summary>입력된 항목 코드(앞뒤 공백 제거).</summary>
    public string Code => CodeTextBox.Text.Trim();
    /// <summary>입력된 항목 이름(앞뒤 공백 제거).</summary>
    public string ItemName => NameTextBox.Text.Trim();
    /// <summary>선택된 분류 ID(선택이 없으면 null).</summary>
    public long? CategoryId => (CategoryCombo.SelectedItem as CategoryChoice)?.Id;
    /// <summary>제조사 입력값.</summary>
    public string? Vendor => VendorTextBox.Text;
    /// <summary>모델 번호 입력값.</summary>
    public string? ModelNo => ModelTextBox.Text;
    /// <summary>설명 입력값(수정 모드에서만 보임).</summary>
    public string? Description => DescriptionTextBox.Text;
    /// <summary>입력된 높이. 수정 모드에서 높이를 입력하고 검증을 통과했을 때만 값이 있고, 아니면 null.</summary>
    public double? ItemHeight { get; private set; }
    /// <summary>선택된 상태 문자열(LibraryStatus.All 중 하나, 예: Released).</summary>
    public string Status => (string)StatusCombo.SelectedItem;

    /// <summary>분류 목록을 "상위 / 하위" 전체 경로를 가진 선택지 목록으로 변환하고 경로순으로 정렬한다.</summary>
    /// <param name="categories">분류 목록(ParentId 로 계층을 이룸).</param>
    /// <returns>경로 문자열 기준으로 정렬된 선택지 목록.</returns>
    public static List<CategoryChoice> BuildChoices(IReadOnlyList<BlockCategory> categories)
    {
        // 1) ID 로 빠르게 부모를 찾기 위한 사전을 만든다.
        var byId = categories.ToDictionary(category => category.Id);
        // 2) 로컬 함수: 분류에서 루트까지 부모를 따라 올라가며 이름을 앞에 끼워 넣어 "상위 / 하위" 경로를 만든다.
        string PathOf(BlockCategory category)
        {
            var names = new List<string>();
            for (BlockCategory? current = category; current is not null; current = current.ParentId is long parent && byId.TryGetValue(parent, out var next) ? next : null)
            {
                names.Insert(0, current.Name);
            }

            return string.Join(" / ", names);
        }

        // 3) 경로를 계산해 선택지로 만들고 현재 문화권 규칙으로 정렬한다.
        return categories.Select(category => new CategoryChoice(category.Id, PathOf(category)))
            .OrderBy(choice => choice.Path, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>신규 등록 모드 창을 만든다(코드는 편집 가능, 높이/상태/설명 영역은 숨김).</summary>
    /// <param name="categories">분류 목록.</param>
    /// <param name="suggestedCode">코드/이름 칸에 미리 넣을 제안 값.</param>
    /// <returns>등록 모드로 설정된 창(아직 표시 전).</returns>
    public static LibraryItemWindow ForRegister(IReadOnlyList<BlockCategory> categories, string suggestedCode)
    {
        var window = new LibraryItemWindow(categories);
        window.HeaderText.Text = "Register block in the library";
        window.MessageText.Text = "The selected block's shape becomes a library item. Other placed blocks with the same shape are linked to it.";
        window.OkButton.Content = "Register";
        window.CodeTextBox.Text = suggestedCode;
        window.NameTextBox.Text = suggestedCode;
        window.EditOnlyPanel.Visibility = Visibility.Collapsed;
        window.DescriptionLabel.Visibility = window.DescriptionTextBox.Visibility = Visibility.Collapsed;
        window.Height = 350;
        return window;
    }

    /// <summary>수정 모드 창을 만든다(코드는 고정, 기존 값으로 모든 칸을 채움).</summary>
    /// <param name="categories">분류 목록.</param>
    /// <param name="item">수정할 라이브러리 항목.</param>
    /// <returns>수정 모드로 설정된 창(아직 표시 전).</returns>
    public static LibraryItemWindow ForEdit(IReadOnlyList<BlockCategory> categories, LibraryItem item)
    {
        var window = new LibraryItemWindow(categories);
        window.HeaderText.Text = $"Edit {item.Code}";
        window.CodeTextBox.Text = item.Code;
        window.CodeTextBox.IsEnabled = false;
        window.NameTextBox.Text = item.Name;
        window.VendorTextBox.Text = item.Vendor;
        window.ModelTextBox.Text = item.ModelNo;
        window.DescriptionTextBox.Text = item.Description;
        window.HeightTextBox.Text = item.Height?.ToString(CultureInfo.InvariantCulture);
        window.StatusCombo.SelectedItem = item.Status;
        window.CategoryCombo.SelectedItem = ((List<CategoryChoice>)window.CategoryCombo.ItemsSource)
            .FirstOrDefault(choice => choice.Id == item.CategoryId);
        return window;
    }

    /// <summary>공통 초기화(private): 분류/상태 콤보 채우기와 초기 포커스 설정. 모드별 설정은 정적 팩터리가 이어서 한다.</summary>
    /// <param name="categories">분류 목록.</param>
    private LibraryItemWindow(IReadOnlyList<BlockCategory> categories)
    {
        InitializeComponent();
        // 1) 분류 콤보를 채우고 가장 작은 ID 의 분류를 기본 선택한다.
        var choices = BuildChoices(categories);
        CategoryCombo.ItemsSource = choices;
        CategoryCombo.SelectedItem = choices.OrderBy(choice => choice.Id).FirstOrDefault();
        // 2) 상태 콤보를 채우고 기본값을 Released 로 한다.
        StatusCombo.ItemsSource = LibraryStatus.All;
        StatusCombo.SelectedItem = LibraryStatus.Released;
        // 3) 표시되면 편집 가능한 첫 칸(코드가 잠겨 있으면 이름)에 포커스를 준다.
        Loaded += (_, _) => (CodeTextBox.IsEnabled ? CodeTextBox : NameTextBox).Focus();
    }

    /// <summary>[OK(Save/Register)] 버튼 Click 핸들러. 필수값/높이 형식을 검사한 뒤 통과하면 true 로 닫는다.</summary>
    /// <param name="sender">OK 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // 1) 코드와 이름은 필수.
        if (Code.Length == 0 || ItemName.Length == 0)
        {
            MessageText.Text = "Code and name are required.";
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

        // 2) 높이는 수정 모드(EditOnlyPanel 보임)이고 입력이 있을 때만 검사한다. 0 이상 숫자(마침표 소수점)여야 한다.
        var heightText = HeightTextBox.Text.Trim();
        if (EditOnlyPanel.Visibility == Visibility.Visible && heightText.Length > 0)
        {
            if (!double.TryParse(heightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var height) || height < 0)
            {
                MessageText.Text = "Height must be a non-negative number.";
                MessageText.Foreground = Brushes.Firebrick;
                return;
            }

            ItemHeight = height;
        }

        // 3) 모든 검사를 통과 → 호출자가 프로퍼티들을 읽어 저장한다.
        DialogResult = true;
    }
}
