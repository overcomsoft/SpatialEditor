using System.Windows;
using System.Windows.Controls;
using SpatialEditor.Domain;

namespace SpatialEditor.App;

public partial class LayerSelectionWindow : Window
{
    private readonly List<CheckBox> layerCheckBoxes = new();

    public IReadOnlyList<string> SelectedLayers { get; private set; } = Array.Empty<string>();

    public LayerSelectionWindow(IReadOnlyList<LayerInfo> layers, IReadOnlyCollection<string>? previouslySelected)
    {
        InitializeComponent();

        var preselect = previouslySelected is { Count: > 0 } ? previouslySelected : null;
        foreach (var layer in layers)
        {
            var checkBox = new CheckBox
            {
                Content = $"{layer.LayerName}  [{layer.GeometryTypes}]  |  Blocks: {layer.InstanceCount:N0}  |  Entities: {layer.EntityCount:N0}",
                Tag = layer.LayerName,
                IsChecked = preselect is null || preselect.Contains(layer.LayerName, StringComparer.OrdinalIgnoreCase),
                Margin = new Thickness(0, 0, 0, 10)
            };
            layerCheckBoxes.Add(checkBox);
            LayerListPanel.Children.Add(checkBox);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerCheckBoxes)
        {
            checkBox.IsChecked = true;
        }
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerCheckBoxes)
        {
            checkBox.IsChecked = false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedLayers = layerCheckBoxes
            .Where(checkBox => checkBox.IsChecked == true)
            .Select(checkBox => (string)checkBox.Tag)
            .ToArray();
        DialogResult = true;
    }
}
