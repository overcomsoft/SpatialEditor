using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

// ============================================================================
// LibraryWindow : 전역 설비 블록 라이브러리 조회/검색/관리 창
// ----------------------------------------------------------------------------
// [호출 위치] MainWindow.xaml.cs OpenLibrary_Click (메뉴 "Block library...")
//   new LibraryWindow(library, currentUser, AuditAsync) { Owner = this }.ShowDialog()
// [생성자 매개변수]
//   repository : 라이브러리 저장소(LibraryRepository) - 분류/항목 조회, 추가, 수정, 삭제.
//   user       : 로그인한 사용자(AppUser). 권한(LibraryCreate/Edit/Delete)에 따라 버튼 활성화 결정.
//   audit      : 감사 로그 기록 콜백. 인자 = (동작명, 대상 종류, 대상 ID, 상세). 예: "library.edit".
// [화면 구성] 왼쪽: 분류 트리(CategoryTree) + New category / Rename category 버튼
//            가운데: 검색 상자(SearchBox) + 상태 필터(StatusFilter) + 항목 목록(ItemList)
//            오른쪽: 미리보기 이미지(PreviewImage) + 상세 텍스트(DetailText) + Edit/Delete/Refresh 버튼
// [흐름]
//   열기 → 생성자: 상태 필터 채움("(all)" + 상태들), 권한에 따라 분류 추가 버튼 활성/이름변경 가능 여부 결정
//     → Loaded 시 ReloadAsync: 분류/항목 조회 → 항목마다 썸네일 생성 → BuildTree(분류 트리 + 개수) → ApplyFilter
//   사용자 동작
//     · 분류 선택(CategoryTree_SelectedItemChanged) / 검색어·상태 변경(Filter_Changed) → ApplyFilter 로 목록 재필터
//     · 목록 선택(ItemList_SelectionChanged) → ShowDetails (미리보기/상세, Edit/Delete 활성 여부 권한 반영)
//     · 목록 더블클릭(ItemList_DoubleClick) → 수정 가능하면 Edit_Click
//     · New category...    (AddCategory_Click)    → 이름 입력 → AddCategoryAsync → 감사로그 → ReloadAsync
//     · Rename category... (RenameCategory_Click) → 이름 입력 → RenameCategoryAsync → 감사로그 → ReloadAsync
//     · Edit...            (Edit_Click)   → LibraryItemWindow.ForEdit 대화상자 → UpdateItemAsync → 감사로그 → ReloadAsync
//     · Delete             (Delete_Click) → 확인 → DeleteItemAsync → 감사로그 → ReloadAsync
//     · Refresh            (Refresh_Click)→ ReloadAsync
//   DialogResult 는 사용하지 않는다(조회/관리 창이며 변경은 즉시 서버에 반영).
// ============================================================================
namespace SpatialEditor.App;

/// <summary>전역 설비 블록 라이브러리를 둘러보고 검색하며 분류/항목을 관리하는 창.</summary>
public partial class LibraryWindow : Window
{
    /// <summary>상태 필터 콤보박스의 "전체" 항목 문자열.</summary>
    private const string AllStatuses = "(all)";

    /// <summary>항목 목록(ListView)에 바인딩되는 한 줄: 라이브러리 항목, 썸네일, 크기 문구.</summary>
    private sealed class Row
    {
        /// <summary>원본 라이브러리 항목.</summary>
        public required LibraryItem Item { get; init; }
        /// <summary>항목의 로컬 도형으로 그린 썸네일 이미지.</summary>
        public ImageSource? Thumbnail { get; init; }
        /// <summary>"가로 x 깊이" 문구. 값이 없으면 빈 문자열.</summary>
        public string SizeText => Item.Width is double w && Item.Depth is double d ? $"{w:0.##} x {d:0.##}" : string.Empty;
    }

    /// <summary>분류 트리 노드의 Tag 에 담는 분류 정보. Id 가 null 이면 루트("All").</summary>
    private sealed class CategoryNode
    {
        /// <summary>분류 ID(루트 "All" 은 null).</summary>
        public long? Id { get; init; }
        /// <summary>분류 이름.</summary>
        public required string Name { get; init; }
        /// <summary>이름을 문자열 표현으로 사용한다.</summary>
        /// <returns>분류 이름.</returns>
        public override string ToString() => Name;
    }

    /// <summary>라이브러리 저장소(DB 접근).</summary>
    private readonly LibraryRepository repository;
    /// <summary>현재 로그인 사용자(권한 확인 및 수정자 ID 기록용).</summary>
    private readonly AppUser user;
    /// <summary>감사 로그 기록 콜백(동작명, 대상 종류, 대상 ID, 상세).</summary>
    private readonly Func<string, string?, string?, string?, Task> audit;
    /// <summary>서버에서 읽은 전체 항목 행(필터 적용 전 원본).</summary>
    private readonly List<Row> allRows = new();
    /// <summary>서버에서 읽은 전체 분류 목록.</summary>
    private IReadOnlyList<BlockCategory> categories = Array.Empty<BlockCategory>();
    /// <summary>ReloadAsync 가 트리/목록을 다시 구성하는 동안 true. 선택 변경 이벤트가 중간 상태로 필터링하는 것을 막는다.</summary>
    private bool loading;
    /// <summary>분류 이름 변경 권한(LibraryEdit) 보유 여부.</summary>
    private readonly bool canRenameCategory;

    /// <summary>라이브러리 창을 만들고 권한에 따라 버튼을 설정한 뒤, 표시되면 데이터를 불러오도록 예약한다.</summary>
    /// <param name="repository">라이브러리 저장소.</param>
    /// <param name="user">로그인 사용자.</param>
    /// <param name="audit">감사 로그 기록 콜백.</param>
    public LibraryWindow(LibraryRepository repository, AppUser user, Func<string, string?, string?, string?, Task> audit)
    {
        InitializeComponent();
        // 1) 의존 객체 보관.
        this.repository = repository;
        this.user = user;
        this.audit = audit;
        // 2) 상태 필터: "(all)" + 정의된 모든 상태, 기본은 전체.
        StatusFilter.ItemsSource = new[] { AllStatuses }.Concat(LibraryStatus.All).ToArray();
        StatusFilter.SelectedIndex = 0;
        // 3) 권한에 따라 분류 추가/이름변경 가능 여부를 정한다(항목 Edit/Delete 는 ShowDetails 에서 결정).
        AddCategoryButton.IsEnabled = user.Has(Permissions.LibraryCreate);
        canRenameCategory = user.Has(Permissions.LibraryEdit);
        // 4) 창이 표시된 뒤 비동기로 데이터를 불러온다.
        Loaded += async (_, _) => await ReloadAsync();
    }

    /// <summary>항목 목록에서 선택된 라이브러리 항목(없으면 null).</summary>
    private LibraryItem? SelectedItem => (ItemList.SelectedItem as Row)?.Item;

    /// <summary>서버에서 분류와 항목을 다시 읽어 트리와 목록을 재구성한다. 가능하면 이전에 선택한 항목을 유지한다.</summary>
    private async Task ReloadAsync()
    {
        loading = true;
        try
        {
            // 1) 재로딩 후 선택을 복원하기 위해 현재 선택 항목 ID 를 기억한다.
            var selectedId = SelectedItem?.Id;
            // 2) 분류와 항목을 조회한다.
            categories = await repository.ListCategoriesAsync();
            var items = await repository.ListItemsAsync();
            // 3) 항목마다 도형 썸네일을 만들어 행으로 보관한다.
            allRows.Clear();
            var stroke = Color.FromRgb(0x1F, 0x4E, 0x8C);
            foreach (var item in items)
            {
                allRows.Add(new Row { Item = item, Thumbnail = GeometryPreview.Create(item.LocalGeometry, stroke) });
            }

            // 4) 분류 트리(개수 포함)를 만들고 현재 필터를 적용한다.
            BuildTree();
            ApplyFilter(selectedId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not load the library: {exception.Message}", "Block library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        // loading 플래그는 성공/실패와 무관하게 반드시 해제한다.
        finally
        {
            loading = false;
        }
    }

    /// <summary>분류 목록으로 "All" 루트 아래 계층 트리를 만들고 각 노드에 하위 포함 항목 개수를 표시한다. 이전 선택 분류를 복원한다.</summary>
    private void BuildTree()
    {
        // 1) 재구성 전에 선택되어 있던 분류를 기억하고 트리를 비운 뒤 "All" 루트를 만든다.
        var previous = (CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode;
        CategoryTree.Items.Clear();
        var root = new TreeViewItem { Header = $"All ({allRows.Count})", Tag = new CategoryNode { Id = null, Name = "All" }, IsExpanded = true };
        CategoryTree.Items.Add(root);
        TreeViewItem? toSelect = root;

        // 2) 로컬 함수(재귀): parentId 의 자식 분류를 노드로 추가한다. 개수는 하위 분류의 항목까지 합산.
        void AddChildren(ItemsControl parent, long? parentId)
        {
            foreach (var category in categories.Where(c => c.ParentId == parentId))
            {
                var ids = DescendantIds(category.Id);
                var count = allRows.Count(row => row.Item.CategoryId is long id && ids.Contains(id));
                var node = new TreeViewItem { Header = $"{category.Name} ({count})", Tag = new CategoryNode { Id = category.Id, Name = category.Name }, IsExpanded = true };
                parent.Items.Add(node);
                if (previous?.Id == category.Id)
                {
                    toSelect = node;
                }

                AddChildren(node, category.Id);
            }
        }

        // 3) 루트부터 재귀 구성 후, 이전 선택 노드(없으면 루트)를 선택한다.
        AddChildren(root, null);
        toSelect!.IsSelected = true;
    }

    /// <summary>주어진 분류와 그 모든 하위 분류의 ID 집합을 구한다(더 늘지 않을 때까지 반복 확장).</summary>
    /// <param name="categoryId">기준 분류 ID.</param>
    /// <returns>기준 분류 자신을 포함한 하위 분류 ID 집합.</returns>
    private HashSet<long> DescendantIds(long categoryId)
    {
        var result = new HashSet<long> { categoryId };
        var added = true;
        while (added)
        {
            added = false;
            foreach (var category in categories)
            {
                if (category.ParentId is long parent && result.Contains(parent) && result.Add(category.Id))
                {
                    added = true;
                }
            }
        }

        return result;
    }

    /// <summary>선택 분류, 검색어, 상태 필터에 맞는 항목만 목록에 표시한다.</summary>
    /// <param name="keepSelectedId">필터 후에도 선택을 유지할 항목 ID(없으면 null).</param>
    private void ApplyFilter(long? keepSelectedId = null)
    {
        // 창 초기화 중 아직 데이터가 없으면 아무 것도 하지 않는다.
        if (!IsLoaded && allRows.Count == 0)
        {
            return;
        }

        // 1) 현재 필터 조건(분류와 하위 분류, 검색어, 상태)을 읽는다.
        var categoryId = ((CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode)?.Id;
        var categoryIds = categoryId is long id ? DescendantIds(id) : null;
        var text = SearchBox.Text.Trim();
        var status = StatusFilter.SelectedItem as string ?? AllStatuses;

        // 2) 로컬 함수: 분류/상태가 맞고, 검색어가 코드/이름/제조사/모델 중 하나에 포함되면 true.
        bool Matches(Row row)
        {
            var item = row.Item;
            if (categoryIds is not null && (item.CategoryId is not long itemCategory || !categoryIds.Contains(itemCategory)))
            {
                return false;
            }

            if (status != AllStatuses && item.Status != status)
            {
                return false;
            }

            return text.Length == 0
                || Contains(item.Code, text) || Contains(item.Name, text)
                || Contains(item.Vendor, text) || Contains(item.ModelNo, text);
        }

        // 3) 필터 결과를 목록에 바인딩하고 선택을 복원한 뒤 요약 문구와 상세 영역을 갱신한다.
        var shown = allRows.Where(Matches).ToList();
        ItemList.ItemsSource = shown;
        if (keepSelectedId is long keep)
        {
            ItemList.SelectedItem = shown.FirstOrDefault(row => row.Item.Id == keep);
        }

        SummaryText.Text = $"{shown.Count} of {allRows.Count} block(s)";
        ShowDetails();
    }

    /// <summary>null 안전 부분 문자열 검색(현재 문화권, 대소문자 무시).</summary>
    /// <param name="value">검색 대상 문자열(null 가능).</param>
    /// <param name="text">찾을 문자열.</param>
    /// <returns>포함하면 true.</returns>
    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;

    /// <summary>선택 항목의 미리보기와 상세 정보를 표시하고, 권한/선택 여부에 따라 Edit/Delete 버튼을 활성화한다.</summary>
    private void ShowDetails()
    {
        var item = SelectedItem;
        EditButton.IsEnabled = item is not null && user.Has(Permissions.LibraryEdit);
        DeleteButton.IsEnabled = item is not null && user.Has(Permissions.LibraryDelete);
        if (item is null)
        {
            PreviewImage.Source = null;
            DetailText.Text = "Select a block to see its details.";
            return;
        }

        PreviewImage.Source = GeometryPreview.Create(item.LocalGeometry, Color.FromRgb(0x1F, 0x4E, 0x8C));
        // 길이 값을 "N m" 로 표시하는 로컬 함수(값이 없으면 "-").
        string Num(double? value) => value is double v ? $"{v:0.###} m" : "-";
        DetailText.Text =
            $"Code: {item.Code}\nName: {item.Name}\nCategory: {item.CategoryName ?? "-"}\n" +
            $"Vendor: {item.Vendor ?? "-"}\nModel: {item.ModelNo ?? "-"}\n" +
            $"Width: {Num(item.Width)}\nDepth: {Num(item.Depth)}\nHeight: {Num(item.Height)}\n" +
            $"Status: {item.Status}   Version: {item.CurrentVersion}\n" +
            $"Placed in drawings: {item.InstanceCount}\nUpdated: {item.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
            (string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $"\n\n{item.Description}");
    }

    /// <summary>CategoryTree 의 SelectedItemChanged 핸들러. 루트가 아니고 권한이 있을 때만 이름 변경을 허용하고 목록을 다시 필터링한다.</summary>
    /// <param name="sender">분류 TreeView.</param>
    /// <param name="e">선택 변경 인자.</param>
    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RenameCategoryButton.IsEnabled = canRenameCategory && ((CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode)?.Id is not null;
        if (!loading)
        {
            ApplyFilter(SelectedItem?.Id);
        }
    }

    /// <summary>SearchBox 의 TextChanged / StatusFilter 의 SelectionChanged 핸들러. 목록을 다시 필터링한다.</summary>
    /// <param name="sender">검색 상자 또는 상태 콤보박스.</param>
    /// <param name="e">이벤트 인자.</param>
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && IsLoaded)
        {
            ApplyFilter(SelectedItem?.Id);
        }
    }

    /// <summary>ItemList 의 SelectionChanged 핸들러. 상세 영역을 갱신한다.</summary>
    /// <param name="sender">항목 ListView.</param>
    /// <param name="e">선택 변경 인자.</param>
    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    /// <summary>ItemList 의 MouseDoubleClick 핸들러. 수정 권한이 있으면 Edit 와 동일하게 동작한다.</summary>
    /// <param name="sender">항목 ListView.</param>
    /// <param name="e">마우스 버튼 인자.</param>
    private void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EditButton.IsEnabled)
        {
            Edit_Click(sender, e);
        }
    }

    /// <summary>[Refresh] 버튼 Click 핸들러. 서버에서 다시 불러온다.</summary>
    /// <param name="sender">Refresh 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    /// <summary>[New category...] 버튼 Click 핸들러. 선택된 분류 아래에 새 분류를 추가한다(루트 선택 시 최상위).</summary>
    /// <param name="sender">New category 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void AddCategory_Click(object sender, RoutedEventArgs e)
    {
        var name = PromptWindow.Ask(this, "New category", "Category name:");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        try
        {
            var parentId = ((CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode)?.Id;
            var id = await repository.AddCategoryAsync(parentId, name);
            await audit("library.category.add", "block_category", id.ToString(), name.Trim());
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "New category", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>[Rename category...] 버튼 Click 핸들러. 선택된 분류의 이름을 바꾼다.</summary>
    /// <param name="sender">Rename category 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if ((CategoryTree.SelectedItem as TreeViewItem)?.Tag is not CategoryNode { Id: long id } node)
        {
            return;
        }

        var name = PromptWindow.Ask(this, "Rename category", "New name:", node.Name);
        if (string.IsNullOrWhiteSpace(name) || name.Trim() == node.Name)
        {
            return;
        }

        try
        {
            await repository.RenameCategoryAsync(id, name);
            await audit("library.category.rename", "block_category", id.ToString(), $"{node.Name} -> {name.Trim()}");
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Rename category", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>[Edit...] 버튼(및 목록 더블클릭) 핸들러. 항목 수정 대화상자를 열고 확인되면 서버에 저장한다.</summary>
    /// <param name="sender">Edit 버튼 또는 ListView.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        // 1) 수정 대화상자 표시(취소하면 종료) → 2) 입력값으로 갱신 → 3) 감사 로그 → 4) 목록 재로딩.
        var window = LibraryItemWindow.ForEdit(categories, item);
        window.Owner = this;
        if (window.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await repository.UpdateItemAsync(
                item.Id,
                new LibraryItemEdit(window.ItemName, window.CategoryId, window.Vendor, window.ModelNo, window.Description, window.ItemHeight, window.Status),
                user.Id);
            await audit("library.edit", "block_library", item.Id.ToString(), item.Code);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Edit library item", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>[Delete] 버튼 Click 핸들러. 확인 후 선택 항목을 삭제한다.</summary>
    /// <param name="sender">Delete 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (MessageBox.Show(this, $"Delete library item '{item.Code}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await repository.DeleteItemAsync(item.Id, user.Id);
            await audit("library.delete", "block_library", item.Id.ToString(), item.Code);
            await ReloadAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Delete library item", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
