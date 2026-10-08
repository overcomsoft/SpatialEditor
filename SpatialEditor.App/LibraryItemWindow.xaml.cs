using System.Globalization;
using System.Windows;
using System.Windows.Media;
using SpatialEditor.Domain;

namespace SpatialEditor.App;

/// <summary>One category choice with its full "Parent / Child" path.</summary>
public sealed record CategoryChoice(long Id, string Path);

/// <summary>Collects the catalogue fields of a library item: for registering a new one or editing an existing one.</summary>
public partial class LibraryItemWindow : Window
{
    public string Code => CodeTextBox.Text.Trim();
    public string ItemName => NameTextBox.Text.Trim();
    public long? CategoryId => (CategoryCombo.SelectedItem as CategoryChoice)?.Id;
    public string? Vendor => VendorTextBox.Text;
    public string? ModelNo => ModelTextBox.Text;
    public string? Description => DescriptionTextBox.Text;
    public double? ItemHeight { get; private set; }
    public string Status => (string)StatusCombo.SelectedItem;

    public static List<CategoryChoice> BuildChoices(IReadOnlyList<BlockCategory> categories)
    {
        var byId = categories.ToDictionary(category => category.Id);
        string PathOf(BlockCategory category)
        {
            var names = new List<string>();
            for (BlockCategory? current = category; current is not null; current = current.ParentId is long parent && byId.TryGetValue(parent, out var next) ? next : null)
            {
                names.Insert(0, current.Name);
            }

            return string.Join(" / ", names);
        }

        return categories.Select(category => new CategoryChoice(category.Id, PathOf(category)))
            .OrderBy(choice => choice.Path, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Register mode: code is editable, suggested from the block name.</summary>
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

    /// <summary>Edit mode: the code is fixed.</summary>
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

    private LibraryItemWindow(IReadOnlyList<BlockCategory> categories)
    {
        InitializeComponent();
        var choices = BuildChoices(categories);
        CategoryCombo.ItemsSource = choices;
        CategoryCombo.SelectedItem = choices.OrderBy(choice => choice.Id).FirstOrDefault();
        StatusCombo.ItemsSource = LibraryStatus.All;
        StatusCombo.SelectedItem = LibraryStatus.Released;
        Loaded += (_, _) => (CodeTextBox.IsEnabled ? CodeTextBox : NameTextBox).Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Code.Length == 0 || ItemName.Length == 0)
        {
            MessageText.Text = "Code and name are required.";
            MessageText.Foreground = Brushes.Firebrick;
            return;
        }

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

        DialogResult = true;
    }
}
