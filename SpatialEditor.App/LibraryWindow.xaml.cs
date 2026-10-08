using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SpatialEditor.Domain;
using SpatialEditor.Infrastructure;

namespace SpatialEditor.App;

/// <summary>Browse, search and maintain the global equipment block library.</summary>
public partial class LibraryWindow : Window
{
    private const string AllStatuses = "(all)";

    private sealed class Row
    {
        public required LibraryItem Item { get; init; }
        public ImageSource? Thumbnail { get; init; }
        public string SizeText => Item.Width is double w && Item.Depth is double d ? $"{w:0.##} x {d:0.##}" : string.Empty;
    }

    private sealed class CategoryNode
    {
        public long? Id { get; init; }
        public required string Name { get; init; }
        public override string ToString() => Name;
    }

    private readonly LibraryRepository repository;
    private readonly AppUser user;
    private readonly Func<string, string?, string?, string?, Task> audit;
    private readonly List<Row> allRows = new();
    private IReadOnlyList<BlockCategory> categories = Array.Empty<BlockCategory>();
    private bool loading;
    private readonly bool canRenameCategory;

    public LibraryWindow(LibraryRepository repository, AppUser user, Func<string, string?, string?, string?, Task> audit)
    {
        InitializeComponent();
        this.repository = repository;
        this.user = user;
        this.audit = audit;
        StatusFilter.ItemsSource = new[] { AllStatuses }.Concat(LibraryStatus.All).ToArray();
        StatusFilter.SelectedIndex = 0;
        AddCategoryButton.IsEnabled = user.Has(Permissions.LibraryCreate);
        canRenameCategory = user.Has(Permissions.LibraryEdit);
        Loaded += async (_, _) => await ReloadAsync();
    }

    private LibraryItem? SelectedItem => (ItemList.SelectedItem as Row)?.Item;

    private async Task ReloadAsync()
    {
        loading = true;
        try
        {
            var selectedId = SelectedItem?.Id;
            categories = await repository.ListCategoriesAsync();
            var items = await repository.ListItemsAsync();
            allRows.Clear();
            var stroke = Color.FromRgb(0x1F, 0x4E, 0x8C);
            foreach (var item in items)
            {
                allRows.Add(new Row { Item = item, Thumbnail = GeometryPreview.Create(item.LocalGeometry, stroke) });
            }

            BuildTree();
            ApplyFilter(selectedId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Could not load the library: {exception.Message}", "Block library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            loading = false;
        }
    }

    private void BuildTree()
    {
        var previous = (CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode;
        CategoryTree.Items.Clear();
        var root = new TreeViewItem { Header = $"All ({allRows.Count})", Tag = new CategoryNode { Id = null, Name = "All" }, IsExpanded = true };
        CategoryTree.Items.Add(root);
        TreeViewItem? toSelect = root;

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

        AddChildren(root, null);
        toSelect!.IsSelected = true;
    }

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

    private void ApplyFilter(long? keepSelectedId = null)
    {
        if (!IsLoaded && allRows.Count == 0)
        {
            return;
        }

        var categoryId = ((CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode)?.Id;
        var categoryIds = categoryId is long id ? DescendantIds(id) : null;
        var text = SearchBox.Text.Trim();
        var status = StatusFilter.SelectedItem as string ?? AllStatuses;

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

        var shown = allRows.Where(Matches).ToList();
        ItemList.ItemsSource = shown;
        if (keepSelectedId is long keep)
        {
            ItemList.SelectedItem = shown.FirstOrDefault(row => row.Item.Id == keep);
        }

        SummaryText.Text = $"{shown.Count} of {allRows.Count} block(s)";
        ShowDetails();
    }

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.CurrentCultureIgnoreCase) == true;

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
        string Num(double? value) => value is double v ? $"{v:0.###} m" : "-";
        DetailText.Text =
            $"Code: {item.Code}\nName: {item.Name}\nCategory: {item.CategoryName ?? "-"}\n" +
            $"Vendor: {item.Vendor ?? "-"}\nModel: {item.ModelNo ?? "-"}\n" +
            $"Width: {Num(item.Width)}\nDepth: {Num(item.Depth)}\nHeight: {Num(item.Height)}\n" +
            $"Status: {item.Status}   Version: {item.CurrentVersion}\n" +
            $"Placed in drawings: {item.InstanceCount}\nUpdated: {item.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
            (string.IsNullOrWhiteSpace(item.Description) ? string.Empty : $"\n\n{item.Description}");
    }

    private void CategoryTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        RenameCategoryButton.IsEnabled = canRenameCategory && ((CategoryTree.SelectedItem as TreeViewItem)?.Tag as CategoryNode)?.Id is not null;
        if (!loading)
        {
            ApplyFilter(SelectedItem?.Id);
        }
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && IsLoaded)
        {
            ApplyFilter(SelectedItem?.Id);
        }
    }

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails();

    private void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EditButton.IsEnabled)
        {
            Edit_Click(sender, e);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

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

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

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
