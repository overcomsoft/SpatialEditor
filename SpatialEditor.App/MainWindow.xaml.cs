using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using NetTopologySuite.Geometries;
using Npgsql;
using SpatialEditor.Cad;
using SpatialEditor.Domain;
using SpatialEditor.Geometry;
using SpatialEditor.Infrastructure;
using NtsPolygon = NetTopologySuite.Geometries.Polygon;

namespace SpatialEditor.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly GeometryService geometryService = new();
    private NtsPolygon? samplePolygon;
    private PostgresConnectionSettings connectionSettings = new();
    private readonly ScaleTransform mapScale = new(1, 1);
    private readonly TranslateTransform mapTranslation = new(0, 0);
    private readonly Dictionary<Shape, SpatialFeature> importedShapes = new();
    private bool isPanning;
    private System.Windows.Point panStart;
    private double panStartX;
    private double panStartY;

    public MainWindow()
    {
        InitializeComponent();
            MapLayerCanvas.RenderTransform = new TransformGroup
        {
            Children = { mapScale, mapTranslation }
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            DrawSample();
            connectionSettings = await PostgresConnectionSettings.LoadAsync();
            HostTextBox.Text = connectionSettings.Host;
            PortTextBox.Text = connectionSettings.Port.ToString();
            DatabaseTextBox.Text = connectionSettings.Database;
            UsernameTextBox.Text = connectionSettings.Username;
            PasswordInput.Password = connectionSettings.Password;
            ImportButton.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                StatusText.Text = "Enter a PostgreSQL password, save the JSON, then test the connection.";
            }
            else
            {
                StatusText.Text = "Loading imported DXF polygons...";
                await LoadImportedPolygonsAsync();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not load connection settings: {exception.Message}";
        }
    }

    private void DrawSample()
    {
        samplePolygon = geometryService.CreatePolygon(new[]
        {
            new Coordinate(50, 50), new Coordinate(330, 50),
            new Coordinate(390, 210), new Coordinate(180, 330),
            new Coordinate(50, 50)
        });

        var points = samplePolygon.Coordinates.Select(coordinate => new System.Windows.Point(coordinate.X, coordinate.Y)).ToArray();
        var shape = new System.Windows.Shapes.Polygon
        {
            Points = new PointCollection(points),
            Fill = new SolidColorBrush(Color.FromArgb(55, 28, 126, 214)),
            Stroke = new SolidColorBrush(Color.FromRgb(28, 126, 214)),
            StrokeThickness = 2
        };
            MapLayerCanvas.Children.Add(shape);
        shape.MouseLeftButtonDown += SampleShape_MouseLeftButtonDown;
        StatusText.Text = $"Polygon loaded | SRID {samplePolygon.SRID} | Valid: {samplePolygon.IsValid}";
    }

    private void SampleShape_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        SelectedObjectText.Text = "Sample polygon";
        PropertyDetailsText.Text = $"Type: Polygon\nSRID: {samplePolygon?.SRID}\nValid: {samplePolygon?.IsValid}";
        e.Handled = true;
    }

    private async Task LoadImportedPolygonsAsync()
    {
        try
        {
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            var features = await repository.FindImportedOuterPolygonsAsync();
            if (features.Count == 0)
            {
                StatusText.Text = "No imported DXF polygons found in the configured database.";
                return;
            }

            RenderImportedPolygons(features);
            StatusText.Text = $"Loaded {features.Count} imported DXF outer polygon(s) from {connectionSettings.Database}.";
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            StatusText.Text = "No spatial_features table found. Import a DXF once to create the schema.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not load imported polygons: {exception.Message}";
        }
    }

    private void RenderImportedPolygons(IReadOnlyList<SpatialFeature> features)
    {
        var envelope = new Envelope();
        foreach (var feature in features)
        {
            envelope.ExpandToInclude(feature.Geometry.EnvelopeInternal);
        }

        if (envelope.IsNull)
        {
            return;
        }

            MapLayerCanvas.Children.Clear();
        importedShapes.Clear();
        var width = Math.Max(MapCanvas.ActualWidth, 1);
        var height = Math.Max(MapCanvas.ActualHeight, 1);
        var padding = 28.0;
        var scale = Math.Min(
            (width - padding * 2) / Math.Max(envelope.Width, 1),
            (height - padding * 2) / Math.Max(envelope.Height, 1));

        foreach (var feature in features)
        {
            if (feature.Geometry is not NtsPolygon polygon)
            {
                continue;
            }

            var points = polygon.ExteriorRing.Coordinates
                .Select(coordinate => new System.Windows.Point(
                    padding + (coordinate.X - envelope.MinX) * scale,
                    height - padding - (coordinate.Y - envelope.MinY) * scale))
                .ToArray();
            var shape = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection(points),
                Fill = new SolidColorBrush(Color.FromArgb(45, 34, 139, 105)),
                Stroke = new SolidColorBrush(Color.FromRgb(34, 139, 105)),
                StrokeThickness = 1
            };
            shape.MouseLeftButtonDown += ImportedShape_MouseLeftButtonDown;
                MapLayerCanvas.Children.Add(shape);
            importedShapes[shape] = feature;
        }

        ResetView();
    }

    private void ImportedShape_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is Shape shape && importedShapes.TryGetValue(shape, out var feature))
        {
            SelectedObjectText.Text = $"Feature #{feature.Id}";
            var attributes = feature.Attributes.Count == 0
                ? "(none)"
                : string.Join("\n", feature.Attributes.Select(attribute => $"{attribute.Key}: {attribute.Value}"));
            PropertyDetailsText.Text = $"Type: {feature.FeatureType}\nGeometry: {feature.Geometry.GeometryType}\nSRID: {feature.Geometry.SRID}\nValid: {feature.Geometry.IsValid}\n\nAttributes\n{attributes}";
            shape.StrokeThickness = 3;
            e.Handled = true;
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(1.25, GetCanvasCenter());

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(0.8, GetCanvasCenter());

    private void FitView_Click(object sender, RoutedEventArgs e) => ResetView();

    private void ResetView_Click(object sender, RoutedEventArgs e) => ResetView();

    private void MapCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(MapCanvas));
        e.Handled = true;
    }

    private void MapCanvas_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.MiddleButton != System.Windows.Input.MouseButtonState.Pressed)
        {
            return;
        }

        isPanning = true;
        panStart = e.GetPosition(MapCanvas);
        panStartX = mapTranslation.X;
        panStartY = mapTranslation.Y;
        MapCanvas.Cursor = System.Windows.Input.Cursors.SizeAll;
        MapCanvas.CaptureMouse();
    }

    private void MapCanvas_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!isPanning)
        {
            return;
        }

        var current = e.GetPosition(MapCanvas);
        mapTranslation.X = panStartX + current.X - panStart.X;
        mapTranslation.Y = panStartY + current.Y - panStart.Y;
    }

    private void MapCanvas_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
        {
            return;
        }

        isPanning = false;
        MapCanvas.ReleaseMouseCapture();
        MapCanvas.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private void ZoomAt(double factor, System.Windows.Point center)
    {
        var oldScale = mapScale.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, 0.1, 20);
        var actualFactor = newScale / oldScale;
        mapTranslation.X = center.X - actualFactor * (center.X - mapTranslation.X);
        mapTranslation.Y = center.Y - actualFactor * (center.Y - mapTranslation.Y);
        mapScale.ScaleX = newScale;
        mapScale.ScaleY = newScale;
    }

    private void ResetView()
    {
        mapScale.ScaleX = 1;
        mapScale.ScaleY = 1;
        mapTranslation.X = 0;
        mapTranslation.Y = 0;
    }

    private System.Windows.Point GetCanvasCenter() => new(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2);

    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        var point = geometryService.CreatePoint(210, 160);
        var marker = new Ellipse { Width = 10, Height = 10, Fill = Brushes.OrangeRed };
        Canvas.SetLeft(marker, point.X - 5);
        Canvas.SetTop(marker, point.Y - 5);
            MapLayerCanvas.Children.Add(marker);
        StatusText.Text = $"Point created: ({point.X:0}, {point.Y:0})";
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = samplePolygon is null ? "No geometry loaded" : $"Geometry valid: {samplePolygon.IsValid}";
    }

    private async void ImportDxf_Click(object sender, RoutedEventArgs e)
    {
        const string filePath = @"D:\DINNO\DEV\SD2D\data\SmartLayout.dxf";
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                throw new InvalidOperationException("PostgreSQL password is empty. Enter it and save the connection JSON first.");
            }

            StatusText.Text = "Importing DXF...";
            var importer = new DxfBlockImporter();
            var blocks = await Task.Run(() => importer.Import(filePath));
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            StatusText.Text = "Preparing PostGIS schema...";
            await repository.EnsureSchemaAsync();
            foreach (var block in blocks)
            {
                await repository.InsertCadBlockAsync(
                    block.BlockName,
                    block.Layer,
                    filePath,
                    block.ActualGeometry,
                    block.OuterPolygon,
                    block.Attributes,
                    block.FeatureType);
            }

            StatusText.Text = $"Imported {blocks.Count} DXF layer(s) into PostgreSQL.";
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            StatusText.Text = "Import failed: PostgreSQL user lacks permission to create or write spatial_features. Apply database/001_initial_schema.sql as an administrator.";
        }
        catch (PostgresException exception)
        {
            StatusText.Text = $"Import failed: PostgreSQL {exception.SqlState}: {exception.MessageText}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Import failed: {exception.Message}";
        }
    }

    private async void SaveConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            await connectionSettings.SaveAsync();
            StatusText.Text = $"Connection JSON saved: {PostgresConnectionSettings.DefaultFilePath}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save connection settings: {exception.Message}";
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                throw new InvalidOperationException("PostgreSQL password is empty.");
            }

            StatusText.Text = "Testing PostgreSQL connection...";
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            StatusText.Text = await repository.TestConnectionAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Connection failed: {exception.Message}";
        }
    }

    private PostgresConnectionSettings ReadConnectionSettingsFromUi()
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException("Port must be a number between 1 and 65535.");
        }

        return new PostgresConnectionSettings
        {
            Host = HostTextBox.Text.Trim(),
            Port = port,
            Database = DatabaseTextBox.Text.Trim(),
            Username = UsernameTextBox.Text.Trim(),
            Password = PasswordInput.Password
        };
    }
}