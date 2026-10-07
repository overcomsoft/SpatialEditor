using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
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
    private PostgresConnectionSettings connectionSettings = new();
    private List<string>? activeLayerFilter;
    private readonly ScaleTransform mapScale = new(1, 1);
    private readonly TranslateTransform mapTranslation = new(0, 0);
    private readonly List<SpatialFeature> importedFeatures = new();
    private readonly Dictionary<Shape, double> baseStrokeWidths = new();
    private readonly Dictionary<string, List<Shape>> shapesByLayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> layerZOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> hiddenLayers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ImportAuditLogger importLogger = new();
    private readonly DispatcherTimer strokeWidthUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private int zOrderCounter;
    private Shape? selectionHighlight;
    private string? selectedFeatureLayer;
    private SpatialFeature? selectedFeature;
    private Envelope? renderedEnvelope;
    private double renderedWidth;
    private double renderedHeight;
    private double renderedPadding;
    private double renderedScale;
    private bool isPanning;
    private System.Windows.Point panStart;
    private double panStartX;
    private double panStartY;
    private CancellationTokenSource? importCts;

    private bool isEditMode;
    private readonly Dictionary<long, EditableLayerObject> editableSourcesById = new();
    private readonly Dictionary<long, PendingLayerEdit> pendingEdits = new();
    private readonly Dictionary<long, List<System.Windows.Shapes.Polygon>> editableShapesById = new();
    private long nextTempId = -1;
    private long? selectedEditId;
    // Every selected object (including selectedEditId, the "primary" one shown in the property panel).
    private readonly HashSet<long> selectedEditIds = new();
    private System.Windows.Shapes.Rectangle? boxSelectRectangle;
    private System.Windows.Point boxSelectStart;
    private bool boxSelectAdditive;
    private Ellipse? rotateHandle;
    private Line? rotateHandleLine;
    private EditDragKind editDragKind;
    private System.Windows.Point editDragAnchor;
    private readonly Dictionary<System.Windows.Shapes.Polygon, System.Windows.Point[]> editShapeSnapshot = new();
    private System.Windows.Point[]? editHandleSnapshot;
    private bool isVertexEditMode;
    private readonly List<System.Windows.Shapes.Rectangle> vertexHandles = new();
    private readonly List<Ellipse> midpointHandles = new();
    private (int RingIndex, int PointIndex)? draggingVertex;
    private const double VertexSnapTolerance = 10.0;
    private bool isGridSnapEnabled;
    private const double GridSnapSizeWorldUnits = 10.0;
    private readonly Dictionary<long, Path> editableDetailPathById = new();

    // spatial_features and spatial_block_instances each have their own independent id sequence,
    // so a block instance's raw id could collide with a spatial_features id in the shared
    // long-keyed edit-mode dictionaries. Tagging block-instance ids with this high bit (real ids
    // never come close to it) keeps every existing id-keyed lookup working unchanged; only the
    // save step needs to know to decode it and route to the other table.
    private const long BlockInstanceIdFlag = 1L << 62;
    private static long EncodeBlockInstanceId(long rawId) => rawId | BlockInstanceIdFlag;
    private static bool IsEncodedBlockInstanceId(long id) => (id & BlockInstanceIdFlag) != 0;
    private static long DecodeBlockInstanceId(long encodedId) => encodedId & ~BlockInstanceIdFlag;

    private static readonly Color[] LayerPalette =
    {
        Color.FromRgb(0x1F, 0x77, 0xB4),
        Color.FromRgb(0xFF, 0x7F, 0x0E),
        Color.FromRgb(0x2C, 0xA0, 0x2C),
        Color.FromRgb(0xD6, 0x27, 0x28),
        Color.FromRgb(0x94, 0x67, 0xBD),
        Color.FromRgb(0x8C, 0x56, 0x4B),
        Color.FromRgb(0xE3, 0x77, 0xC2),
        Color.FromRgb(0x7F, 0x7F, 0x7F),
        Color.FromRgb(0xBC, 0xBD, 0x22),
        Color.FromRgb(0x17, 0xBE, 0xCF)
    };

    private readonly Dictionary<string, Color> layerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Rectangle> layerSwatchesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Border> layerRowsByName = new(StringComparer.OrdinalIgnoreCase);
    private string? selectedLayerName;
    private string? activeEditLayerName;

    /// <summary>
    /// Assigns each layer a stable, distinct color from <see cref="LayerPalette"/> the first
    /// time it is seen (in palette-cycle order), reused afterward for the map render and the
    /// layer panel swatch alike.
    /// </summary>
    private Color GetLayerColor(string layerName)
    {
        if (!layerColors.TryGetValue(layerName, out var color))
        {
            color = LayerPalette[layerColors.Count % LayerPalette.Length];
            layerColors[layerName] = color;
        }

        return color;
    }

    private enum EditDragKind
    {
        None,
        Move,
        Vertex,
        Box
    }

    /// <summary>
    /// Tracks an object's live edited state as plain "current" geometries rather than an
    /// accumulated transform, so move/rotate and direct vertex edits can freely interleave
    /// without having to reconcile a transform against a separately-reshaped outline.
    /// </summary>
    private sealed class PendingLayerEdit
    {
        public required EditableLayerObject Source { get; init; }
        public bool IsNew { get; init; }
        public bool IsDeleted { get; set; }
        public bool HasChanges { get; set; }
        public required NetTopologySuite.Geometries.Geometry CurrentGeometry { get; set; }
        public required NetTopologySuite.Geometries.Geometry CurrentOuterGeometry { get; set; }
        public required Dictionary<string, string?> Attributes { get; set; }
    }

    private sealed record AttributeRow(string Key, string? Value);

    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// Keeps a block instance's placement attributes (insert point, rotation) in step with the
    /// rigid move that was just applied to its geometry, so the stored attributes always describe
    /// where the block actually is. Rotation follows the DXF convention (CCW-positive, degrees).
    /// </summary>
    private static void ApplyPlacementMove(PendingLayerEdit edit, double dx, double dy)
    {
        if (edit.Source.FeatureType != "block_instance"
            || !TryGetDouble(edit.Attributes, "block_insert_x", out var x)
            || !TryGetDouble(edit.Attributes, "block_insert_y", out var y))
        {
            return;
        }

        edit.Attributes["block_insert_x"] = (x + dx).ToString("0.######", Invariant);
        edit.Attributes["block_insert_y"] = (y + dy).ToString("0.######", Invariant);
    }

    private static void ApplyPlacementRotation(PendingLayerEdit edit, double pivotX, double pivotY, double ccwDegrees)
    {
        if (edit.Source.FeatureType != "block_instance")
        {
            return;
        }

        if (TryGetDouble(edit.Attributes, "block_insert_x", out var x) && TryGetDouble(edit.Attributes, "block_insert_y", out var y))
        {
            var radians = ccwDegrees * Math.PI / 180.0;
            var cos = Math.Cos(radians);
            var sin = Math.Sin(radians);
            var rx = x - pivotX;
            var ry = y - pivotY;
            edit.Attributes["block_insert_x"] = (pivotX + rx * cos - ry * sin).ToString("0.######", Invariant);
            edit.Attributes["block_insert_y"] = (pivotY + rx * sin + ry * cos).ToString("0.######", Invariant);
        }

        var rotation = TryGetDouble(edit.Attributes, "block_rotation_deg", out var current) ? current : 0.0;
        var normalized = ((rotation + ccwDegrees) % 360.0 + 360.0) % 360.0;
        edit.Attributes["block_rotation_deg"] = normalized.ToString("0.######", Invariant);
    }

    private static bool TryGetDouble(IReadOnlyDictionary<string, string?> attributes, string key, out double value)
    {
        value = 0;
        return attributes.TryGetValue(key, out var text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, Invariant, out value);
    }

    private void ClearPropertyDetails()
    {
        PropertySummaryText.Text = string.Empty;
        PropertyHintText.Text = string.Empty;
        AttributesGrid.ItemsSource = null;
    }

    private static PendingLayerEdit CreatePendingEdit(EditableLayerObject source, bool isNew = false) => new()
    {
        Source = source,
        IsNew = isNew,
        CurrentGeometry = source.Geometry,
        CurrentOuterGeometry = source.OuterGeometry,
        Attributes = new Dictionary<string, string?>(source.Attributes)
    };

    public MainWindow()
    {
        InitializeComponent();
        var mapTransform = new TransformGroup
        {
            Children = { mapScale, mapTranslation }
        };
        MapLayerCanvas.RenderTransform = mapTransform;
        EditableObjectsCanvas.RenderTransform = mapTransform;
        strokeWidthUpdateTimer.Tick += (_, _) =>
        {
            strokeWidthUpdateTimer.Stop();
            UpdateStrokeWidths();
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            connectionSettings = await PostgresConnectionSettings.LoadAsync();
            HostTextBox.Text = connectionSettings.Host;
            PortTextBox.Text = connectionSettings.Port.ToString();
            DatabaseTextBox.Text = connectionSettings.Database;
            UsernameTextBox.Text = connectionSettings.Username;
            PasswordInput.Password = connectionSettings.Password;
            ImportButton.IsEnabled = true;
            DeleteImportButton.IsEnabled = true;
            EditModeButton.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                StatusText.Text = "Enter a PostgreSQL password, save the JSON, then test the connection.";
            }
            else
            {
                await PromptLayerSelectionAndLoadAsync();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not load connection settings: {exception.Message}";
        }
    }

    private async Task PromptLayerSelectionAndLoadAsync()
    {
        IReadOnlyList<LayerInfo> layers;
        ShowBusy("Fetching layer list from PostgreSQL...");
        try
        {
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            layers = await repository.FindImportedLayersAsync();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            StatusText.Text = "No spatial_features table found. Import a DXF once to create the schema.";
            return;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not load the layer list: {exception.Message}";
            return;
        }
        finally
        {
            HideBusy();
        }

        if (layers.Count == 0)
        {
            LayersPanel.Children.Clear();
            StatusText.Text = "No imported DXF polygons found in the configured database.";
            return;
        }

        var dialog = new LayerSelectionWindow(layers, connectionSettings.SelectedLayers) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            StatusText.Text = "Layer selection cancelled. No data was loaded.";
            return;
        }

        activeLayerFilter = dialog.SelectedLayers.ToList();
        connectionSettings.SelectedLayers = activeLayerFilter.ToList();
        await connectionSettings.SaveAsync();

        if (activeLayerFilter.Count == 0)
        {
            LayersPanel.Children.Clear();
            StatusText.Text = "No layers selected. Nothing was loaded.";
            return;
        }

        await LoadImportedPolygonsAsync();
    }

    private async Task LoadImportedPolygonsAsync()
    {
        ShowBusy("Fetching imported geometries from PostgreSQL...");
        try
        {
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            var layerFilter = activeLayerFilter is { Count: > 0 } ? activeLayerFilter : null;
            var layerFeatures = await repository.FindImportedOuterPolygonsAsync(layerFilter);
            var instanceFeatures = await repository.FindImportedBlockInstanceGeometriesAsync(layerFilter);
            if (layerFeatures.Count == 0 && instanceFeatures.Count == 0)
            {
                LayersPanel.Children.Clear();
                StatusText.Text = "No imported DXF polygons found in the configured database.";
                return;
            }

            var instanceLayers = instanceFeatures
                .Select(feature => feature.LayerName ?? "(no layer)")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var features = layerFeatures
                .Where(feature => !instanceLayers.Contains(feature.LayerName ?? "(no layer)"))
                .Concat(instanceFeatures)
                .ToArray();
            var layers = await repository.FindImportedLayersAsync();
            if (layerFilter is not null)
            {
                layers = layers.Where(layer => layerFilter.Contains(layer.LayerName, StringComparer.OrdinalIgnoreCase)).ToArray();
            }

            BusyText.Text = $"Rendering {features.Length:N0} geometries...";
            RenderImportedGeometries(features);
            PopulateLayerPanel(layers);
            StatusText.Text = $"Loaded {features.Length} imported DXF object polygon(s) from {connectionSettings.Database}.";
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            StatusText.Text = "No spatial_features table found. Import a DXF once to create the schema.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not load imported polygons: {exception.Message}";
        }
        finally
        {
            HideBusy();
        }
    }

    private void ShowBusy(string message)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        Cursor = System.Windows.Input.Cursors.Wait;
    }

    private void HideBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>
    /// Renders every feature of a given (layer, geometry-kind) pair as ONE Path built from a
    /// single StreamGeometry, instead of one WPF Shape per feature. With tens of thousands of
    /// imported features, one FrameworkElement per feature made the visual tree itself the
    /// bottleneck (construction, hit-testing, per-element property updates); a handful of Path
    /// elements renders in a single draw call and keeps pan/zoom/selection responsive.
    /// </summary>
    private void RenderImportedGeometries(IReadOnlyList<SpatialFeature> features)
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
        baseStrokeWidths.Clear();
        shapesByLayer.Clear();
        layerZOrder.Clear();
        hiddenLayers.Clear();
        zOrderCounter = 0;
        importedFeatures.Clear();
        importedFeatures.AddRange(features);
        blockGroups = null;
        selectionHighlight = null;
        selectedFeatureLayer = null;
        selectedFeature = null;

        var width = Math.Max(MapCanvas.ActualWidth, 1);
        var height = Math.Max(MapCanvas.ActualHeight, 1);
        var padding = 28.0;
        var scale = Math.Min(
            (width - padding * 2) / Math.Max(envelope.Width, 1),
            (height - padding * 2) / Math.Max(envelope.Height, 1));

        renderedEnvelope = envelope;
        renderedWidth = width;
        renderedHeight = height;
        renderedPadding = padding;
        renderedScale = scale;

        var polygonContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);
        var lineContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);
        var pointContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);

        (StreamGeometry Geometry, StreamGeometryContext Context) GetContext(Dictionary<string, (StreamGeometry, StreamGeometryContext)> contexts, string layerName)
        {
            if (!contexts.TryGetValue(layerName, out var entry))
            {
                var geometry = new StreamGeometry();
                entry = (geometry, geometry.Open());
                contexts[layerName] = entry;
            }

            return entry;
        }

        foreach (var feature in features)
        {
            var layerName = feature.LayerName ?? "(no layer)";
            switch (feature.Geometry)
            {
                case NtsPolygon polygon:
                    AddPolygonFigure(GetContext(polygonContexts, layerName).Context, polygon, ProjectToScreen);
                    break;
                case LineString line:
                    AddLineFigure(GetContext(lineContexts, layerName).Context, line, ProjectToScreen);
                    break;
                case NetTopologySuite.Geometries.Point point:
                    AddPointFigure(GetContext(pointContexts, layerName).Context, point, ProjectToScreen);
                    break;
            }
        }

        foreach (var (layerName, entry) in polygonContexts)
        {
            entry.Context.Close();
            var color = GetLayerColor(layerName);
            var path = new Path
            {
                Data = entry.Geometry,
                Fill = new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color)
            };
            RegisterLayerPath(path, layerName, 1);
        }

        foreach (var (layerName, entry) in lineContexts)
        {
            entry.Context.Close();
            var color = GetLayerColor(layerName);
            var path = new Path
            {
                Data = entry.Geometry,
                Fill = null,
                Stroke = new SolidColorBrush(color)
            };
            RegisterLayerPath(path, layerName, 1);
        }

        foreach (var (layerName, entry) in pointContexts)
        {
            entry.Context.Close();
            var color = GetLayerColor(layerName);
            var path = new Path
            {
                Data = entry.Geometry,
                Fill = new SolidColorBrush(color),
                Stroke = null
            };
            RegisterLayerPath(path, layerName, 0);
        }

        ResetView();
    }

    private static void AddPolygonFigure(StreamGeometryContext context, NtsPolygon polygon, Func<Coordinate, System.Windows.Point> project)
    {
        AddRingFigure(context, polygon.ExteriorRing, project);
        for (var index = 0; index < polygon.NumInteriorRings; index++)
        {
            AddRingFigure(context, polygon.GetInteriorRingN(index), project);
        }
    }

    private static void AddRingFigure(StreamGeometryContext context, LineString ring, Func<Coordinate, System.Windows.Point> project)
    {
        var points = ring.Coordinates.Select(project).ToList();
        if (points.Count == 0)
        {
            return;
        }

        context.BeginFigure(points[0], true, true);
        context.PolyLineTo(points.Skip(1).ToList(), true, false);
    }

    private static void AddLineFigure(StreamGeometryContext context, LineString line, Func<Coordinate, System.Windows.Point> project)
    {
        var points = line.Coordinates.Select(project).ToList();
        if (points.Count == 0)
        {
            return;
        }

        context.BeginFigure(points[0], false, false);
        context.PolyLineTo(points.Skip(1).ToList(), true, false);
    }

    private const double PointMarkerRadius = 3.0;

    private static void AddPointFigure(StreamGeometryContext context, NetTopologySuite.Geometries.Point point, Func<Coordinate, System.Windows.Point> project)
    {
        var center = project(point.Coordinate);
        const double r = PointMarkerRadius;
        context.BeginFigure(new System.Windows.Point(center.X - r, center.Y), true, true);
        context.PolyLineTo(new List<System.Windows.Point>
        {
            new(center.X, center.Y - r),
            new(center.X + r, center.Y),
            new(center.X, center.Y + r)
        }, true, false);
    }

    private void RegisterLayerPath(Path path, string layerName, double baseStrokeWidth)
    {
        if (baseStrokeWidth > 0)
        {
            baseStrokeWidths[path] = baseStrokeWidth;
            path.StrokeThickness = baseStrokeWidth / Math.Max(mapScale.ScaleX, 0.1);
        }

        MapLayerCanvas.Children.Add(path);
        layerZOrder[layerName] = zOrderCounter++;
        if (!shapesByLayer.TryGetValue(layerName, out var layerShapes))
        {
            layerShapes = new List<Shape>();
            shapesByLayer[layerName] = layerShapes;
        }

        layerShapes.Add(path);
    }

    /// <summary>
    /// World coordinate to/from the untransformed ("local") coordinate space shared by
    /// MapLayerCanvas and EditableObjectsCanvas, i.e. the space Shape.Points are defined in
    /// before the shared pan/zoom RenderTransform is applied for on-screen display.
    /// </summary>
    private System.Windows.Point ProjectToScreen(Coordinate coordinate) => new(
        renderedPadding + (coordinate.X - renderedEnvelope!.MinX) * renderedScale,
        renderedHeight - renderedPadding - (coordinate.Y - renderedEnvelope.MinY) * renderedScale);

    private Coordinate UnprojectFromScreen(System.Windows.Point local) => new(
        renderedEnvelope!.MinX + (local.X - renderedPadding) / renderedScale,
        renderedEnvelope.MinY + (renderedHeight - renderedPadding - local.Y) / renderedScale);

    private sealed record BlockGroup(Envelope Envelope, SpatialFeature Combined);

    private Dictionary<long, BlockGroup>? blockGroups;

    /// <summary>
    /// Folds the per-line pieces of every block instance (they arrive as separate features that
    /// share the instance id) back into one object: a GeometryCollection of all its parts plus its
    /// overall bounds. Cached until the loaded features change.
    /// </summary>
    private Dictionary<long, BlockGroup> GetBlockGroups()
    {
        if (blockGroups is not null)
        {
            return blockGroups;
        }

        blockGroups = new Dictionary<long, BlockGroup>();
        foreach (var partsOfInstance in importedFeatures.Where(f => f.FeatureType == "block_instance").GroupBy(f => f.Id))
        {
            var parts = partsOfInstance.ToList();
            var geometries = new List<NetTopologySuite.Geometries.Geometry>();
            var envelope = new Envelope();
            foreach (var part in parts)
            {
                geometries.Add(part.Geometry);
                envelope.ExpandToInclude(part.Geometry.EnvelopeInternal);
            }

            var combined = new SpatialFeature(
                parts[0].Id,
                "block_instance",
                parts[0].Geometry.Factory.CreateGeometryCollection(geometries.ToArray()),
                parts[0].Attributes,
                parts[0].LayerName);
            blockGroups[parts[0].Id] = new BlockGroup(envelope, combined);
        }

        return blockGroups;
    }

    private void TrySelectFeatureAt(System.Windows.Point canvasPoint)
    {
        if (renderedEnvelope is null || importedFeatures.Count == 0)
        {
            StatusText.Text = "No imported geometries are loaded yet.";
            return;
        }

        try
        {
            var scaleX = Math.Max(mapScale.ScaleX, 0.0001);
            var scaleY = Math.Max(mapScale.ScaleY, 0.0001);
            var localX = (canvasPoint.X - mapTranslation.X) / scaleX;
            var localY = (canvasPoint.Y - mapTranslation.Y) / scaleY;
            var worldX = renderedEnvelope.MinX + (localX - renderedPadding) / renderedScale;
            var worldY = renderedEnvelope.MinY + (renderedHeight - renderedPadding - localY) / renderedScale;

            const double toleranceScreenPixels = 10.0;
            var toleranceWorld = toleranceScreenPixels / (renderedScale * scaleX);
            var searchEnvelope = new Envelope(worldX - toleranceWorld, worldX + toleranceWorld, worldY - toleranceWorld, worldY + toleranceWorld);
            var clickPoint = new NetTopologySuite.Geometries.Point(new Coordinate(worldX, worldY));

            SpatialFeature? best = null;
            var bestDistance = double.MaxValue;
            var bestZOrder = -1;
            var bestArea = double.MaxValue;

            // A block instance is ONE object made of many lines: hit-test it as a whole (anywhere
            // inside its bounds, or near any of its lines) and select all of its parts together.
            foreach (var group in GetBlockGroups().Values)
            {
                var layerName = group.Combined.LayerName ?? "(no layer)";
                if (hiddenLayers.Contains(layerName) || !group.Envelope.Intersects(searchEnvelope))
                {
                    continue;
                }

                var inside = group.Envelope.Contains(worldX, worldY);
                var distance = inside ? 0 : group.Combined.Geometry.Distance(clickPoint);
                if (distance > toleranceWorld)
                {
                    continue;
                }

                var z = layerZOrder.TryGetValue(layerName, out var groupZ) ? groupZ : -1;
                var area = group.Envelope.Area;
                // Higher layer wins; then closer; among equally close (click inside several
                // overlapping bounds) the smallest block wins so inner blocks stay selectable.
                var isBetterGroup = best is null || z > bestZOrder
                    || (z == bestZOrder && (distance < bestDistance || (distance == bestDistance && area < bestArea)));
                if (isBetterGroup)
                {
                    best = group.Combined;
                    bestDistance = distance;
                    bestZOrder = z;
                    bestArea = area;
                }
            }

            foreach (var feature in importedFeatures)
            {
                if (feature.FeatureType == "block_instance")
                {
                    continue;
                }

                var layerName = feature.LayerName ?? "(no layer)";
                if (hiddenLayers.Contains(layerName))
                {
                    continue;
                }

                if (!feature.Geometry.EnvelopeInternal.Intersects(searchEnvelope))
                {
                    continue;
                }

                var distance = feature.Geometry.Distance(clickPoint);
                if (distance > toleranceWorld)
                {
                    continue;
                }

                var z = layerZOrder.TryGetValue(layerName, out var zValue) ? zValue : -1;
                var isBetter = best is null || z > bestZOrder || (z == bestZOrder && distance < bestDistance);
                if (isBetter)
                {
                    best = feature;
                    bestDistance = distance;
                    bestZOrder = z;
                    bestArea = double.MaxValue;
                }
            }

            if (best is not null)
            {
                SelectFeature(best);
                StatusText.Text = $"Selected feature #{best.Id} ({best.Geometry.GeometryType}).";
            }
            else
            {
                ClearSelectionHighlight();
                SelectedObjectText.Text = "No object selected";
                ClearPropertyDetails();
                StatusText.Text = "No feature found near the clicked point. Click closer to a line, fill, or point.";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Selection failed: {exception.Message}";
        }
    }

    private void SelectFeature(SpatialFeature feature)
    {
        SelectedObjectText.Text = $"Feature #{feature.Id}";
        PropertySummaryText.Text = $"Type: {feature.FeatureType}\nGeometry: {feature.Geometry.GeometryType}\nSRID: {feature.Geometry.SRID}\nValid: {feature.Geometry.IsValid}";
        PropertyHintText.Text = string.Empty;
        AttributesGrid.ItemsSource = feature.Attributes.Select(attribute => new AttributeRow(attribute.Key, attribute.Value)).ToArray();
        selectedFeatureLayer = feature.LayerName ?? "(no layer)";
        selectedFeature = feature;
        HighlightFeature(feature);
    }

    private void HighlightFeature(SpatialFeature feature)
    {
        ClearSelectionHighlight();
        if (renderedEnvelope is null)
        {
            return;
        }

        Shape? highlight = feature.Geometry switch
        {
            // A whole block object: outline every one of its parts, not just a single line.
            NetTopologySuite.Geometries.GeometryCollection collection => new Path
            {
                Data = BuildHighlightGeometry(collection),
                Stroke = Brushes.Gold,
                Fill = Brushes.Transparent
            },
            NtsPolygon polygon => new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection(polygon.ExteriorRing.Coordinates.Select(ProjectToScreen)),
                Stroke = Brushes.Gold,
                Fill = Brushes.Transparent
            },
            LineString line => new Polyline
            {
                Points = new PointCollection(line.Coordinates.Select(ProjectToScreen)),
                Stroke = Brushes.Gold,
                Fill = Brushes.Transparent
            },
            NetTopologySuite.Geometries.Point point => CreateHighlightMarker(ProjectToScreen(point.Coordinate)),
            _ => null
        };

        if (highlight is null)
        {
            return;
        }

        highlight.IsHitTestVisible = false;
        if (highlight is Ellipse)
        {
            highlight.StrokeThickness = 3;
        }
        else
        {
            baseStrokeWidths[highlight] = 4;
            highlight.StrokeThickness = 4 / Math.Max(mapScale.ScaleX, 0.1);
        }

        MapLayerCanvas.Children.Add(highlight);
        selectionHighlight = highlight;
    }

    private StreamGeometry BuildHighlightGeometry(NetTopologySuite.Geometries.GeometryCollection collection)
    {
        var streamGeometry = new StreamGeometry();
        using (var context = streamGeometry.Open())
        {
            foreach (var part in collection.Geometries)
            {
                switch (part)
                {
                    case NtsPolygon polygon:
                        AddPolygonFigure(context, polygon, ProjectToScreen);
                        break;
                    case LineString line:
                        AddLineFigure(context, line, ProjectToScreen);
                        break;
                    case NetTopologySuite.Geometries.Point point:
                        AddPointFigure(context, point, ProjectToScreen);
                        break;
                }
            }
        }

        streamGeometry.Freeze();
        return streamGeometry;
    }

    private static Shape CreateHighlightMarker(System.Windows.Point center)
    {
        const double r = 7;
        var ellipse = new Ellipse { Width = r * 2, Height = r * 2, Stroke = Brushes.Gold, Fill = Brushes.Transparent };
        Canvas.SetLeft(ellipse, center.X - r);
        Canvas.SetTop(ellipse, center.Y - r);
        return ellipse;
    }

    private void ClearSelectionHighlight()
    {
        if (selectionHighlight is null)
        {
            return;
        }

        baseStrokeWidths.Remove(selectionHighlight);
        MapLayerCanvas.Children.Remove(selectionHighlight);
        selectionHighlight = null;
        selectedFeatureLayer = null;
        selectedFeature = null;
    }

    private void PopulateLayerPanel(IReadOnlyList<LayerInfo> layers)
    {
        LayersPanel.Children.Clear();
        layerSwatchesByName.Clear();
        layerRowsByName.Clear();
        selectedLayerName = null;
        foreach (var layer in layers)
        {
            var color = GetLayerColor(layer.LayerName);
            var swatch = new Rectangle
            {
                Width = 14,
                Height = 14,
                Fill = new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B)),
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 1.5,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = layer.LayerName,
                ToolTip = "Click to edit this layer"
            };
            swatch.MouseLeftButtonDown += LayerSwatch_MouseLeftButtonDown;
            layerSwatchesByName[layer.LayerName] = swatch;

            var checkBox = new CheckBox
            {
                IsChecked = true,
                Tag = layer.LayerName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Show/hide this layer"
            };
            checkBox.Checked += LayerVisibilityChanged;
            checkBox.Unchecked += LayerVisibilityChanged;

            var nameText = new TextBlock
            {
                Text = $"{layer.LayerName} [{layer.GeometryTypes}] | Blocks: {layer.InstanceCount} | Entities: {layer.EntityCount}",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = $"Layer: {layer.LayerName}\nGeometry: {layer.GeometryTypes}\nBlock instances: {layer.InstanceCount}\nEntities: {layer.EntityCount}"
            };

            var row = new Border
            {
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { checkBox, swatch, nameText }
                }
            };
            var layerName = layer.LayerName;
            nameText.MouseLeftButtonDown += (_, _) => SelectLayerRow(layerName);
            layerRowsByName[layer.LayerName] = row;

            LayersPanel.Children.Add(row);
        }

        UpdateLayerSwatchEditState();
    }

    /// <summary>
    /// Marks a layer as the panel's "current" selection, highlighted like a ListBox item —
    /// purely a display/reference aid, independent of the swatch's edit-mode toggle.
    /// </summary>
    private void SelectLayerRow(string layerName)
    {
        selectedLayerName = layerName;
        UpdateLayerRowSelectionHighlight();
    }

    private void UpdateLayerRowSelectionHighlight()
    {
        var highlight = new SolidColorBrush(Color.FromRgb(0xDC, 0xE9, 0xFB));
        foreach (var (name, row) in layerRowsByName)
        {
            row.Background = string.Equals(name, selectedLayerName, StringComparison.OrdinalIgnoreCase)
                ? highlight
                : Brushes.Transparent;
        }
    }

    /// <summary>
    /// Dashes the color swatch of whichever layer(s) are currently in edit mode — the one
    /// clicked layer when scoped, or every layer that actually has editable objects when edit
    /// mode was entered unscoped (the global "Enter edit mode" button).
    /// </summary>
    private void UpdateLayerSwatchEditState()
    {
        var activeLayerNames = isEditMode
            ? (activeEditLayerName is not null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { activeEditLayerName }
                : editableSourcesById.Values.Select(o => o.LayerName ?? "(no layer)").ToHashSet(StringComparer.OrdinalIgnoreCase))
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, swatch) in layerSwatchesByName)
        {
            var isActive = activeLayerNames.Contains(name);
            swatch.StrokeDashArray = isActive ? new DoubleCollection { 3, 2 } : null;
            swatch.StrokeThickness = isActive ? 2.5 : 1.5;
        }
    }

    private void LayerVisibilityChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.Tag is not string layerName)
        {
            return;
        }

        var isVisible = checkBox.IsChecked == true;
        if (isVisible)
        {
            hiddenLayers.Remove(layerName);
        }
        else
        {
            hiddenLayers.Add(layerName);
            if (selectionHighlight is not null && string.Equals(selectedFeatureLayer, layerName, StringComparison.OrdinalIgnoreCase))
            {
                ClearSelectionHighlight();
                SelectedObjectText.Text = "No object selected";
                ClearPropertyDetails();
            }
        }

        if (!shapesByLayer.TryGetValue(layerName, out var layerShapes))
        {
            return;
        }

        var visibility = isVisible && !editMaskedLayers.Contains(layerName) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var shape in layerShapes)
        {
            shape.Visibility = visibility;
        }
    }

    // Layers whose base-map drawing is hidden because their objects are drawn by the edit overlay.
    private readonly HashSet<string> editMaskedLayers = new(StringComparer.OrdinalIgnoreCase);

    private void SetBaseLayerVisibility(string layerName, bool visible)
    {
        if (!shapesByLayer.TryGetValue(layerName, out var layerShapes))
        {
            return;
        }

        var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        foreach (var shape in layerShapes)
        {
            shape.Visibility = visibility;
        }
    }

    /// <summary>
    /// Hides the base-map drawing of every layer that has editable objects. Without this the
    /// unmoved original stays visible underneath the overlay, so a moved/rotated object looks like
    /// it left a copy behind.
    /// </summary>
    private void ApplyEditMask(IEnumerable<string> layerNames)
    {
        ClearEditMask();
        foreach (var layerName in layerNames)
        {
            editMaskedLayers.Add(layerName);
            SetBaseLayerVisibility(layerName, visible: false);
        }
    }

    private void ClearEditMask()
    {
        foreach (var layerName in editMaskedLayers)
        {
            SetBaseLayerVisibility(layerName, visible: !hiddenLayers.Contains(layerName));
        }

        editMaskedLayers.Clear();
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
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            if (isEditMode)
            {
                // Dragging on empty space draws a selection box (Ctrl adds to the selection).
                var additive = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
                BeginBoxSelect(e.GetPosition(EditableObjectsCanvas), additive);
                return;
            }

            TrySelectFeatureAt(e.GetPosition(MapCanvas));
            return;
        }

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
        if (editDragKind != EditDragKind.None)
        {
            UpdateEditDragPreview(e.GetPosition(EditableObjectsCanvas));
            return;
        }

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
        if (editDragKind != EditDragKind.None)
        {
            FinalizeEditDrag(e.GetPosition(EditableObjectsCanvas));
            return;
        }

        if (e.ChangedButton != System.Windows.Input.MouseButton.Middle)
        {
            return;
        }

        isPanning = false;
        MapCanvas.ReleaseMouseCapture();
        MapCanvas.Cursor = System.Windows.Input.Cursors.Arrow;
    }

    private EditableLayerObject GetEditableSource(long id) =>
        pendingEdits.TryGetValue(id, out var edit) ? edit.Source : editableSourcesById[id];

    private bool HasPendingEdits() =>
        pendingEdits.Values.Any(edit => edit.IsNew || edit.IsDeleted || edit.HasChanges);

    private async void ToggleEditMode_Click(object sender, RoutedEventArgs e)
    {
        if (isEditMode)
        {
            if (HasPendingEdits())
            {
                var answer = MessageBox.Show(
                    "Discard unsaved edits and exit edit mode?",
                    "Exit edit mode",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            ExitEditMode();
            return;
        }

        await EnterEditModeForLayerAsync(null);
    }

    /// <summary>
    /// Clicking a layer's color swatch is the entry point for editing just that layer: it
    /// enters edit mode scoped to the clicked layer, switches scope if a different layer was
    /// being edited (confirming discard first), or exits if the same layer's swatch is clicked
    /// again. The active layer's swatch renders dashed via <see cref="UpdateLayerSwatchEditState"/>.
    /// </summary>
    private async void LayerSwatch_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Rectangle swatch || swatch.Tag is not string layerName)
        {
            return;
        }

        e.Handled = true;

        if (isEditMode && string.Equals(activeEditLayerName, layerName, StringComparison.OrdinalIgnoreCase))
        {
            if (HasPendingEdits())
            {
                var answer = MessageBox.Show(
                    "Discard unsaved edits and exit edit mode?",
                    "Exit edit mode",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            ExitEditMode();
            return;
        }

        if (isEditMode && HasPendingEdits())
        {
            var answer = MessageBox.Show(
                "Switching the edited layer will discard unsaved edits. Continue?",
                "Switch edited layer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await EnterEditModeForLayerAsync(layerName);
    }

    private async Task EnterEditModeForLayerAsync(string? layerName)
    {
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            ShowBusy("Loading editable objects...");
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            // Ensures spatial_block_instances.updated_at (added for block-instance editing) exists
            // even if the database was never re-imported/re-deleted since that column was added —
            // otherwise FindEditableBlockInstancesAsync below fails and edit mode silently no-ops.
            await repository.EnsureSchemaAsync();
            var objects = await repository.FindEditableLayerObjectsAsync();
            var blockInstances = await repository.FindEditableBlockInstancesAsync();
            var instanceLayers = blockInstances
                .Select(instance => instance.LayerName ?? "(no layer)")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // The "Edit" toolbar button edits every layer at once, but it must stick to what is
            // actually on screen — otherwise a layer the user hid or never loaded (e.g. via the
            // layer-selection dialog) would silently reappear as editable shapes the moment edit
            // mode is entered.
            bool IsInScope(string objLayerName)
            {
                if (layerName is not null)
                {
                    return string.Equals(objLayerName, layerName, StringComparison.OrdinalIgnoreCase);
                }

                if (activeLayerFilter is { Count: > 0 } && !activeLayerFilter.Contains(objLayerName, StringComparer.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !hiddenLayers.Contains(objLayerName);
            }

            editableSourcesById.Clear();
            foreach (var obj in objects.Where(o => !instanceLayers.Contains(o.LayerName ?? "(no layer)")))
            {
                if (IsInScope(obj.LayerName ?? "(no layer)"))
                {
                    editableSourcesById[obj.Id] = obj;
                }
            }

            // Block instances (e.g. Equipment) carry both their real drawn shape (Geometry) and a
            // bounding outline (OuterGeometry) in the same row; routing them through the same
            // pending-edit pipeline as spatial_features objects means move/rotate transforms both
            // together automatically. Ids are tagged with a high bit so they can share the same
            // long-keyed dictionaries as spatial_features ids without colliding, and are decoded
            // back only when building the save statements.
            foreach (var obj in blockInstances)
            {
                if (!IsInScope(obj.LayerName ?? "(no layer)"))
                {
                    continue;
                }

                var encoded = obj with { Id = EncodeBlockInstanceId(obj.Id) };
                editableSourcesById[encoded.Id] = encoded;
            }

            pendingEdits.Clear();
            ApplyEditMask(editableSourcesById.Values.Select(source => source.LayerName ?? "(no layer)").Distinct(StringComparer.OrdinalIgnoreCase));
            selectedEditId = null;
            selectedEditIds.Clear();
            GroupButton.IsEnabled = false;
            isVertexEditMode = false;
            ClearVertexHandles();
            VertexEditButton.Content = "Vtx: Off";
            VertexEditButton.IsEnabled = false;
            DuplicateButton.IsEnabled = false;
            DeleteObjectButton.IsEnabled = false;
            activeEditLayerName = layerName;
            isEditMode = true;
            EditModeButton.Content = "Exit Edit";
            ImportButton.IsEnabled = false;
            DeleteImportButton.IsEnabled = false;
            SaveEditsButton.IsEnabled = true;
            DiscardEditsButton.IsEnabled = true;
            RenderEditableLayerObjects();
            UpdateLayerSwatchEditState();
            EditStatusText.Text = layerName is null
                ? $"Edit mode: {editableSourcesById.Count} polygon object(s) across all layers. Drag a shape to move it, drag its handle to rotate it."
                : $"Edit mode: layer '{layerName}' ({editableSourcesById.Count} object(s)). Drag a shape to move it, drag its handle to rotate it.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not enter edit mode: {exception.Message}";
        }
        finally
        {
            HideBusy();
        }
    }

    private void ExitEditMode()
    {
        ClearEditMask();
        isEditMode = false;
        isVertexEditMode = false;
        draggingVertex = null;
        editDragKind = EditDragKind.None;
        editShapeSnapshot.Clear();
        editHandleSnapshot = null;
        RemoveRotateHandle();
        ClearVertexHandles();
        EditableObjectsCanvas.Children.Clear();
        editableShapesById.Clear();
        editableDetailPathById.Clear();
        pendingEdits.Clear();
        editableSourcesById.Clear();
        selectedEditId = null;
        selectedEditIds.Clear();
        boxSelectRectangle = null;
        GroupButton.IsEnabled = false;
        EditModeButton.Content = "Edit";
        ImportButton.IsEnabled = true;
        DeleteImportButton.IsEnabled = true;
        DuplicateButton.IsEnabled = false;
        DeleteObjectButton.IsEnabled = false;
        VertexEditButton.Content = "Vtx: Off";
        VertexEditButton.IsEnabled = false;
        SaveEditsButton.IsEnabled = false;
        DiscardEditsButton.IsEnabled = false;
        EditStatusText.Text = string.Empty;
        SelectedObjectText.Text = "No object selected";
        ClearPropertyDetails();
        activeEditLayerName = null;
        UpdateLayerSwatchEditState();
    }

    private void RenderEditableLayerObjects()
    {
        EditableObjectsCanvas.Children.Clear();
        editableShapesById.Clear();
        editableDetailPathById.Clear();
        RemoveRotateHandle();

        foreach (var id in editableSourcesById.Keys.Union(pendingEdits.Keys).ToArray())
        {
            if (pendingEdits.TryGetValue(id, out var edit) && edit.IsDeleted)
            {
                continue;
            }

            DrawEditableObject(id);
        }

        // Drop selected objects that no longer exist (deleted/regrouped), then restore the rest.
        selectedEditIds.RemoveWhere(selectedId => !editableShapesById.ContainsKey(selectedId));
        if (selectedEditId is long selected && editableShapesById.ContainsKey(selected))
        {
            SelectEditableObject(selected, keepGroup: true);
        }
        else if (selectedEditIds.Count > 0)
        {
            SelectEditableObject(selectedEditIds.First(), keepGroup: true);
        }
    }

    private void DrawEditableObject(long id)
    {
        pendingEdits.TryGetValue(id, out var edit);
        var source = edit?.Source ?? editableSourcesById[id];
        var outer = edit?.CurrentOuterGeometry ?? source.OuterGeometry;
        var isNew = edit is { IsNew: true };
        var isSelected = selectedEditIds.Contains(id);

        // The editable object draws its own full shape: the base map layer is hidden while the
        // layer is being edited (see ApplyEditMask), so the object is shown exactly once, at its
        // current (possibly moved/rotated) position.
        var currentGeometry = edit?.CurrentGeometry ?? source.Geometry;
        var detailPath = BuildDetailPath(currentGeometry, source.LayerName);
        if (detailPath is not null)
        {
            EditableObjectsCanvas.Children.Add(detailPath);
            editableDetailPathById[id] = detailPath;
        }

        // Block instances (comb/gear-style equipment especially) often polygonize into several
        // small disjoint closed regions rather than one clean outline — e.g. each tooth of a comb
        // closes into its own tiny polygon — which would otherwise scatter the click target and
        // selection highlight across many small pieces instead of covering the whole object. Using
        // the bounding envelope as the interactive/selection shape for these keeps one clean,
        // fully-covering hit target; the real outline is still visible via the detail path drawn
        // above.
        // The polygonized outline (outer_geom) often covers only the closed regions it found, so a
        // block's boundary is the bounds of ALL its parts (geom) plus the outline, which always
        // encloses the whole object.
        var ringCoordinateSets = source.FeatureType == "block_instance"
            ? new[] { EnvelopeRingCoordinates(BlockBounds(edit?.CurrentGeometry ?? source.Geometry, outer)) }
            : ExtractExteriorRings(outer).Select(ring => ring.Coordinates);

        var shapes = new List<System.Windows.Shapes.Polygon>();
        foreach (var ringCoordinates in ringCoordinateSets)
        {
            var shape = new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection(ringCoordinates.Select(ProjectToScreen)),
                Fill = Brushes.Transparent,
                Tag = id,
                Cursor = System.Windows.Input.Cursors.SizeAll
            };
            ApplyEditableShapeStyle(shape, isSelected, isNew);
            shape.MouseLeftButtonDown += EditableShape_MouseLeftButtonDown;
            EditableObjectsCanvas.Children.Add(shape);
            shapes.Add(shape);
        }

        editableShapesById[id] = shapes;
    }

    private static Envelope BlockBounds(NetTopologySuite.Geometries.Geometry geometry, NetTopologySuite.Geometries.Geometry outer)
    {
        var bounds = new Envelope(geometry.EnvelopeInternal);
        bounds.ExpandToInclude(outer.EnvelopeInternal);
        return bounds;
    }

    private static Coordinate[] EnvelopeRingCoordinates(Envelope envelope) => new[]
    {
        new Coordinate(envelope.MinX, envelope.MinY),
        new Coordinate(envelope.MaxX, envelope.MinY),
        new Coordinate(envelope.MaxX, envelope.MaxY),
        new Coordinate(envelope.MinX, envelope.MaxY),
        new Coordinate(envelope.MinX, envelope.MinY)
    };

    /// <summary>
    /// Selected (actively edited) objects render dashed and gold; unsaved new objects render
    /// dashed and green; everything else is a solid blue outline.
    /// </summary>
    private static void ApplyEditableShapeStyle(Shape shape, bool isSelected, bool isNew)
    {
        shape.Stroke = isSelected ? Brushes.Gold : (isNew ? Brushes.MediumSeaGreen : Brushes.DodgerBlue);
        shape.StrokeThickness = isSelected ? 2 : 1;
        // Every editable outline is dashed; color tells the state apart (gold selected, green new).
        shape.StrokeDashArray = isSelected
            ? new DoubleCollection { 4, 2 }
            : new DoubleCollection { 3, 2 };
    }

    /// <summary>
    /// Renders a block instance's actual drawn shape (its <c>geom</c> column — a GeometryCollection
    /// of the entities that made up the DXF Insert) as a single non-interactive overlay, using the
    /// same per-component figure helpers as the normal map render. Purely visual: selection/drag
    /// hit-testing still goes through the OuterGeometry polygon drawn on top of it.
    /// </summary>
    private Path? BuildDetailPath(NetTopologySuite.Geometries.Geometry geometry, string? layerName)
    {
        var parts = geometry is NetTopologySuite.Geometries.GeometryCollection collection ? collection.Geometries : new[] { geometry };
        if (parts.Length == 0)
        {
            return null;
        }

        var streamGeometry = new StreamGeometry();
        using (var context = streamGeometry.Open())
        {
            foreach (var part in parts)
            {
                switch (part)
                {
                    case NtsPolygon polygon:
                        AddPolygonFigure(context, polygon, ProjectToScreen);
                        break;
                    case LineString line:
                        AddLineFigure(context, line, ProjectToScreen);
                        break;
                    case NetTopologySuite.Geometries.Point point:
                        AddPointFigure(context, point, ProjectToScreen);
                        break;
                }
            }
        }

        var color = GetLayerColor(layerName ?? "(no layer)");
        return new Path
        {
            Data = streamGeometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1,
            Fill = null,
            IsHitTestVisible = false
        };
    }

    private static IEnumerable<LineString> ExtractExteriorRings(NetTopologySuite.Geometries.Geometry geometry)
    {
        switch (geometry)
        {
            case NtsPolygon polygon:
                yield return polygon.ExteriorRing;
                break;
            case MultiPolygon multiPolygon:
                foreach (var part in multiPolygon.Geometries.OfType<NtsPolygon>())
                {
                    yield return part.ExteriorRing;
                }
                break;
        }
    }

    private void EditableShape_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!isEditMode || sender is not System.Windows.Shapes.Polygon shape || shape.Tag is not long id)
        {
            return;
        }

        e.Handled = true;
        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        // Ctrl+click toggles membership without dragging; a plain click on an already selected
        // object keeps the group so it can be dragged as a whole.
        SelectEditableObject(id, additive: ctrl, keepGroup: true);
        if (!ctrl && !isVertexEditMode)
        {
            BeginMoveDrag(e.GetPosition(EditableObjectsCanvas));
        }
    }

    /// <summary>
    /// The rotate handle is a single click, not a drag: each click turns the selected object by
    /// a fixed 90 degrees clockwise around its bounding-box center. A negative angle passed to
    /// NTS's AffineTransformation.RotationInstance (standard CCW-positive convention in world
    /// space) renders as a clockwise turn once projected to screen (Y is flipped for display).
    /// </summary>
    private void RotateHandle_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var shapes = SelectedShapes();
        if (!isEditMode || selectedEditId is null || shapes.Count == 0)
        {
            return;
        }

        e.Handled = true;

        var allPoints = shapes.SelectMany(s => s.Points).ToArray();
        var centerScreen = new System.Windows.Point(
            (allPoints.Min(p => p.X) + allPoints.Max(p => p.X)) / 2,
            (allPoints.Min(p => p.Y) + allPoints.Max(p => p.Y)) / 2);
        var pivotWorld = UnprojectFromScreen(centerScreen);

        var rotation = AffineTransformation.RotationInstance(-Math.PI / 2, pivotWorld.X, pivotWorld.Y);
        // Every selected object turns about the shared center, so a group rotates rigidly.
        foreach (var selectedId in selectedEditIds.ToArray())
        {
            var edit = GetOrCreateEdit(selectedId);
            edit.CurrentGeometry = rotation.Transform(edit.CurrentGeometry);
            edit.CurrentOuterGeometry = rotation.Transform(edit.CurrentOuterGeometry);
            ApplyPlacementRotation(edit, pivotWorld.X, pivotWorld.Y, -90.0);
            edit.HasChanges = true;
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
    }

    private void SnapToGridCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        isGridSnapEnabled = SnapToGridCheckBox.IsChecked == true;
    }

    private void BeginMoveDrag(System.Windows.Point start)
    {
        var shapes = SelectedShapes();
        if (selectedEditId is null || shapes.Count == 0)
        {
            return;
        }

        editDragKind = EditDragKind.Move;
        editDragAnchor = start;
        SnapshotEditShapes(shapes);
        MapCanvas.CaptureMouse();
    }

    private void SnapshotEditShapes(List<System.Windows.Shapes.Polygon> shapes)
    {
        editShapeSnapshot.Clear();
        foreach (var shape in shapes)
        {
            editShapeSnapshot[shape] = shape.Points.Cast<System.Windows.Point>().ToArray();
        }

        editHandleSnapshot = rotateHandle is not null && rotateHandleLine is not null
            ? new[]
            {
                new System.Windows.Point(rotateHandleLine.X1, rotateHandleLine.Y1),
                new System.Windows.Point(rotateHandleLine.X2, rotateHandleLine.Y2)
            }
            : null;
    }

    private void UpdateEditDragPreview(System.Windows.Point current)
    {
        if (editDragKind == EditDragKind.Move)
        {
            var (screenDx, screenDy, _, _) = ComputeMoveDelta(current);
            foreach (var (shape, snapshot) in editShapeSnapshot)
            {
                shape.Points = new PointCollection(snapshot.Select(p => new System.Windows.Point(p.X + screenDx, p.Y + screenDy)));
            }

            MoveHandle(new Vector(screenDx, screenDy));

            foreach (var movingId in selectedEditIds)
            {
                if (editableDetailPathById.TryGetValue(movingId, out var detailPath))
                {
                    detailPath.RenderTransform = new TranslateTransform(screenDx, screenDy);
                }
            }

            return;
        }

        if (editDragKind == EditDragKind.Box && boxSelectRectangle is not null)
        {
            Canvas.SetLeft(boxSelectRectangle, Math.Min(boxSelectStart.X, current.X));
            Canvas.SetTop(boxSelectRectangle, Math.Min(boxSelectStart.Y, current.Y));
            boxSelectRectangle.Width = Math.Abs(current.X - boxSelectStart.X);
            boxSelectRectangle.Height = Math.Abs(current.Y - boxSelectStart.Y);
            return;
        }

        if (editDragKind == EditDragKind.Vertex)
        {
            UpdateVertexDragPreview(current);
        }
    }

    /// <summary>
    /// Converts the raw drag distance to a screen-space delta (for the live shape preview) and a
    /// world-space delta (for the geometry translation applied on drop). When grid snapping is on,
    /// both are adjusted so the moved object's outline centroid lands on a <see cref="GridSnapSizeWorldUnits"/>
    /// grid intersection, computed from the object's pre-drag position so repeated small mouse
    /// moves don't accumulate rounding error.
    /// </summary>
    private (double ScreenDx, double ScreenDy, double WorldDx, double WorldDy) ComputeMoveDelta(System.Windows.Point current)
    {
        var rawDelta = current - editDragAnchor;
        var worldDx = rawDelta.X / renderedScale;
        var worldDy = -rawDelta.Y / renderedScale;

        if (isGridSnapEnabled && selectedEditId is long id)
        {
            var baseline = pendingEdits.TryGetValue(id, out var edit) ? edit.CurrentOuterGeometry : GetEditableSource(id).OuterGeometry;
            var centroid = baseline.Centroid;
            var movedX = centroid.X + worldDx;
            var movedY = centroid.Y + worldDy;
            var snappedX = Math.Round(movedX / GridSnapSizeWorldUnits) * GridSnapSizeWorldUnits;
            var snappedY = Math.Round(movedY / GridSnapSizeWorldUnits) * GridSnapSizeWorldUnits;
            worldDx = snappedX - centroid.X;
            worldDy = snappedY - centroid.Y;
        }

        var screenDx = worldDx * renderedScale;
        var screenDy = -worldDy * renderedScale;
        return (screenDx, screenDy, worldDx, worldDy);
    }

    private void MoveHandle(Vector delta)
    {
        if (rotateHandle is null || rotateHandleLine is null || editHandleSnapshot is not { Length: 2 } handleSnapshot)
        {
            return;
        }

        rotateHandleLine.X1 = handleSnapshot[0].X + delta.X;
        rotateHandleLine.Y1 = handleSnapshot[0].Y + delta.Y;
        rotateHandleLine.X2 = handleSnapshot[1].X + delta.X;
        rotateHandleLine.Y2 = handleSnapshot[1].Y + delta.Y;
        Canvas.SetLeft(rotateHandle, handleSnapshot[1].X + delta.X - 6);
        Canvas.SetTop(rotateHandle, handleSnapshot[1].Y + delta.Y - 6);
    }

    private void FinalizeEditDrag(System.Windows.Point current)
    {
        var kind = editDragKind;
        editDragKind = EditDragKind.None;
        MapCanvas.ReleaseMouseCapture();
        editShapeSnapshot.Clear();
        editHandleSnapshot = null;

        if (kind == EditDragKind.Box)
        {
            FinalizeBoxSelect(current);
            return;
        }

        if (selectedEditId is not long id)
        {
            return;
        }

        var edit = GetOrCreateEdit(id);

        if (kind == EditDragKind.Move)
        {
            var (_, _, worldDx, worldDy) = ComputeMoveDelta(current);
            if (Math.Abs(worldDx) > 1e-9 || Math.Abs(worldDy) > 1e-9)
            {
                var translation = AffineTransformation.TranslationInstance(worldDx, worldDy);
                foreach (var selectedId in selectedEditIds.ToArray())
                {
                    var moved = GetOrCreateEdit(selectedId);
                    moved.CurrentGeometry = translation.Transform(moved.CurrentGeometry);
                    moved.CurrentOuterGeometry = translation.Transform(moved.CurrentOuterGeometry);
                    ApplyPlacementMove(moved, worldDx, worldDy);
                    moved.HasChanges = true;
                }
            }
        }
        else if (kind == EditDragKind.Vertex)
        {
            FinalizeVertexDrag(edit, current);
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
    }

    private void BeginBoxSelect(System.Windows.Point start, bool additive)
    {
        boxSelectStart = start;
        boxSelectAdditive = additive;
        boxSelectRectangle = new System.Windows.Shapes.Rectangle
        {
            Stroke = Brushes.DodgerBlue,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(40, 30, 144, 255)),
            IsHitTestVisible = false,
            Width = 0,
            Height = 0
        };
        Canvas.SetLeft(boxSelectRectangle, start.X);
        Canvas.SetTop(boxSelectRectangle, start.Y);
        EditableObjectsCanvas.Children.Add(boxSelectRectangle);
        editDragKind = EditDragKind.Box;
        MapCanvas.CaptureMouse();
    }

    /// <summary>
    /// Window selection: an object is picked only when every one of its outline points lies inside
    /// the rubber band, so a huge layer outline is not grabbed just because the box overlaps it.
    /// </summary>
    private void FinalizeBoxSelect(System.Windows.Point current)
    {
        if (boxSelectRectangle is not null)
        {
            EditableObjectsCanvas.Children.Remove(boxSelectRectangle);
            boxSelectRectangle = null;
        }

        var rect = new Rect(boxSelectStart, current);
        if (rect.Width < 3 && rect.Height < 3)
        {
            if (!boxSelectAdditive)
            {
                DeselectEditableObject();
            }

            return;
        }

        var picked = editableShapesById
            .Where(pair => pair.Value.Count > 0 && pair.Value.All(shape => shape.Points.All(rect.Contains)))
            .Select(pair => pair.Key)
            .ToList();
        if (picked.Count == 0)
        {
            if (!boxSelectAdditive)
            {
                DeselectEditableObject();
            }

            return;
        }

        if (!boxSelectAdditive)
        {
            selectedEditIds.Clear();
        }

        foreach (var pickedId in picked)
        {
            selectedEditIds.Add(pickedId);
        }

        SelectEditableObject(picked[0], keepGroup: true);
    }

    private PendingLayerEdit GetOrCreateEdit(long id)
    {
        if (!pendingEdits.TryGetValue(id, out var edit))
        {
            edit = CreatePendingEdit(GetEditableSource(id));
            pendingEdits[id] = edit;
        }

        return edit;
    }

    /// <summary>
    /// Selects <paramref name="id"/> as the primary object. <paramref name="additive"/> (Ctrl+click)
    /// toggles it in/out of the multi-selection; <paramref name="keepGroup"/> keeps an existing
    /// multi-selection that already contains it (plain click on a selected object, or a re-render).
    /// </summary>
    private void SelectEditableObject(long id, bool additive = false, bool keepGroup = false)
    {
        if (additive)
        {
            if (!selectedEditIds.Remove(id))
            {
                selectedEditIds.Add(id);
            }
            else if (selectedEditIds.Count == 0)
            {
                DeselectEditableObject();
                return;
            }
            else
            {
                // Removed one: keep the old primary unless that is the one that was removed.
                id = selectedEditId is long primary && primary != id ? primary : selectedEditIds.First();
            }
        }
        else if (!(keepGroup && selectedEditIds.Contains(id)))
        {
            selectedEditIds.Clear();
            selectedEditIds.Add(id);
        }

        var isSameSelection = selectedEditId == id && selectedEditIds.Count == 1;
        selectedEditId = id;
        RemoveRotateHandle();

        if (!isSameSelection || selectedEditIds.Count > 1)
        {
            isVertexEditMode = false;
            ClearVertexHandles();
            VertexEditButton.Content = "Vtx: Off";
        }

        foreach (var (shapeId, shapes) in editableShapesById)
        {
            var isSelected = selectedEditIds.Contains(shapeId);
            var isNew = pendingEdits.TryGetValue(shapeId, out var shapeEdit) && shapeEdit.IsNew;
            foreach (var shape in shapes)
            {
                ApplyEditableShapeStyle(shape, isSelected, isNew);
            }
        }

        if (isVertexEditMode)
        {
            RenderVertexHandles(id);
        }
        else
        {
            CreateRotateHandle(id);
        }

        var source = GetEditableSource(id);
        var isNewObject = pendingEdits.TryGetValue(id, out var edit) && edit.IsNew;
        SelectedObjectText.Text = $"Editing feature #{id}{(isNewObject ? " (new, unsaved)" : string.Empty)}";
        PropertySummaryText.Text = $"Type: {source.FeatureType}\nLayer: {source.LayerName}";
        var isBlock = source.FeatureType == "block_instance";
        var isMulti = selectedEditIds.Count > 1;
        if (isMulti)
        {
            SelectedObjectText.Text = $"{selectedEditIds.Count} objects selected (primary #{id})";
        }

        PropertyHintText.Text = isMulti
            ? "Multi-selection: drag any selected shape to move all, click the gold handle to rotate all 90° clockwise.\nCtrl+click toggles an object, drag on empty space to box-select.\nGroup turns the selection into one block."
            : isBlock
                ? "Block instance: moves, rotates, duplicates and deletes as one object.\nDrag the shape to move it.\nClick the gold handle to rotate 90° clockwise."
                : "Drag the shape to move it.\nClick the gold handle to rotate 90° clockwise.\nToggle Vertex edit to reshape the outline.";
        var shownAttributes = pendingEdits.TryGetValue(id, out var attributeEdit) ? attributeEdit.Attributes : source.Attributes;
        AttributesGrid.ItemsSource = shownAttributes.Select(attribute => new AttributeRow(attribute.Key, attribute.Value)).ToArray();
        DuplicateButton.IsEnabled = true;
        DeleteObjectButton.IsEnabled = true;
        GroupButton.IsEnabled = isMulti;
        // Reshaping only the outline would desynchronize it from the block's real geometry.
        VertexEditButton.IsEnabled = !isBlock && !isMulti;
        VertexEditButton.ToolTip = isBlock ? "Vertex edit is not available for block instances" : "Vertex edit";
    }

    private void DeselectEditableObject()
    {
        if (selectedEditId is null)
        {
            return;
        }

        selectedEditId = null;
        selectedEditIds.Clear();
        RemoveRotateHandle();
        isVertexEditMode = false;
        ClearVertexHandles();
        VertexEditButton.Content = "Vtx: Off";
        VertexEditButton.IsEnabled = false;
        DuplicateButton.IsEnabled = false;
        DeleteObjectButton.IsEnabled = false;
        GroupButton.IsEnabled = false;
        SelectedObjectText.Text = "No object selected";
        ClearPropertyDetails();

        foreach (var (shapeId, shapes) in editableShapesById)
        {
            var isNew = pendingEdits.TryGetValue(shapeId, out var edit) && edit.IsNew;
            foreach (var shape in shapes)
            {
                ApplyEditableShapeStyle(shape, isSelected: false, isNew);
            }
        }
    }

    private List<System.Windows.Shapes.Polygon> SelectedShapes() =>
        selectedEditIds.SelectMany(id => editableShapesById.TryGetValue(id, out var s) ? s : new()).ToList();

    private void CreateRotateHandle(long id)
    {
        // The handle sits above the combined bounds of the whole selection.
        var shapes = SelectedShapes();
        if (shapes.Count == 0)
        {
            return;
        }

        var allPoints = shapes.SelectMany(shape => shape.Points).ToArray();
        var topCenter = new System.Windows.Point((allPoints.Min(p => p.X) + allPoints.Max(p => p.X)) / 2, allPoints.Min(p => p.Y));
        var handleCenter = new System.Windows.Point(topCenter.X, topCenter.Y - 34);

        rotateHandleLine = new Line
        {
            X1 = topCenter.X,
            Y1 = topCenter.Y,
            X2 = handleCenter.X,
            Y2 = handleCenter.Y,
            Stroke = Brushes.Gold,
            StrokeThickness = 1.5,
            IsHitTestVisible = false
        };
        rotateHandle = new Ellipse
        {
            Width = 12,
            Height = 12,
            Fill = Brushes.Gold,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Rotate 90° clockwise"
        };
        Canvas.SetLeft(rotateHandle, handleCenter.X - 6);
        Canvas.SetTop(rotateHandle, handleCenter.Y - 6);
        rotateHandle.MouseLeftButtonDown += RotateHandle_MouseLeftButtonDown;

        EditableObjectsCanvas.Children.Add(rotateHandleLine);
        EditableObjectsCanvas.Children.Add(rotateHandle);
    }

    private void RemoveRotateHandle()
    {
        if (rotateHandle is not null)
        {
            EditableObjectsCanvas.Children.Remove(rotateHandle);
            rotateHandle = null;
        }

        if (rotateHandleLine is not null)
        {
            EditableObjectsCanvas.Children.Remove(rotateHandleLine);
            rotateHandleLine = null;
        }
    }

    private void ToggleVertexEdit_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEditId is not long id || GetEditableSource(id).FeatureType == "block_instance")
        {
            return;
        }

        isVertexEditMode = !isVertexEditMode;
        VertexEditButton.Content = isVertexEditMode ? "Vtx: On" : "Vtx: Off";
        if (isVertexEditMode)
        {
            RemoveRotateHandle();
            RenderVertexHandles(id);
            EditStatusText.Text = "Vertex edit: drag a square to move it (snaps near other vertices), click a green dot to insert, right-click a square to delete.";
        }
        else
        {
            ClearVertexHandles();
            CreateRotateHandle(id);
        }
    }

    /// <summary>
    /// Exterior ring vertices of <paramref name="geometry"/> as open (no duplicated closing
    /// point) world-coordinate arrays — one per polygon part.
    /// </summary>
    private static Coordinate[][] GetOpenExteriorRings(NetTopologySuite.Geometries.Geometry geometry) =>
        ExtractExteriorRings(geometry)
            .Select(ring => ring.Coordinates.Length > 1 && ring.Coordinates[0].Equals2D(ring.Coordinates[^1])
                ? ring.Coordinates[..^1]
                : ring.Coordinates)
            .ToArray();

    private static NetTopologySuite.Geometries.Geometry RebuildOuterGeometry(NetTopologySuite.Geometries.Geometry outer, int ringIndex, Coordinate[] openRing)
    {
        var closedRing = openRing.Length > 0 && !openRing[0].Equals2D(openRing[^1])
            ? openRing.Append(openRing[0]).ToArray()
            : openRing;
        var factory = outer.Factory;
        var newPolygon = factory.CreatePolygon(factory.CreateLinearRing(closedRing));

        if (outer is MultiPolygon multiPolygon)
        {
            var polygons = multiPolygon.Geometries.Cast<NtsPolygon>().ToArray();
            polygons[ringIndex] = newPolygon;
            return factory.CreateMultiPolygon(polygons);
        }

        return newPolygon;
    }

    private void RenderVertexHandles(long id)
    {
        ClearVertexHandles();
        if (!pendingEdits.TryGetValue(id, out var edit))
        {
            edit = CreatePendingEdit(GetEditableSource(id));
            pendingEdits[id] = edit;
        }

        var rings = GetOpenExteriorRings(edit.CurrentOuterGeometry);
        for (var ringIndex = 0; ringIndex < rings.Length; ringIndex++)
        {
            var ring = rings[ringIndex];
            for (var pointIndex = 0; pointIndex < ring.Length; pointIndex++)
            {
                var screen = ProjectToScreen(ring[pointIndex]);
                var handle = new System.Windows.Shapes.Rectangle
                {
                    Width = 9,
                    Height = 9,
                    Fill = Brushes.White,
                    Stroke = Brushes.DodgerBlue,
                    StrokeThickness = 1.5,
                    Tag = (ringIndex, pointIndex),
                    Cursor = System.Windows.Input.Cursors.Cross
                };
                Canvas.SetLeft(handle, screen.X - 4.5);
                Canvas.SetTop(handle, screen.Y - 4.5);
                handle.MouseLeftButtonDown += VertexHandle_MouseLeftButtonDown;
                handle.MouseRightButtonDown += VertexHandle_MouseRightButtonDown;
                EditableObjectsCanvas.Children.Add(handle);
                vertexHandles.Add(handle);

                var next = ring[(pointIndex + 1) % ring.Length];
                var midWorld = new Coordinate((ring[pointIndex].X + next.X) / 2, (ring[pointIndex].Y + next.Y) / 2);
                var midScreen = ProjectToScreen(midWorld);
                var midHandle = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = Brushes.MediumSeaGreen,
                    Cursor = System.Windows.Input.Cursors.Cross,
                    Tag = (ringIndex, pointIndex)
                };
                Canvas.SetLeft(midHandle, midScreen.X - 3.5);
                Canvas.SetTop(midHandle, midScreen.Y - 3.5);
                midHandle.MouseLeftButtonDown += MidpointHandle_MouseLeftButtonDown;
                EditableObjectsCanvas.Children.Add(midHandle);
                midpointHandles.Add(midHandle);
            }
        }
    }

    private void ClearVertexHandles()
    {
        foreach (var handle in vertexHandles)
        {
            EditableObjectsCanvas.Children.Remove(handle);
        }

        vertexHandles.Clear();

        foreach (var handle in midpointHandles)
        {
            EditableObjectsCanvas.Children.Remove(handle);
        }

        midpointHandles.Clear();
    }

    private void VertexHandle_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!isEditMode || !isVertexEditMode || selectedEditId is not long ||
            sender is not System.Windows.Shapes.Rectangle handle || handle.Tag is not (int ringIndex, int pointIndex))
        {
            return;
        }

        e.Handled = true;
        draggingVertex = (ringIndex, pointIndex);
        editDragKind = EditDragKind.Vertex;
        editDragAnchor = e.GetPosition(EditableObjectsCanvas);
        MapCanvas.CaptureMouse();
    }

    private void VertexHandle_MouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!isEditMode || !isVertexEditMode || selectedEditId is not long id || !pendingEdits.TryGetValue(id, out var edit) ||
            sender is not System.Windows.Shapes.Rectangle handle || handle.Tag is not (int ringIndex, int pointIndex))
        {
            return;
        }

        e.Handled = true;
        var rings = GetOpenExteriorRings(edit.CurrentOuterGeometry);
        if (ringIndex >= rings.Length || rings[ringIndex].Length <= 3)
        {
            EditStatusText.Text = "Cannot delete: a polygon needs at least 3 vertices.";
            return;
        }

        var newRing = rings[ringIndex].Where((_, index) => index != pointIndex).ToArray();
        edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, newRing);
        edit.HasChanges = true;
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = "Vertex deleted.";
    }

    private void MidpointHandle_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!isEditMode || !isVertexEditMode || selectedEditId is not long id || !pendingEdits.TryGetValue(id, out var edit) ||
            sender is not Ellipse handle || handle.Tag is not (int ringIndex, int afterPointIndex))
        {
            return;
        }

        e.Handled = true;
        var rings = GetOpenExteriorRings(edit.CurrentOuterGeometry);
        if (ringIndex >= rings.Length)
        {
            return;
        }

        var ring = rings[ringIndex];
        var next = ring[(afterPointIndex + 1) % ring.Length];
        var midWorld = new Coordinate((ring[afterPointIndex].X + next.X) / 2, (ring[afterPointIndex].Y + next.Y) / 2);
        var newRing = ring.ToList();
        newRing.Insert(afterPointIndex + 1, midWorld);

        edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, newRing.ToArray());
        edit.HasChanges = true;
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = "Vertex added.";
    }

    private void UpdateVertexDragPreview(System.Windows.Point current)
    {
        if (draggingVertex is not var (ringIndex, pointIndex) || selectedEditId is not long id ||
            !editableShapesById.TryGetValue(id, out var shapes) || ringIndex >= shapes.Count)
        {
            return;
        }

        var snapped = SnapToNearbyVertex(current, id, ringIndex, pointIndex);
        var points = shapes[ringIndex].Points;
        var newPoints = new PointCollection(points);
        newPoints[pointIndex] = snapped;
        if (pointIndex == 0)
        {
            newPoints[newPoints.Count - 1] = snapped;
        }

        shapes[ringIndex].Points = newPoints;
    }

    /// <summary>
    /// Snaps to the nearest other vertex (across every editable object, not just this one)
    /// within <see cref="VertexSnapTolerance"/> local canvas units of <paramref name="current"/>.
    /// The tolerance is in unzoomed local units rather than true screen pixels, so it loosens
    /// as the map is zoomed in and tightens when zoomed out — acceptable for this first pass.
    /// </summary>
    private System.Windows.Point SnapToNearbyVertex(System.Windows.Point current, long excludeId, int excludeRing, int excludePoint)
    {
        System.Windows.Point? best = null;
        var bestDistanceSq = VertexSnapTolerance * VertexSnapTolerance;

        foreach (var (otherId, shapes) in editableShapesById)
        {
            for (var ringIndex = 0; ringIndex < shapes.Count; ringIndex++)
            {
                var points = shapes[ringIndex].Points;
                var lastIndex = points.Count - 1;
                for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
                {
                    var isSameVertex = otherId == excludeId && ringIndex == excludeRing &&
                        (pointIndex == excludePoint || (excludePoint == 0 && pointIndex == lastIndex));
                    if (isSameVertex)
                    {
                        continue;
                    }

                    var candidate = points[pointIndex];
                    var dx = candidate.X - current.X;
                    var dy = candidate.Y - current.Y;
                    var distanceSq = (dx * dx) + (dy * dy);
                    if (distanceSq < bestDistanceSq)
                    {
                        bestDistanceSq = distanceSq;
                        best = candidate;
                    }
                }
            }
        }

        return best ?? current;
    }

    private void FinalizeVertexDrag(PendingLayerEdit edit, System.Windows.Point current)
    {
        if (draggingVertex is not var (ringIndex, pointIndex) || selectedEditId is not long id)
        {
            draggingVertex = null;
            return;
        }

        var snapped = SnapToNearbyVertex(current, id, ringIndex, pointIndex);
        var worldPoint = UnprojectFromScreen(snapped);

        var rings = GetOpenExteriorRings(edit.CurrentOuterGeometry);
        if (ringIndex < rings.Length)
        {
            rings[ringIndex][pointIndex] = worldPoint;
            edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, rings[ringIndex]);
            edit.HasChanges = true;
        }

        draggingVertex = null;
    }

    private void DuplicateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEditIds.Count == 0)
        {
            return;
        }

        var offset = 10.0 / Math.Max(renderedScale, 0.0001);
        var translation = AffineTransformation.TranslationInstance(offset, -offset);
        var newIds = new List<long>();
        foreach (var id in selectedEditIds.ToArray())
        {
            var source = GetEditableSource(id);
            pendingEdits.TryGetValue(id, out var existingEdit);
            var baseGeometry = existingEdit?.CurrentGeometry ?? source.Geometry;
            var baseOuter = existingEdit?.CurrentOuterGeometry ?? source.OuterGeometry;

            var newId = nextTempId--;
            var clone = CreatePendingEdit(source, isNew: true);
            clone.CurrentGeometry = translation.Transform(baseGeometry);
            clone.CurrentOuterGeometry = translation.Transform(baseOuter);
            if (existingEdit is not null)
            {
                clone.Attributes = new Dictionary<string, string?>(existingEdit.Attributes);
            }

            // A copy is a new DXF-less instance: it must not share the original's DXF handle.
            clone.Attributes.Remove("block_handle");
            ApplyPlacementMove(clone, offset, -offset);
            clone.HasChanges = true;
            pendingEdits[newId] = clone;
            newIds.Add(newId);
        }

        // The copies become the selection so the whole set can be dragged into place together.
        selectedEditIds.Clear();
        foreach (var newId in newIds)
        {
            selectedEditIds.Add(newId);
        }

        selectedEditId = newIds[0];
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = newIds.Count == 1
            ? "Duplicated. Drag the new object into place, then Save changes."
            : $"Duplicated {newIds.Count} objects. Drag them into place, then Save changes.";
    }

    /// <summary>
    /// Turns the selected objects into ONE new block instance: its geometry is the union of the
    /// members' geometry, its outline the convex hull of their outlines, and the placement
    /// attributes start at the outline's centroid. The originals are marked for deletion, so after
    /// Save the group moves/rotates/deletes as a single object and gets its own block definition.
    /// </summary>
    private void GroupSelected_Click(object sender, RoutedEventArgs e)
    {
        var ids = selectedEditIds.ToArray();
        if (ids.Length < 2)
        {
            return;
        }

        var edits = ids.Select(GetOrCreateEdit).ToList();
        var factory = edits[0].CurrentGeometry.Factory;

        var parts = new List<NetTopologySuite.Geometries.Geometry>();
        foreach (var member in edits)
        {
            CollectGeometryParts(member.CurrentGeometry, parts);
        }

        var hullSource = factory.CreateGeometryCollection(edits.Select(member => member.CurrentOuterGeometry).ToArray());
        var hull = hullSource.ConvexHull();
        if (hull is not NtsPolygon)
        {
            hull = hullSource.Envelope;
        }

        var first = edits[0].Source;
        var blockName = $"GROUP_{DateTime.Now:yyyyMMddHHmmss}_{Math.Abs(nextTempId)}";
        var centroid = hull.Centroid;
        var attributes = new Dictionary<string, string?>
        {
            ["block_name"] = blockName,
            ["block_insert_x"] = centroid.X.ToString("0.######", Invariant),
            ["block_insert_y"] = centroid.Y.ToString("0.######", Invariant),
            ["block_rotation_deg"] = "0",
            ["block_scale_x"] = "1",
            ["block_scale_y"] = "1",
            ["grouped_member_count"] = edits.Count.ToString(Invariant)
        };

        var groupId = nextTempId--;
        var groupSource = new EditableLayerObject(
            groupId, "block_instance", blockName, first.SourceFile ?? "manual", first.LayerName,
            factory.CreateGeometryCollection(parts.ToArray()), hull, attributes, DateTime.UtcNow);

        pendingEdits[groupId] = new PendingLayerEdit
        {
            Source = groupSource,
            IsNew = true,
            HasChanges = true,
            CurrentGeometry = groupSource.Geometry,
            CurrentOuterGeometry = hull,
            Attributes = attributes
        };

        // Members are consumed by the group: drop unsaved copies, delete saved originals on Save.
        foreach (var id in ids)
        {
            if (pendingEdits.TryGetValue(id, out var member) && member.IsNew)
            {
                pendingEdits.Remove(id);
            }
            else
            {
                GetOrCreateEdit(id).IsDeleted = true;
            }
        }

        selectedEditIds.Clear();
        selectedEditIds.Add(groupId);
        selectedEditId = groupId;
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = $"Grouped {ids.Length} objects into block '{blockName}'. Save changes to apply.";
    }

    private static void CollectGeometryParts(NetTopologySuite.Geometries.Geometry geometry, List<NetTopologySuite.Geometries.Geometry> parts)
    {
        if (geometry is NetTopologySuite.Geometries.GeometryCollection collection)
        {
            foreach (var child in collection.Geometries)
            {
                CollectGeometryParts(child, parts);
            }

            return;
        }

        parts.Add(geometry);
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEditIds.Count == 0)
        {
            return;
        }

        foreach (var id in selectedEditIds.ToArray())
        {
            if (pendingEdits.TryGetValue(id, out var edit) && edit.IsNew)
            {
                pendingEdits.Remove(id);
            }
            else
            {
                GetOrCreateEdit(id).IsDeleted = true;
            }
        }

        DeselectEditableObject();
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = "Marked for deletion. Save changes to apply.";
    }

    private async void SaveEdits_Click(object sender, RoutedEventArgs e)
    {
        var updates = new List<(long Id, NetTopologySuite.Geometries.Geometry Geometry, NetTopologySuite.Geometries.Geometry OuterGeometry, DateTime ExpectedUpdatedAt, bool IsBlockInstance, IReadOnlyDictionary<string, string?>? Attributes)>();
        var inserts = new List<(EditableLayerObject Source, NetTopologySuite.Geometries.Geometry Geometry, NetTopologySuite.Geometries.Geometry OuterGeometry)>();
        var deletes = new List<(long Id, DateTime ExpectedUpdatedAt, bool IsBlockInstance)>();

        foreach (var (id, edit) in pendingEdits)
        {
            if (edit.IsNew)
            {
                if (!edit.IsDeleted)
                {
                    inserts.Add((edit.Source with { Attributes = edit.Attributes }, edit.CurrentGeometry, edit.CurrentOuterGeometry));
                }

                continue;
            }

            var isBlockInstance = IsEncodedBlockInstanceId(id);
            var realId = isBlockInstance ? DecodeBlockInstanceId(id) : id;

            if (edit.IsDeleted)
            {
                deletes.Add((realId, edit.Source.UpdatedAt, isBlockInstance));
                continue;
            }

            if (edit.HasChanges)
            {
                updates.Add((realId, edit.CurrentGeometry, edit.CurrentOuterGeometry, edit.Source.UpdatedAt, isBlockInstance,
                    isBlockInstance ? edit.Attributes : null));
            }
        }

        if (updates.Count == 0 && inserts.Count == 0 && deletes.Count == 0)
        {
            EditStatusText.Text = "No changes to save.";
            return;
        }

        ShowBusy("Saving edits to PostgreSQL...");
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            var conflictIds = await repository.ApplyLayerEditsAsync(updates, inserts, deletes);
            var appliedUpdates = updates.Count - conflictIds.Count(id => updates.Any(u => u.Id == id));
            var appliedDeletes = deletes.Count - conflictIds.Count(id => deletes.Any(d => d.Id == id));
            var summary = $"Saved edits: {appliedUpdates} updated, {inserts.Count} added, {appliedDeletes} deleted.";
            if (conflictIds.Count > 0)
            {
                summary += $" {conflictIds.Count} object(s) were changed by someone else in the meantime and were NOT overwritten (ids: {string.Join(", ", conflictIds)}) — reload shows their latest state.";
            }

            ExitEditMode();
            StatusText.Text = summary;
            await LoadImportedPolygonsAsync();
        }
        catch (PostgresException exception)
        {
            StatusText.Text = $"Save failed: PostgreSQL {exception.SqlState}: {exception.MessageText}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Save failed: {exception.Message}";
        }
        finally
        {
            HideBusy();
        }
    }

    /// <summary>
    /// Esc in edit mode: cancels a drag in progress, otherwise throws away every unsaved change
    /// (moves, rotations, copies, groups, deletions) and restores the saved state, staying in
    /// edit mode. With nothing to restore it just clears the selection.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Escape || !isEditMode)
        {
            return;
        }

        e.Handled = true;
        if (editDragKind != EditDragKind.None)
        {
            editDragKind = EditDragKind.None;
            draggingVertex = null;
            editShapeSnapshot.Clear();
            editHandleSnapshot = null;
            boxSelectRectangle = null;
            MapCanvas.ReleaseMouseCapture();
        }

        if (HasPendingEdits())
        {
            pendingEdits.Clear();
            DeselectEditableObject();
            RenderEditableLayerObjects();
            EditStatusText.Text = "Restored to the saved state. Unsaved edits were discarded.";
            return;
        }

        DeselectEditableObject();
        RenderEditableLayerObjects();
    }

    private void DiscardEdits_Click(object sender, RoutedEventArgs e)
    {
        if (HasPendingEdits())
        {
            var answer = MessageBox.Show(
                "Discard all unsaved edits?",
                "Discard changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ExitEditMode();
        StatusText.Text = "Edits discarded.";
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
        ScheduleStrokeWidthUpdate();
    }

    private void ResetView()
    {
        mapScale.ScaleX = 1;
        mapScale.ScaleY = 1;
        mapTranslation.X = 0;
        mapTranslation.Y = 0;
        UpdateStrokeWidths();
    }

    private void ScheduleStrokeWidthUpdate()
    {
        strokeWidthUpdateTimer.Stop();
        strokeWidthUpdateTimer.Start();
    }

    private void AddMapShape(Shape shape, double strokeWidth)
    {
        baseStrokeWidths[shape] = strokeWidth;
        shape.StrokeThickness = strokeWidth / Math.Max(mapScale.ScaleX, 0.1);
        MapLayerCanvas.Children.Add(shape);
    }

    private void UpdateStrokeWidths()
    {
        var scale = Math.Max(mapScale.ScaleX, 0.1);
        foreach (var (shape, baseWidth) in baseStrokeWidths)
        {
            shape.StrokeThickness = baseWidth / scale;
        }
    }

    private System.Windows.Point GetCanvasCenter() => new(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2);

    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        var point = geometryService.CreatePoint(210, 160);
        var marker = new Ellipse { Width = 10, Height = 10, Fill = Brushes.OrangeRed };
        Canvas.SetLeft(marker, point.X - 5);
        Canvas.SetTop(marker, point.Y - 5);
            AddMapShape(marker, 1);
        StatusText.Text = $"Point created: ({point.X:0}, {point.Y:0})";
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = selectedFeature is null
            ? "No object selected. Click a shape on the map first."
            : $"Feature #{selectedFeature.Id} ({selectedFeature.Geometry.GeometryType}) valid: {selectedFeature.Geometry.IsValid}";
    }

    private async void ImportDxf_Click(object sender, RoutedEventArgs e)
    {
        const string filePath = @"D:\DINNO\DEV\SD2D\data\SmartLayout.dxf";
        importCts = new CancellationTokenSource();
        var cancellationToken = importCts.Token;
        ImportButton.IsEnabled = false;
        CancelImportButton.IsEnabled = true;
        ShowBusy("Importing DXF...");
        var totalStopwatch = Stopwatch.StartNew();
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                throw new InvalidOperationException("PostgreSQL password is empty. Enter it and save the connection JSON first.");
            }

            StatusText.Text = "Reading DXF...";
            var importer = new DxfBlockImporter();
            var parseStopwatch = Stopwatch.StartNew();
            var singleLayer = SingleLayerImportCheckBox.IsChecked == true;
            IReadOnlyList<CadImportResult> blocks;
            IReadOnlyList<CadBlockInstanceResult> instances;
            IReadOnlyList<CadImportResult> equipmentBoundingBoxes;
            if (singleLayer)
            {
                // Only the Equipment INSERTs, as block objects: no layer aggregates, nothing else.
                blocks = Array.Empty<CadImportResult>();
                equipmentBoundingBoxes = Array.Empty<CadImportResult>();
                instances = await Task.Run(() => importer.ImportEquipmentBlockObjects(filePath), cancellationToken);
            }
            else
            {
                blocks = await Task.Run(() => importer.Import(filePath), cancellationToken);
                instances = await Task.Run(() => importer.ImportBlockInstances(filePath), cancellationToken);
                equipmentBoundingBoxes = await Task.Run(() => importer.ImportEquipmentBoundingBoxes(filePath), cancellationToken);
            }

            var allLayerRows = blocks.Concat(equipmentBoundingBoxes).ToArray();
            parseStopwatch.Stop();

            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            StatusText.Text = "Preparing PostGIS schema...";
            BusyText.Text = "Preparing PostGIS schema...";
            await repository.EnsureSchemaAsync(cancellationToken);
            if (await repository.HasImportedFileAsync(filePath, cancellationToken))
            {
                HideBusy();
                var answer = MessageBox.Show(
                    "This DXF already has imported data in the database. Delete all layers and objects for this DXF before importing again?",
                    "Existing DXF data",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Import cancelled. Existing DXF data was kept.";
                    return;
                }

                ShowBusy("Deleting previous layers and block instances...");
                StatusText.Text = "Deleting previous layers and block instances...";
                await repository.DeleteImportedFileAsync(filePath, cancellationToken);
            }

            BusyProgressBar.IsIndeterminate = false;
            BusyProgressBar.Minimum = 0;
            var progress = new Progress<ImportProgress>(p =>
            {
                BusyProgressBar.Maximum = p.Total;
                BusyProgressBar.Value = p.Completed;
                BusyText.Text = $"{p.Stage}: {p.Completed:N0}/{p.Total:N0}";
            });

            var writeStopwatch = Stopwatch.StartNew();
            await repository.ImportAsync(
                allLayerRows.Select(block => (block.BlockName, block.Layer, block.ActualGeometry, block.OuterGeometry, block.Attributes, block.FeatureType)).ToArray(),
                instances.Select(instance => (instance.BlockName, instance.Layer, instance.ActualGeometry, instance.OuterGeometry, instance.Attributes)).ToArray(),
                filePath,
                progress,
                cancellationToken);
            writeStopwatch.Stop();

            var databaseCounts = await repository.FindImportedCountsAsync(filePath, cancellationToken);
            LogImportCounts(allLayerRows, instances, databaseCounts, filePath);
            totalStopwatch.Stop();
            importLogger.Write($"DURATION source={filePath} parse-ms={parseStopwatch.ElapsedMilliseconds} write-ms={writeStopwatch.ElapsedMilliseconds} total-ms={totalStopwatch.ElapsedMilliseconds}");
            activeLayerFilter = null;
            await LoadImportedPolygonsAsync();
            StatusText.Text = singleLayer
                ? $"Imported {instances.Count} Equipment block object(s) into PostgreSQL in {totalStopwatch.ElapsedMilliseconds:N0} ms."
                : $"Imported {blocks.Count} layer(s) ({equipmentBoundingBoxes.Count} Equipment-poly) and {instances.Count} block instance(s) into PostgreSQL in {totalStopwatch.ElapsedMilliseconds:N0} ms.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Import cancelled by user. No data was written (the transaction was rolled back).";
            importLogger.Write($"IMPORT source={filePath} result=cancelled-by-user");
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
        finally
        {
            HideBusy();
            BusyProgressBar.IsIndeterminate = true;
            ImportButton.IsEnabled = true;
            CancelImportButton.IsEnabled = false;
            importCts?.Dispose();
            importCts = null;
        }
    }

    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        importCts?.Cancel();
        CancelImportButton.IsEnabled = false;
        StatusText.Text = "Cancelling import...";
    }

    private async void DeleteImportedDxf_Click(object sender, RoutedEventArgs e)
    {
        const string filePath = @"D:\DINNO\DEV\SD2D\data\SmartLayout.dxf";
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            if (string.IsNullOrWhiteSpace(connectionSettings.Password))
            {
                throw new InvalidOperationException("PostgreSQL password is empty.");
            }

            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
            await repository.EnsureSchemaAsync();
            if (!await repository.HasImportedFileAsync(filePath))
            {
                StatusText.Text = "No imported data found for SmartLayout.dxf.";
                return;
            }

            var answer = MessageBox.Show(
                "Delete every imported layer and block object belonging to SmartLayout.dxf? This cannot be undone.",
                "Delete DXF data",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                StatusText.Text = "Delete cancelled.";
                return;
            }

            await repository.DeleteImportedFileAsync(filePath);
            importLogger.Write($"DELETE source={filePath} result=deleted-all-layers-and-instances");
            MapLayerCanvas.Children.Clear();
            importedFeatures.Clear();
            baseStrokeWidths.Clear();
            shapesByLayer.Clear();
            layerZOrder.Clear();
            hiddenLayers.Clear();
            zOrderCounter = 0;
            selectionHighlight = null;
            selectedFeatureLayer = null;
            selectedFeature = null;
            LayersPanel.Children.Clear();
            StatusText.Text = "All imported SmartLayout.dxf layers and block objects were deleted.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Delete failed: {exception.Message}";
        }
    }

    private void LogImportCounts(
        IReadOnlyList<CadImportResult> dxfLayers,
        IReadOnlyList<CadBlockInstanceResult> dxfInstances,
        IReadOnlyList<ImportLayerCount> databaseCounts,
        string sourceFile)
    {
        importLogger.Write($"IMPORT source={sourceFile} dxf-layers={dxfLayers.Count} dxf-block-instances={dxfInstances.Count}");

        var generatedAttributeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "block_name", "block_handle", "block_description",
            "block_insert_x", "block_insert_y", "block_rotation_deg", "block_scale_x", "block_scale_y",
            "unsupported_entities", "outer_geom_fallback_reason"
        };
        var instancesWithNativeAttributes = dxfInstances.Count(instance => instance.Attributes.Keys.Any(key => !generatedAttributeKeys.Contains(key)));
        importLogger.Write(
            $"INFO block-instance-attributes generated=block_name,block_handle,block_insert_x/y,block_rotation_deg,block_scale_x/y for all {dxfInstances.Count} instance(s); " +
            $"native-dxf-attrib-tags found on {instancesWithNativeAttributes}/{dxfInstances.Count} instance(s)");
        var dxfByLayer = dxfLayers.ToDictionary(layer => layer.Layer ?? "(no layer)", layer => layer.EntityCount, StringComparer.OrdinalIgnoreCase);
        var instanceByLayer = dxfInstances
            .GroupBy(instance => instance.Layer ?? "(no layer)", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.LongCount(), StringComparer.OrdinalIgnoreCase);
        var layerNames = dxfByLayer.Keys.Concat(instanceByLayer.Keys).Concat(databaseCounts.Select(count => count.LayerName)).Distinct(StringComparer.OrdinalIgnoreCase);
        var databaseByLayer = databaseCounts.ToDictionary(count => count.LayerName, StringComparer.OrdinalIgnoreCase);
        foreach (var layerName in layerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            dxfByLayer.TryGetValue(layerName, out var dxfEntityCount);
            instanceByLayer.TryGetValue(layerName, out var dxfBlockCount);
            var databaseCount = databaseByLayer.TryGetValue(layerName, out var storedCount)
                ? storedCount
                : new ImportLayerCount(layerName, 0, 0);
            var entityMatch = dxfEntityCount == databaseCount.EntityCount;
            var blockMatch = dxfBlockCount == databaseCount.BlockInstanceCount;
            importLogger.Write($"LAYER name={layerName} dxf-entities={dxfEntityCount} psql-entities={databaseCount.EntityCount} entity-match={entityMatch} dxf-blocks={dxfBlockCount} psql-blocks={databaseCount.BlockInstanceCount} block-match={blockMatch}");
        }

        foreach (var layer in dxfLayers)
        {
            var layerName = layer.Layer ?? "(no layer)";
            if (layer.Attributes.TryGetValue("unsupported_entities", out var unsupported) && !string.IsNullOrEmpty(unsupported))
            {
                importLogger.Write($"WARN layer={layerName} unsupported-entities={unsupported}");
            }

            if (layer.OuterGeometryIsFallback)
            {
                var reason = layer.Attributes.TryGetValue("outer_geom_fallback_reason", out var layerReason) ? layerReason : "unknown";
                importLogger.Write($"WARN layer={layerName} outer-geom-fallback=true reason={reason}");
            }
        }

        var instanceIndexByLayer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in dxfInstances)
        {
            var layerName = instance.Layer ?? "(no layer)";
            var index = instanceIndexByLayer.TryGetValue(layerName, out var current) ? current + 1 : 0;
            instanceIndexByLayer[layerName] = index;

            if (instance.Attributes.TryGetValue("unsupported_entities", out var unsupported) && !string.IsNullOrEmpty(unsupported))
            {
                importLogger.Write($"WARN block-instance layer={layerName} block={instance.BlockName} index={index} unsupported-entities={unsupported}");
            }

            if (instance.OuterGeometryIsFallback)
            {
                var reason = instance.Attributes.TryGetValue("outer_geom_fallback_reason", out var instanceReason) ? instanceReason : "unknown";
                importLogger.Write($"WARN block-instance layer={layerName} block={instance.BlockName} index={index} outer-geom-fallback=true reason={reason}");
            }
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