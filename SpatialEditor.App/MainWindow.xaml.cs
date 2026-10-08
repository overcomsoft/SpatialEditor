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
            var paletteColor = LayerPalette[layerColors.Count % LayerPalette.Length];
            defaultLayerColors[layerName] = paletteColor;
            color = connectionSettings?.LayerStyles is { } styles
                && styles.TryGetValue(layerName, out var style)
                && TryParseHexColor(style.Color, out var custom)
                ? custom
                : paletteColor;
            layerColors[layerName] = color;
        }

        return color;
    }

    private readonly Dictionary<string, Color> defaultLayerColors = new(StringComparer.OrdinalIgnoreCase);

    private const double DefaultLineThickness = 1.0;

    private double GetLayerThickness(string layerName) =>
        connectionSettings?.LayerStyles is { } styles && styles.TryGetValue(layerName, out var style) && style.Thickness > 0
            ? style.Thickness
            : DefaultLineThickness;

    private static bool TryParseHexColor(string? hex, out Color color)
    {
        color = default;
        if (hex is { Length: 7 } && hex[0] == '#'
            && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var rgb))
        {
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Round joins and caps make touching segments (separate DXF lines, polygon corners) read as
    /// one continuous line, even when zoomed in and drawn thick; WPF's defaults (miter joins,
    /// flat caps) leave visible notches and gaps there.
    /// </summary>
    private static void ApplyRoundStroke(Shape shape)
    {
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.StrokeStartLineCap = PenLineCap.Round;
        shape.StrokeEndLineCap = PenLineCap.Round;
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
        BuildText.Text = BuildInfo.Label;
        mapScale.Changed += (_, _) => ScheduleMiniMapViewport();
        mapTranslation.Changed += (_, _) => ScheduleMiniMapViewport();
        MapCanvas.SizeChanged += (_, _) => ScheduleMiniMapViewport();
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

    private bool isConnected;

    private AppUser? currentUser;

    private bool Has(string permission) => currentUser?.Has(permission) == true;

    private bool CanImport => isConnected && Has(Permissions.ImportDxf);

    private void SetConnected(bool connected)
    {
        isConnected = connected;
        ApplyUserState();
    }

    /// <summary>Enables each feature only when connected and the logged-in user holds its permission.</summary>
    private void ApplyUserState()
    {
        ImportButton.IsEnabled = CanImport;
        DeleteImportButton.IsEnabled = CanImport;
        EditModeButton.IsEnabled = isConnected && Has(Permissions.DrawingEdit);
        UsersMenuItem.IsEnabled = Has(Permissions.UserManage);
        LibraryMenuItem.IsEnabled = isConnected && Has(Permissions.LibraryView);
        RegisterLibraryMenuItem.IsEnabled = isConnected && Has(Permissions.LibraryCreate);
        PasswordMenuItem.IsEnabled = currentUser is not null;
        LogoutMenuItem.IsEnabled = isConnected;
        LogoutMenuItem.Header = currentUser is null ? "_Log in" : "_Log out";
        LogoutMenuIcon.Source = (ImageSource)FindResource(currentUser is null ? "Icon.Login" : "Icon.Logout");
        ConnectionDot.Fill = new SolidColorBrush(isConnected ? Color.FromRgb(0x43, 0xA0, 0x47) : Color.FromRgb(0xB0, 0xBE, 0xC5));
        ConnectionSummaryText.Text = isConnected
            ? $"{connectionSettings.Host}:{connectionSettings.Port}/{connectionSettings.Database}"
            : "Not connected";
        UserSummaryText.Text = currentUser is null
            ? "Not logged in"
            : $"{currentUser.DisplayName} ({(currentUser.Roles.Count == 0 ? "no role" : string.Join(", ", currentUser.Roles))})";
    }

    private bool Require(string permission, string action)
    {
        if (Has(permission))
        {
            return true;
        }

        MessageBox.Show(this, $"You do not have permission to {action}.", "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private async Task AuditAsync(string action, string? entityType, string? entityId, string? summary)
    {
        if (currentUser is null)
        {
            return;
        }

        await using var users = new UserRepository(connectionSettings.ConnectionString);
        await users.TryWriteAuditAsync(currentUser, action, entityType, entityId, summary);
    }

    /// <summary>
    /// Shows the login window (or the first-administrator setup when there are no users), forces a
    /// password change when required, and remembers the ID for next time.
    /// </summary>
    private async Task<bool> LoginAsync()
    {
        try
        {
            await using var users = new UserRepository(connectionSettings.ConnectionString);
            await users.EnsureSchemaAsync();
            var setup = !await users.HasUsersAsync();
            var dialog = new LoginWindow(users, connectionSettings.LastLoginId, setup) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.User is null)
            {
                return false;
            }

            var user = dialog.User;
            while (user.MustChangePassword)
            {
                var change = new PasswordWindow("Change password", "You must set a new password before continuing.", askOld: true, allowCancel: false)
                {
                    Owner = this
                };
                if (change.ShowDialog() != true)
                {
                    return false;
                }

                try
                {
                    await users.ChangeOwnPasswordAsync(user, change.OldPassword, change.NewPassword);
                    break;
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(this, exception.Message, "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            currentUser = user;
            connectionSettings.LastLoginId = user.LoginId;
            try
            {
                await connectionSettings.SaveAsync();
            }
            catch (Exception)
            {
                // Remembering the ID is a convenience only.
            }

            return true;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Login failed: {exception.Message}";
            MessageBox.Show(this, $"Login failed: {exception.Message}", "Log in", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>After a (new) server connection: log in, then load the drawing data the user may see.</summary>
    private async Task CompleteConnectionAsync()
    {
        currentUser = null;
        ClearMapAndLayers();
        SetConnected(true);
        await LoginAndLoadAsync();
    }

    private async Task LoginAndLoadAsync()
    {
        if (!await LoginAsync())
        {
            ApplyUserState();
            StatusText.Text = "Not logged in. Use the Log in button.";
            return;
        }

        ApplyUserState();
        if (Has(Permissions.DrawingView))
        {
            await StartSyncAsync();
            await PromptLayerSelectionAndLoadAsync();
        }
        else
        {
            StatusText.Text = "Logged in, but your roles do not allow viewing drawings.";
        }
    }

    private void ClearMapAndLayers()
    {
        StopSync();
        if (isEditMode)
        {
            ExitEditMode();
        }

        MapLayerCanvas.Children.Clear();
        importedFeatures.Clear();
        blockGroups = null;
        baseStrokeWidths.Clear();
        shapesByLayer.Clear();
        layerZOrder.Clear();
        hiddenLayers.Clear();
        zOrderCounter = 0;
        selectionHighlight = null;
        selectedFeatureLayer = null;
        selectedFeature = null;
        LayersPanel.Children.Clear();
        SelectedObjectText.Text = "No object selected";
        ClearPropertyDetails();
        ScheduleMiniMapRebuild();
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null)
        {
            await LoginAndLoadAsync();
            return;
        }

        if (isEditMode && HasPendingEdits())
        {
            var answer = MessageBox.Show(this, "Discard unsaved edits and log out?", "Log out", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        await AuditAsync("logout", "user", currentUser.Id.ToString(), null);
        currentUser = null;
        ClearMapAndLayers();
        ApplyUserState();
        await LoginAndLoadAsync();
    }

    private async void ChangePassword_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null)
        {
            return;
        }

        var dialog = new PasswordWindow("Change password", "Choose a new password.", askOld: true) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await using var users = new UserRepository(connectionSettings.ConnectionString);
            await users.ChangeOwnPasswordAsync(currentUser, dialog.OldPassword, dialog.NewPassword);
            StatusText.Text = "Your password was changed.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Overview map (minimap) ------------------------------------------------------------

    private bool miniMapRebuildPending;
    private bool miniMapViewportPending;
    private bool miniMapDragging;

    /// <summary>Redraws the overview shortly after layer content/visibility/style changes (coalesced).</summary>
    private void ScheduleMiniMapRebuild()
    {
        if (miniMapRebuildPending)
        {
            return;
        }

        miniMapRebuildPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            miniMapRebuildPending = false;
            RebuildMiniMap();
        }));
    }

    private void ScheduleMiniMapViewport()
    {
        if (miniMapViewportPending)
        {
            return;
        }

        miniMapViewportPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, new Action(() =>
        {
            miniMapViewportPending = false;
            UpdateMiniMapViewport();
        }));
    }

    /// <summary>
    /// The overview reuses the map's own (frozen-size) path geometries with a thin fixed-width pen, so it
    /// shows the same layers, colours, order and visibility as the main view at any zoom level.
    /// </summary>
    private void RebuildMiniMap()
    {
        MiniMapShapes.Children.Clear();
        if (renderedEnvelope is null || shapesByLayer.Count == 0 || renderedWidth <= 0 || renderedHeight <= 0)
        {
            MiniMapViewport.Visibility = Visibility.Collapsed;
            return;
        }

        MiniMapCanvas.Width = renderedWidth;
        MiniMapCanvas.Height = renderedHeight;
        foreach (var (layerName, shapes) in shapesByLayer)
        {
            if (hiddenLayers.Contains(layerName))
            {
                continue;
            }

            foreach (var shape in shapes.OfType<System.Windows.Shapes.Path>())
            {
                var copy = new System.Windows.Shapes.Path
                {
                    Data = shape.Data,
                    Stroke = shape.Stroke,
                    Fill = shape.Fill,
                    StrokeLineJoin = PenLineJoin.Round,
                    IsHitTestVisible = false
                };
                Panel.SetZIndex(copy, Panel.GetZIndex(shape));
                MiniMapShapes.Children.Add(copy);
            }
        }

        MiniMapBox.UpdateLayout();
        ApplyMiniMapStrokeWidths();
        UpdateMiniMapViewport();
    }

    /// <summary>Scale from map coordinates to overview pixels.</summary>
    private double MiniMapScale => renderedWidth > 0 && MiniMapBox.ActualWidth > 0 ? MiniMapBox.ActualWidth / renderedWidth : 1;

    private void ApplyMiniMapStrokeWidths()
    {
        var thickness = 1.0 / MiniMapScale;
        foreach (var child in MiniMapShapes.Children.OfType<System.Windows.Shapes.Path>())
        {
            child.StrokeThickness = thickness;
        }

        MiniMapViewport.StrokeThickness = 1.6 / MiniMapScale;
    }

    /// <summary>Places the red rectangle on the part of the drawing currently visible in the map view.</summary>
    private void UpdateMiniMapViewport()
    {
        if (renderedEnvelope is null || shapesByLayer.Count == 0
            || mapScale.ScaleX <= 0 || mapScale.ScaleY <= 0 || MapCanvas.ActualWidth <= 0)
        {
            MiniMapViewport.Visibility = Visibility.Collapsed;
            return;
        }

        var left = -mapTranslation.X / mapScale.ScaleX;
        var top = -mapTranslation.Y / mapScale.ScaleY;
        var right = (MapCanvas.ActualWidth - mapTranslation.X) / mapScale.ScaleX;
        var bottom = (MapCanvas.ActualHeight - mapTranslation.Y) / mapScale.ScaleY;
        Canvas.SetLeft(MiniMapViewport, left);
        Canvas.SetTop(MiniMapViewport, top);
        MiniMapViewport.Width = Math.Max(right - left, 0);
        MiniMapViewport.Height = Math.Max(bottom - top, 0);
        MiniMapViewport.Visibility = Visibility.Visible;
    }

    private void MiniMap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMiniMapStrokeWidths();
        ScheduleMiniMapViewport();
    }

    /// <summary>Pans the map so the clicked overview point becomes the centre of the view.</summary>
    private void CenterMapOnMiniMapPoint(System.Windows.Point point)
    {
        if (renderedEnvelope is null || shapesByLayer.Count == 0)
        {
            return;
        }

        var x = Math.Clamp(point.X, 0, renderedWidth);
        var y = Math.Clamp(point.Y, 0, renderedHeight);
        mapTranslation.X = MapCanvas.ActualWidth / 2 - x * mapScale.ScaleX;
        mapTranslation.Y = MapCanvas.ActualHeight / 2 - y * mapScale.ScaleY;
    }

    private void MiniMap_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (renderedEnvelope is null)
        {
            return;
        }

        miniMapDragging = true;
        MiniMapBorder.CaptureMouse();
        CenterMapOnMiniMapPoint(e.GetPosition(MiniMapCanvas));
        e.Handled = true;
    }

    private void MiniMap_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (miniMapDragging)
        {
            CenterMapOnMiniMapPoint(e.GetPosition(MiniMapCanvas));
        }
    }

    private void MiniMap_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        miniMapDragging = false;
        MiniMapBorder.ReleaseMouseCapture();
    }

    // ---- Multi-user synchronisation -------------------------------------------------------

    private readonly Guid sessionId = Guid.NewGuid();
    private SyncCoordinator? sync;
    private bool syncCheckRunning;

    private ChangeContext CurrentChangeContext => new(currentUser?.Id, currentUser?.DisplayName, sessionId);

    private async Task StartSyncAsync()
    {
        StopSync();
        try
        {
            var coordinator = new SyncCoordinator(
                connectionSettings.ConnectionString, sessionId,
                () => layerChecksByName.Keys.ToArray());
            coordinator.CheckRequested += () => _ = CheckForSyncChangesAsync();
            coordinator.ConnectionChanged += _ => UpdateSyncStatus();
            await coordinator.StartAsync();
            sync = coordinator;
            UpdateSyncStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"동기화를 시작할 수 없습니다: {exception.Message}";
            SyncStatusText.Text = "동기화: 사용 안 함";
        }
    }

    private void StopSync()
    {
        var old = sync;
        sync = null;
        SyncStatusText.Text = string.Empty;
        if (old is not null)
        {
            _ = old.DisposeAsync().AsTask();
        }
    }

    private void ScheduleSyncCheck()
    {
        if (sync is not null)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => _ = CheckForSyncChangesAsync()));
        }
    }

    private void SyncStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _ = CheckForSyncChangesAsync(force: true);

    private void UpdateSyncStatus()
    {
        var coordinator = sync;
        if (coordinator is null)
        {
            SyncStatusText.Text = string.Empty;
            return;
        }

        if (coordinator.Pending is { HasChanges: true } pending)
        {
            SyncStatusText.Text = $"갱신 대기 ({SyncCoordinator.CountChanges(pending)}건)";
            SyncStatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0x7E, 0x00));
            SyncStatusText.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            SyncStatusText.Text = coordinator.IsListening ? "동기화: 연결됨" : "동기화: 재연결 중";
            SyncStatusText.Foreground = new SolidColorBrush(coordinator.IsListening ? Color.FromRgb(0x2E, 0x7D, 0x32) : Color.FromRgb(0x78, 0x90, 0x9C));
            SyncStatusText.FontWeight = FontWeights.Normal;
        }
    }

    private static string BuildSyncMessage(ChangeBatch batch)
    {
        var lines = batch.Summaries.Take(8).Select(summary =>
        {
            var who = string.IsNullOrWhiteSpace(summary.UserName) ? "다른 사용자" : summary.UserName;
            var parts = new List<string>();
            if (summary.Updated > 0) parts.Add($"수정 {summary.Updated}");
            if (summary.Added > 0) parts.Add($"추가 {summary.Added}");
            if (summary.Deleted > 0) parts.Add($"삭제 {summary.Deleted}");
            if (summary.WholeLayer) parts.Add("DXF 가져오기/삭제");
            return $"• {who} 님 - {summary.LayerName}: {string.Join(", ", parts)}";
        }).ToList();
        if (batch.Summaries.Count > lines.Count)
        {
            lines.Add($"… 외 {batch.Summaries.Count - lines.Count}건");
        }

        return "서버 데이터가 변경되었습니다.\n\n" + string.Join("\n", lines) + "\n\n갱신하시겠습니까?";
    }

    /// <summary>
    /// Looks for changes other sessions saved to the loaded layers and, if there are new ones, asks
    /// whether to refresh. While editing, the question waits until the edit is saved or discarded.
    /// </summary>
    private async Task CheckForSyncChangesAsync(bool force = false)
    {
        var coordinator = sync;
        if (coordinator is null || syncCheckRunning || layerChecksByName.Count == 0
            || BusyOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        syncCheckRunning = true;
        try
        {
            var batch = await coordinator.CheckAsync();
            if (!ReferenceEquals(coordinator, sync))
            {
                return;
            }

            UpdateSyncStatus();
            if (!batch.HasChanges)
            {
                return;
            }

            if (isEditMode)
            {
                if (force)
                {
                    MessageBox.Show(this, "편집을 저장하거나 취소한 뒤에 갱신할 수 있습니다.", "서버 데이터 변경", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            if (!force && !coordinator.IsNewSinceDeclined(batch))
            {
                return;
            }

            var answer = MessageBox.Show(this, BuildSyncMessage(batch), "서버 데이터 변경", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (!ReferenceEquals(coordinator, sync))
            {
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                await ReloadForSyncAsync();
            }
            else
            {
                coordinator.Decline(batch);
            }

            UpdateSyncStatus();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"동기화 확인 실패: {exception.Message}";
        }
        finally
        {
            syncCheckRunning = false;
        }
    }

    /// <summary>Reloads the loaded layers but keeps the user's zoom/pan position and hidden layers.</summary>
    private async Task ReloadForSyncAsync()
    {
        var centerX = MapCanvas.ActualWidth / 2;
        var centerY = MapCanvas.ActualHeight / 2;
        Coordinate? centerWorld = null;
        if (renderedEnvelope is not null && mapScale.ScaleX > 0 && mapScale.ScaleY > 0)
        {
            centerWorld = UnprojectFromScreen(new System.Windows.Point(
                (centerX - mapTranslation.X) / mapScale.ScaleX,
                (centerY - mapTranslation.Y) / mapScale.ScaleY));
        }

        var scaleX = mapScale.ScaleX;
        var scaleY = mapScale.ScaleY;
        var hidden = hiddenLayers.ToList();

        await LoadImportedPolygonsAsync();

        if (centerWorld is not null && renderedEnvelope is not null)
        {
            var projected = ProjectToScreen(centerWorld);
            mapScale.ScaleX = scaleX;
            mapScale.ScaleY = scaleY;
            mapTranslation.X = centerX - projected.X * scaleX;
            mapTranslation.Y = centerY - projected.Y * scaleY;
            UpdateZoomText();
            UpdateStrokeWidths();
        }

        foreach (var name in hidden)
        {
            SetLayerChecked(name, false);
        }

        StatusText.Text = "서버 데이터로 갱신했습니다.";
    }

    private async void OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null || !Require(Permissions.LibraryView, "view the block library"))
        {
            return;
        }

        try
        {
            await using var library = new LibraryRepository(connectionSettings.ConnectionString);
            await library.EnsureSchemaAsync();
            new LibraryWindow(library, currentUser, AuditAsync) { Owner = this }.ShowDialog();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open the library: {exception.Message}";
        }
    }

    /// <summary>The saved block instance the user has selected (view mode or edit mode), if exactly one.</summary>
    private (long InstanceId, string? BlockName)? GetSelectedBlockInstance(out string? problem)
    {
        problem = null;
        if (isEditMode)
        {
            if (selectedEditIds.Count != 1)
            {
                problem = "Select exactly one block object first.";
                return null;
            }

            var id = selectedEditIds.First();
            if (!IsEncodedBlockInstanceId(id) || !editableSourcesById.TryGetValue(id, out var source))
            {
                problem = "The selected object is not a saved block instance.";
                return null;
            }

            if (pendingEdits.TryGetValue(id, out var pending) && (pending.HasChanges || pending.IsNew))
            {
                problem = "Save or discard the pending edits of this block before registering it.";
                return null;
            }

            return (DecodeBlockInstanceId(id), source.BlockName);
        }

        if (selectedFeature is { FeatureType: "block_instance" } feature)
        {
            feature.Attributes.TryGetValue("block_name", out var blockName);
            return (feature.Id, blockName);
        }

        problem = "Select a block object first.";
        return null;
    }

    private async void RegisterInLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null || !Require(Permissions.LibraryCreate, "register library blocks"))
        {
            return;
        }

        if (GetSelectedBlockInstance(out var problem) is not { } selection)
        {
            MessageBox.Show(this, problem, "Register in library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            await using var library = new LibraryRepository(connectionSettings.ConnectionString);
            await library.EnsureSchemaAsync();
            var categories = await library.ListCategoriesAsync();
            var window = LibraryItemWindow.ForRegister(categories, selection.BlockName ?? $"BLOCK-{selection.InstanceId}");
            window.Owner = this;
            if (window.ShowDialog() != true)
            {
                return;
            }

            var result = await library.RegisterFromInstanceAsync(
                selection.InstanceId, window.Code, window.ItemName, window.CategoryId,
                window.Vendor, window.ModelNo, currentUser.Id);
            if (result.Outcome == LibraryRegisterOutcome.Created)
            {
                await AuditAsync("library.register", "block_library", result.LibraryId.ToString(), result.Code);
                StatusText.Text = $"Registered '{result.Code}' in the block library.";
            }
            else
            {
                await AuditAsync("library.link", "block_library", result.LibraryId.ToString(), $"instance {selection.InstanceId} -> {result.Code}");
                StatusText.Text = $"The library already holds this shape as '{result.Code}'; the block was linked to it.";
                MessageBox.Show(this, StatusText.Text, "Register in library", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, "Register in library", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Register failed: {exception.Message}";
        }
    }

    private async void ManageUsers_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null || !Require(Permissions.UserManage, "manage users"))
        {
            return;
        }

        await using var users = new UserRepository(connectionSettings.ConnectionString);
        new UserManagementWindow(users, currentUser) { Owner = this }.ShowDialog();

        // Roles or the account itself may have been changed from inside the window.
        try
        {
            var refreshed = await users.GetUserAsync(currentUser.Id);
            if (refreshed is null || !refreshed.IsActive)
            {
                MessageBox.Show(this, "Your account is no longer active. You will be logged out.", "User management");
                currentUser = null;
                ClearMapAndLayers();
                ApplyUserState();
                await LoginAndLoadAsync();
                return;
            }

            currentUser = refreshed;
            ApplyUserState();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not refresh your permissions: {exception.Message}";
        }
    }

    /// <summary>Shows the connection window; returns the settings that connected, or null if cancelled.</summary>
    private PostgresConnectionSettings? ShowServerDialog(string? message)
    {
        var dialog = new ServerConnectionWindow(connectionSettings, message) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            connectionSettings = await PostgresConnectionSettings.LoadAsync();
        }
        catch (Exception exception)
        {
            connectionSettings = new PostgresConnectionSettings();
            StatusText.Text = $"Could not read the connection settings file: {exception.Message}";
        }

        await ConnectOnStartupAsync();
    }

    /// <summary>
    /// Connects with the saved settings; when there are none or the connection fails, the server
    /// window opens (with the reason) so the user can correct them.
    /// </summary>
    private async Task ConnectOnStartupAsync()
    {
        string? failure = null;
        if (string.IsNullOrWhiteSpace(connectionSettings.Password))
        {
            failure = "No saved connection yet. Enter the PostgreSQL connection details.";
        }
        else
        {
            ShowBusy("Connecting to PostgreSQL...");
            try
            {
                await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString);
                StatusText.Text = await repository.TestConnectionAsync();
            }
            catch (Exception exception)
            {
                failure = $"Automatic connection failed: {exception.Message}";
            }
            finally
            {
                HideBusy();
            }
        }

        if (failure is not null)
        {
            var settings = ShowServerDialog(failure);
            if (settings is null)
            {
                SetConnected(false);
                StatusText.Text = "Not connected. Use the Server button to connect.";
                return;
            }

            connectionSettings = settings;
        }

        await CompleteConnectionAsync();
    }

    private async void ConnectServer_Click(object sender, RoutedEventArgs e)
    {
        if (isEditMode)
        {
            MessageBox.Show("Exit edit mode (Save or Discard) before changing the server connection.", "Server", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var settings = ShowServerDialog(null);
        if (settings is null)
        {
            return;
        }

        connectionSettings = settings;
        await CompleteConnectionAsync();
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
            // Anything saved from now on is newer than what this load shows.
            if (sync is not null)
            {
                await sync.ResetBaselineAsync();
            }

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
        InputBlocker.Visibility = Visibility.Visible;
        Cursor = System.Windows.Input.Cursors.Wait;
    }

    private void HideBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        InputBlocker.Visibility = Visibility.Collapsed;
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
            ApplyRoundStroke(path);
            RegisterLayerPath(path, layerName, GetLayerThickness(layerName));
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
            ApplyRoundStroke(path);
            RegisterLayerPath(path, layerName, GetLayerThickness(layerName));
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
        ScheduleMiniMapRebuild();
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
        // HighlightFeature clears the previous highlight (and with it selectedFeature/Layer), so the
        // new selection must be recorded afterwards or hiding the layer could never clear it.
        HighlightFeature(feature);
        selectedFeatureLayer = feature.LayerName ?? "(no layer)";
        selectedFeature = feature;
        UpdateSelectionCount();
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
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
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

        Panel.SetZIndex(highlight, 100000);
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
        UpdateSelectionCount();
    }

    private readonly Dictionary<string, CheckBox> layerChecksByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TextBlock> layerNameTextsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, System.Windows.Controls.Image> layerPencilsByName = new(StringComparer.OrdinalIgnoreCase);
    private System.Windows.Point layerDragStart;
    private Border? layerDragRow;
    private const string LayerDragFormat = "SpatialEditor.Layer";

    /// <summary>
    /// QGIS-style layer list: visibility check box, color symbol (click = edit that layer), one-line
    /// name with the object count, a pencil while the layer is being edited, double-click for the
    /// style dialog, a right-click menu, and drag-and-drop to change the stacking order (top of the
    /// list = top of the map).
    /// </summary>
    private void PopulateLayerPanel(IReadOnlyList<LayerInfo> layers)
    {
        LayersPanel.Children.Clear();
        layerSwatchesByName.Clear();
        layerRowsByName.Clear();
        layerChecksByName.Clear();
        layerNameTextsByName.Clear();
        layerPencilsByName.Clear();
        selectedLayerName = null;
        foreach (var layer in layers)
        {
            var layerName = layer.LayerName;
            var color = GetLayerColor(layerName);
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
                Tag = layerName,
                ToolTip = "Click to edit this layer"
            };
            swatch.MouseLeftButtonDown += LayerSwatch_MouseLeftButtonDown;
            layerSwatchesByName[layerName] = swatch;

            var checkBox = new CheckBox
            {
                IsChecked = true,
                Tag = layerName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Show/hide this layer"
            };
            checkBox.Checked += LayerVisibilityChanged;
            checkBox.Unchecked += LayerVisibilityChanged;
            layerChecksByName[layerName] = checkBox;

            var count = layer.InstanceCount > 0 ? layer.InstanceCount : layer.EntityCount;
            var nameText = new TextBlock
            {
                Text = $"{layerName} ({count:N0})",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Layer: {layerName}\nGeometry: {layer.GeometryTypes}\nBlock instances: {layer.InstanceCount}\nEntities: {layer.EntityCount}\n\nDouble-click: style   Drag: change order   Right-click: more"
            };
            layerNameTextsByName[layerName] = nameText;

            var pencil = new System.Windows.Controls.Image
            {
                Source = (ImageSource)FindResource("Icon.Edit"),
                Width = 14,
                Height = 14,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = Visibility.Collapsed,
                ToolTip = "This layer is being edited"
            };
            layerPencilsByName[layerName] = pencil;

            var content = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(checkBox, Dock.Left);
            DockPanel.SetDock(swatch, Dock.Left);
            DockPanel.SetDock(pencil, Dock.Right);
            content.Children.Add(checkBox);
            content.Children.Add(swatch);
            content.Children.Add(pencil);
            content.Children.Add(nameText);

            var row = new Border
            {
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(0, 0, 0, 2),
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Tag = layerName,
                AllowDrop = true,
                Child = content,
                ContextMenu = BuildLayerContextMenu(layerName)
            };
            row.MouseLeftButtonDown += LayerRow_MouseLeftButtonDown;
            row.PreviewMouseMove += LayerRow_PreviewMouseMove;
            row.DragOver += LayerRow_DragOver;
            row.Drop += LayerRow_Drop;
            layerRowsByName[layerName] = row;

            LayersPanel.Children.Add(row);
        }

        ApplyLayerOrder();
        UpdateLayerSwatchEditState();
    }

    private ContextMenu BuildLayerContextMenu(string layerName)
    {
        MenuItem Item(string header, string icon, Action action)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new System.Windows.Controls.Image { Source = (ImageSource)FindResource(icon), Width = 16, Height = 16 }
            };
            item.Click += (_, _) => action();
            return item;
        }

        var editItem = Item("Edit this layer", "Icon.Edit", () => _ = ToggleLayerEditAsync(layerName));
        var menu = new ContextMenu();
        menu.Opened += (_, _) => editItem.IsEnabled = EditModeButton.IsEnabled;
        menu.Items.Add(Item("Zoom to layer", "Icon.Fit", () => ZoomToLayer(layerName)));
        menu.Items.Add(Item("Style...", "Icon.Style", () => _ = OpenLayerStyleAsync(layerName)));
        menu.Items.Add(editItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Show only this layer", "Icon.ShowAll", () => ShowOnlyLayer(layerName)));
        menu.Items.Add(Item("Hide this layer", "Icon.HideAll", () => SetLayerChecked(layerName, false)));
        return menu;
    }

    private void LayerRow_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string layerName } row)
        {
            return;
        }

        SelectLayerRow(layerName);
        layerDragStart = e.GetPosition(null);
        layerDragRow = row;
        if (e.ClickCount == 2)
        {
            layerDragRow = null;
            e.Handled = true;
            _ = OpenLayerStyleAsync(layerName);
        }
    }

    private void LayerRow_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || layerDragRow is null)
        {
            return;
        }

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - layerDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(position.Y - layerDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = layerDragRow;
        layerDragRow = null;
        if (row.Tag is string name)
        {
            DragDrop.DoDragDrop(row, new DataObject(LayerDragFormat, name), DragDropEffects.Move);
        }
    }

    private void LayerRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(LayerDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void LayerRow_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Border target || !e.Data.GetDataPresent(LayerDragFormat)
            || e.Data.GetData(LayerDragFormat) is not string draggedName
            || !layerRowsByName.TryGetValue(draggedName, out var dragged) || ReferenceEquals(dragged, target))
        {
            return;
        }

        var from = LayersPanel.Children.IndexOf(dragged);
        var to = LayersPanel.Children.IndexOf(target);
        if (from < 0 || to < 0)
        {
            return;
        }

        LayersPanel.Children.RemoveAt(from);
        LayersPanel.Children.Insert(to, dragged);
        e.Handled = true;
        ApplyLayerOrder();
    }

    /// <summary>
    /// Stacks the map shapes in the order of the layer list (first row on top). Inside a layer the
    /// fills sit under the lines and the lines under the point markers. The same rank drives the
    /// "which layer wins a click" rule.
    /// </summary>
    private void ApplyLayerOrder()
    {
        var names = LayersPanel.Children.OfType<Border>().Select(row => (string)row.Tag).ToList();
        for (var index = 0; index < names.Count; index++)
        {
            var rank = names.Count - index;
            layerZOrder[names[index]] = rank;
            if (!shapesByLayer.TryGetValue(names[index], out var shapes))
            {
                continue;
            }

            foreach (var shape in shapes)
            {
                var kind = shape.Stroke is null ? 2 : shape.Fill is null ? 1 : 0;
                Panel.SetZIndex(shape, rank * 10 + kind);
            }
        }

        ScheduleMiniMapRebuild();
    }

    private void SetLayerChecked(string layerName, bool isChecked)
    {
        if (layerChecksByName.TryGetValue(layerName, out var checkBox))
        {
            checkBox.IsChecked = isChecked;
        }
    }

    private void ShowOnlyLayer(string layerName)
    {
        foreach (var (name, checkBox) in layerChecksByName)
        {
            checkBox.IsChecked = string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ZoomToLayer(string layerName)
    {
        var envelope = new Envelope();
        foreach (var feature in importedFeatures.Where(f => string.Equals(f.LayerName ?? "(no layer)", layerName, StringComparison.OrdinalIgnoreCase)))
        {
            envelope.ExpandToInclude(feature.Geometry.EnvelopeInternal);
        }

        if (envelope.IsNull || renderedEnvelope is null)
        {
            StatusText.Text = $"Layer '{layerName}' has nothing to zoom to.";
            return;
        }

        ZoomToEnvelope(envelope);
    }

    private void ZoomToEnvelope(Envelope envelope)
    {
        var a = ProjectToScreen(new Coordinate(envelope.MinX, envelope.MinY));
        var b = ProjectToScreen(new Coordinate(envelope.MaxX, envelope.MaxY));
        var left = Math.Min(a.X, b.X);
        var right = Math.Max(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var bottom = Math.Max(a.Y, b.Y);
        var canvasWidth = Math.Max(MapCanvas.ActualWidth, 1);
        var canvasHeight = Math.Max(MapCanvas.ActualHeight, 1);
        var scale = Math.Clamp(Math.Min(canvasWidth / Math.Max(right - left, 1), canvasHeight / Math.Max(bottom - top, 1)) * 0.9, 0.1, 20);
        mapScale.ScaleX = scale;
        mapScale.ScaleY = scale;
        mapTranslation.X = canvasWidth / 2 - (left + right) / 2 * scale;
        mapTranslation.Y = canvasHeight / 2 - (top + bottom) / 2 * scale;
        UpdateZoomText();
        ScheduleStrokeWidthUpdate();
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

        foreach (var (name, text) in layerNameTextsByName)
        {
            text.FontWeight = activeLayerNames.Contains(name) ? FontWeights.Bold : FontWeights.Normal;
        }

        foreach (var (name, pencil) in layerPencilsByName)
        {
            pencil.Visibility = activeLayerNames.Contains(name) ? Visibility.Visible : Visibility.Collapsed;
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

        ScheduleMiniMapRebuild();
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

    private async Task OpenLayerStyleAsync(string layerName)
    {
        var dialog = new LayerStyleWindow(layerName, GetLayerColor(layerName), GetLayerThickness(layerName)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (dialog.ResetRequested)
        {
            connectionSettings.LayerStyles.Remove(layerName);
            layerColors[layerName] = defaultLayerColors.TryGetValue(layerName, out var original) ? original : LayerPalette[0];
        }
        else
        {
            connectionSettings.LayerStyles[layerName] = new LayerStyleSetting
            {
                Color = LayerStyleWindow.ToHex(dialog.SelectedColor),
                Thickness = dialog.SelectedThickness
            };
            layerColors[layerName] = dialog.SelectedColor;
        }

        ApplyLayerStyle(layerName);
        try
        {
            await connectionSettings.SaveAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Style applied, but saving it failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Re-colors and re-sizes a layer's already-drawn shapes in place (no reload), updates its
    /// panel swatch, and redraws the edit overlay if the layer is being edited.
    /// </summary>
    private void ApplyLayerStyle(string layerName)
    {
        var color = GetLayerColor(layerName);
        var thickness = GetLayerThickness(layerName);
        var scale = Math.Max(mapScale.ScaleX, 0.1);

        if (shapesByLayer.TryGetValue(layerName, out var layerShapes))
        {
            foreach (var shape in layerShapes)
            {
                if (shape.Stroke is not null)
                {
                    shape.Stroke = new SolidColorBrush(color);
                    baseStrokeWidths[shape] = thickness;
                    shape.StrokeThickness = thickness / scale;
                }

                if (shape.Fill is SolidColorBrush)
                {
                    // Polygon fills are translucent; point markers are solid.
                    shape.Fill = shape.Stroke is null
                        ? new SolidColorBrush(color)
                        : new SolidColorBrush(Color.FromArgb(60, color.R, color.G, color.B));
                }
            }
        }

        if (layerSwatchesByName.TryGetValue(layerName, out var swatch))
        {
            swatch.Fill = new SolidColorBrush(Color.FromArgb(140, color.R, color.G, color.B));
            swatch.Stroke = new SolidColorBrush(color);
        }

        if (isEditMode)
        {
            RenderEditableLayerObjects();
        }

        StatusText.Text = $"Layer '{layerName}' style: {LayerStyleWindow.ToHex(color)}, {thickness:0.##} px.";
        ScheduleMiniMapRebuild();
    }

    private static void SetToggleState(Button button, bool on) => button.Tag = on ? "On" : null;

    private void UpdateZoomText() => ZoomText.Text = $"{mapScale.ScaleX * 100:0}%";

    private void UpdateSelectionCount() =>
        SelectionCountText.Text = $"Selected: {(isEditMode ? selectedEditIds.Count : selectedFeature is null ? 0 : 1)}";

    private void UpdateCursorCoordinates(System.Windows.Point canvasPoint)
    {
        if (renderedEnvelope is null)
        {
            return;
        }

        var scale = Math.Max(mapScale.ScaleX, 0.0001);
        var world = UnprojectFromScreen(new System.Windows.Point(
            (canvasPoint.X - mapTranslation.X) / scale,
            (canvasPoint.Y - mapTranslation.Y) / scale));
        CursorCoordText.Text = $"X {world.X:N2}    Y {world.Y:N2}";
    }

    private void MapCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        CursorCoordText.Text = "X -    Y -";

    private void HandleShortcut(System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox or PasswordBox)
        {
            return;
        }

        var ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;

        // The Korean IME reports letter keys as ImeProcessed; the real key is in ImeProcessedKey.
        var key = e.Key == System.Windows.Input.Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == System.Windows.Input.Key.R && !ctrl && !alt && isEditMode && selectedEditIds.Count > 0)
        {
            // "r" turns clockwise, "R" (the typed capital: Shift, or Caps Lock without Shift) counter-clockwise.
            e.Handled = true;
            if (!e.IsRepeat)
            {
                var shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
                var capital = shift ^ System.Windows.Input.Keyboard.IsKeyToggled(System.Windows.Input.Key.CapsLock);
                RotateSelected(clockwise: !capital);
            }

            return;
        }

        if (ctrl && e.Key == System.Windows.Input.Key.S && isEditMode && SaveEditsButton.IsEnabled)
        {
            e.Handled = true;
            SaveEdits_Click(this, new RoutedEventArgs());
        }
        else if (ctrl && e.Key == System.Windows.Input.Key.D && isEditMode && DuplicateButton.IsEnabled)
        {
            e.Handled = true;
            DuplicateSelected_Click(this, new RoutedEventArgs());
        }
        else if (ctrl && e.Key == System.Windows.Input.Key.G && isEditMode && GroupButton.IsEnabled)
        {
            e.Handled = true;
            GroupSelected_Click(this, new RoutedEventArgs());
        }
        else if (!ctrl && e.Key == System.Windows.Input.Key.Delete && isEditMode && DeleteObjectButton.IsEnabled)
        {
            e.Handled = true;
            DeleteSelected_Click(this, new RoutedEventArgs());
        }
        else if (System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.None && e.Key == System.Windows.Input.Key.F)
        {
            e.Handled = true;
            FitView_Click(this, new RoutedEventArgs());
        }
    }

    private void DeselectAll_Click(object sender, RoutedEventArgs e)
    {
        if (isEditMode)
        {
            DeselectEditableObject();
            return;
        }

        ClearSelectionHighlight();
        SelectedObjectText.Text = "No object selected";
        ClearPropertyDetails();
    }

    private void ExitApp_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        MessageBox.Show(this,
            "OmniDT SE (Spatial Editor) " + version + Environment.NewLine +
            "Build " + BuildInfo.Number + " (" + BuildInfo.Display + ")" + Environment.NewLine + Environment.NewLine +
            "PostGIS spatial data editor for CAD equipment blocks." + Environment.NewLine +
            "Import DXF, edit block objects, manage users and permissions.",
            "About OmniDT SE", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private double leftPanelWidth = 250;
    private double rightPanelWidth = 260;

    private void LeftPanelMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var visible = LeftPanelMenuItem.IsChecked;
        if (!visible && LeftColumn.ActualWidth > 0)
        {
            leftPanelWidth = LeftColumn.ActualWidth;
        }

        LeftPanel.Visibility = LeftSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.MinWidth = visible ? 170 : 0;
        LeftColumn.Width = new GridLength(visible ? leftPanelWidth : 0);
        LeftSplitColumn.Width = new GridLength(visible ? 6 : 0);
    }

    private void RightPanelMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        var visible = RightPanelMenuItem.IsChecked;
        if (!visible && RightColumn.ActualWidth > 0)
        {
            rightPanelWidth = RightColumn.ActualWidth;
        }

        RightPanel.Visibility = RightSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.MinWidth = visible ? 180 : 0;
        RightColumn.Width = new GridLength(visible ? rightPanelWidth : 0);
        RightSplitColumn.Width = new GridLength(visible ? 6 : 0);
    }

    private void StatusBarMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            MainStatusBar.Visibility = StatusBarMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void StatusConnection_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ConnectServer_Click(sender, new RoutedEventArgs());

    /// <summary>Layer list actions need a connection, a login and the right to view drawings.</summary>
    private bool CanReloadLayers()
    {
        if (!isConnected || currentUser is null)
        {
            StatusText.Text = "Connect to the server and log in first.";
            return false;
        }

        if (!Require(Permissions.DrawingView, "view drawings"))
        {
            return false;
        }

        if (isEditMode)
        {
            MessageBox.Show(this, "Exit edit mode (Save or Discard) before reloading layers.", "Layers", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private async void LoadLayers_Click(object sender, RoutedEventArgs e)
    {
        if (CanReloadLayers())
        {
            await PromptLayerSelectionAndLoadAsync();
        }
    }

    private async void RefreshLayers_Click(object sender, RoutedEventArgs e)
    {
        if (CanReloadLayers())
        {
            await LoadImportedPolygonsAsync();
        }
    }

    private void ShowAllLayers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerChecksByName.Values)
        {
            checkBox.IsChecked = true;
        }
    }

    private void HideAllLayers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerChecksByName.Values)
        {
            checkBox.IsChecked = false;
        }
    }

    private bool TryGetSelectedLayer(out string layerName)
    {
        layerName = selectedLayerName ?? string.Empty;
        if (selectedLayerName is null)
        {
            StatusText.Text = "Select a layer in the Layers panel first.";
            return false;
        }

        return true;
    }

    private async void SelectedLayerStyle_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            await OpenLayerStyleAsync(layerName);
        }
    }

    private void SelectedLayerZoom_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            ZoomToLayer(layerName);
        }
    }

    private async void SelectedLayerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            await ToggleLayerEditAsync(layerName);
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
        UpdateCursorCoordinates(e.GetPosition(MapCanvas));
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
        // Edit mode keeps a crosshair; view mode uses the normal arrow.
        MapCanvas.Cursor = isEditMode ? System.Windows.Input.Cursors.Cross : System.Windows.Input.Cursors.Arrow;
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
        await ToggleLayerEditAsync(layerName);
    }

    private async Task ToggleLayerEditAsync(string layerName)
    {
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
        if (!Require(Permissions.DrawingEdit, "edit drawings"))
        {
            return;
        }

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

            // A selection highlight left over from view mode is drawn on the base map at the saved
            // position; it would stay behind as a ghost while the object is moved or rotated.
            ClearSelectionHighlight();
            SelectedObjectText.Text = "No object selected";
            ClearPropertyDetails();
            selectedEditId = null;
            selectedEditIds.Clear();
            GroupButton.IsEnabled = false;
            isVertexEditMode = false;
            ClearVertexHandles();
            SetToggleState(VertexEditButton, false);
            VertexEditButton.IsEnabled = false;
            DuplicateButton.IsEnabled = false;
            DeleteObjectButton.IsEnabled = false;
            activeEditLayerName = layerName;
            isEditMode = true;
            SetToggleState(EditModeButton, true);
            MapCanvas.Cursor = System.Windows.Input.Cursors.Cross;
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
        ForgetEditableStrokeWidths();
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
        UpdateSelectionCount();
        GroupButton.IsEnabled = false;
        SetToggleState(EditModeButton, false);
        MapCanvas.Cursor = System.Windows.Input.Cursors.Arrow;
        ImportButton.IsEnabled = CanImport;
        DeleteImportButton.IsEnabled = CanImport;
        DuplicateButton.IsEnabled = false;
        DeleteObjectButton.IsEnabled = false;
        SetToggleState(VertexEditButton, false);
        VertexEditButton.IsEnabled = false;
        SaveEditsButton.IsEnabled = false;
        DiscardEditsButton.IsEnabled = false;
        EditStatusText.Text = string.Empty;
        SelectedObjectText.Text = "No object selected";
        ClearPropertyDetails();
        activeEditLayerName = null;
        UpdateLayerSwatchEditState();
        ScheduleSyncCheck();
    }

    private void ForgetEditableStrokeWidths()
    {
        foreach (var shape in editableShapesById.Values.SelectMany(shapes => shapes))
        {
            baseStrokeWidths.Remove(shape);
        }

        foreach (var detail in editableDetailPathById.Values)
        {
            baseStrokeWidths.Remove(detail);
        }
    }

    private void RenderEditableLayerObjects()
    {
        ForgetEditableStrokeWidths();
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
    private void ApplyEditableShapeStyle(Shape shape, bool isSelected, bool isNew)
    {
        shape.Stroke = isSelected ? Brushes.Gold : (isNew ? Brushes.MediumSeaGreen : Brushes.DodgerBlue);
        var width = isSelected ? 2.0 : 1.0;
        // Registered so the outline keeps a constant on-screen width when zooming.
        baseStrokeWidths[shape] = width;
        shape.StrokeThickness = width / Math.Max(mapScale.ScaleX, 0.1);
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

        var detailLayer = layerName ?? "(no layer)";
        var color = GetLayerColor(detailLayer);
        var thickness = GetLayerThickness(detailLayer);
        var detail = new Path
        {
            Data = streamGeometry,
            Stroke = new SolidColorBrush(color),
            Fill = null,
            IsHitTestVisible = false
        };
        ApplyRoundStroke(detail);
        // Kept at a constant on-screen width while zooming, like the base map lines.
        baseStrokeWidths[detail] = thickness;
        detail.StrokeThickness = thickness / Math.Max(mapScale.ScaleX, 0.1);
        return detail;
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
        if (!isEditMode || selectedEditId is null || SelectedShapes().Count == 0)
        {
            return;
        }

        e.Handled = true;
        RotateSelected(clockwise: true);
    }

    /// <summary>
    /// Turns the whole selection by 90 degrees about the center of its combined bounds: clockwise
    /// (the gold handle, or the "r" key) or counter-clockwise (Shift+R). Refused, with the usual
    /// "장비영역 겹침" error, when the turned equipment would overlap another piece of equipment.
    /// </summary>
    private void RotateSelected(bool clockwise)
    {
        var shapes = SelectedShapes();
        if (!isEditMode || selectedEditId is null || shapes.Count == 0)
        {
            return;
        }

        var allPoints = shapes.SelectMany(s => s.Points).ToArray();
        var centerScreen = new System.Windows.Point(
            (allPoints.Min(p => p.X) + allPoints.Max(p => p.X)) / 2,
            (allPoints.Min(p => p.Y) + allPoints.Max(p => p.Y)) / 2);
        var pivotWorld = UnprojectFromScreen(centerScreen);

        // World space is counter-clockwise-positive (screen Y is flipped), so clockwise is -90.
        var ccwDegrees = clockwise ? -90.0 : 90.0;
        var rotation = AffineTransformation.RotationInstance(ccwDegrees * Math.PI / 180.0, pivotWorld.X, pivotWorld.Y);
        if (RejectEquipmentOverlap(selectedEditIds, geometry => rotation.Transform(geometry)))
        {
            return;
        }

        // Every selected object turns about the shared center, so a group rotates rigidly.
        foreach (var selectedId in selectedEditIds.ToArray())
        {
            var edit = GetOrCreateEdit(selectedId);
            edit.CurrentGeometry = rotation.Transform(edit.CurrentGeometry);
            edit.CurrentOuterGeometry = rotation.Transform(edit.CurrentOuterGeometry);
            ApplyPlacementRotation(edit, pivotWorld.X, pivotWorld.Y, ccwDegrees);
            edit.HasChanges = true;
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = clockwise ? "Rotated 90° clockwise." : "Rotated 90° counter-clockwise.";
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
                if (RejectEquipmentOverlap(selectedEditIds, geometry => translation.Transform(geometry)))
                {
                    // Dropped on top of another machine: snap back to where it was picked up.
                    RenderEditableLayerObjects();
                    return;
                }

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

    // ------------------------------------------------------------------ equipment overlap rule

    private static bool RectanglesOverlap(Envelope a, Envelope b)
    {
        // Touching edges are allowed; only a real overlap area counts.
        const double tolerance = 1e-6;
        var width = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        var height = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        return width > tolerance && height > tolerance;
    }

    /// <summary>
    /// The equipment area of every block object in the edit session (its dashed rectangle),
    /// using the current, possibly unsaved position and skipping deleted objects.
    /// </summary>
    private List<(long Id, Envelope Bounds)> EquipmentAreas(ISet<long>? exclude = null)
    {
        var areas = new List<(long, Envelope)>();
        foreach (var id in editableSourcesById.Keys.Union(pendingEdits.Keys))
        {
            if (exclude is not null && exclude.Contains(id))
            {
                continue;
            }

            pendingEdits.TryGetValue(id, out var edit);
            if (edit is { IsDeleted: true })
            {
                continue;
            }

            var source = GetEditableSource(id);
            if (source.FeatureType != "block_instance")
            {
                continue;
            }

            areas.Add((id, BlockBounds(edit?.CurrentGeometry ?? source.Geometry, edit?.CurrentOuterGeometry ?? source.OuterGeometry)));
        }

        return areas;
    }

    private string DescribeEquipment(long id)
    {
        var source = GetEditableSource(id);
        var name = string.IsNullOrWhiteSpace(source.BlockName) ? "block" : source.BlockName;
        return id < 0 ? $"{name} (new)" : $"{name} #{(IsEncodedBlockInstanceId(id) ? DecodeBlockInstanceId(id) : id)}";
    }

    private void ReportEquipmentOverlap(long first, long second, string consequence)
    {
        var detail = $"{DescribeEquipment(first)}  <->  {DescribeEquipment(second)}";
        EditStatusText.Text = $"장비영역 겹침: {detail}";
        MessageBox.Show(this,
            "장비영역 겹침" + Environment.NewLine + Environment.NewLine + detail + Environment.NewLine + Environment.NewLine + consequence,
            "장비영역 겹침", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Equipment cannot overlap. Checks where the moving block objects would end up after
    /// <paramref name="transform"/> against every other block object; on overlap reports the error
    /// and returns true so the caller keeps the original position.
    /// </summary>
    private bool RejectEquipmentOverlap(IReadOnlyCollection<long> movingIds, Func<NetTopologySuite.Geometries.Geometry, NetTopologySuite.Geometries.Geometry> transform)
    {
        var moving = movingIds.ToHashSet();
        var others = EquipmentAreas(moving);
        foreach (var id in moving)
        {
            var source = GetEditableSource(id);
            if (source.FeatureType != "block_instance")
            {
                continue;
            }

            pendingEdits.TryGetValue(id, out var edit);
            var bounds = BlockBounds(
                transform(edit?.CurrentGeometry ?? source.Geometry),
                transform(edit?.CurrentOuterGeometry ?? source.OuterGeometry));
            foreach (var (otherId, otherBounds) in others)
            {
                if (RectanglesOverlap(bounds, otherBounds))
                {
                    ReportEquipmentOverlap(id, otherId, "설비는 서로 겹칠 수 없어 원래 위치로 되돌렸습니다.");
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Blocks saving when any new or changed equipment still overlaps another one.</summary>
    private bool HasEquipmentOverlapBeforeSave()
    {
        var areas = EquipmentAreas();
        foreach (var (id, bounds) in areas)
        {
            if (!pendingEdits.TryGetValue(id, out var edit) || !(edit.IsNew || edit.HasChanges))
            {
                continue;
            }

            foreach (var (otherId, otherBounds) in areas)
            {
                if (otherId != id && RectanglesOverlap(bounds, otherBounds))
                {
                    ReportEquipmentOverlap(id, otherId, "겹친 설비를 옮긴 뒤 다시 저장하세요. 저장하지 않았습니다.");
                    return true;
                }
            }
        }

        return false;
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
            SetToggleState(VertexEditButton, false);
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
            ? "Multi-selection: drag any selected shape to move all, click the gold handle or press r to rotate all 90° clockwise (Shift+R: counter-clockwise).\nCtrl+click toggles an object, drag on empty space to box-select.\nGroup turns the selection into one block."
            : isBlock
                ? "Block instance: moves, rotates, duplicates and deletes as one object.\nDrag the shape to move it.\nr: rotate 90° clockwise, Shift+R: counter-clockwise (or click the gold handle)."
                : "Drag the shape to move it.\nr: rotate 90° clockwise, Shift+R: counter-clockwise (or click the gold handle).\nToggle Vertex edit to reshape the outline.";
        var shownAttributes = pendingEdits.TryGetValue(id, out var attributeEdit) ? attributeEdit.Attributes : source.Attributes;
        AttributesGrid.ItemsSource = shownAttributes.Select(attribute => new AttributeRow(attribute.Key, attribute.Value)).ToArray();
        DuplicateButton.IsEnabled = true;
        DeleteObjectButton.IsEnabled = true;
        GroupButton.IsEnabled = isMulti;
        // Reshaping only the outline would desynchronize it from the block's real geometry.
        VertexEditButton.IsEnabled = !isBlock && !isMulti;
        VertexEditButton.ToolTip = isBlock ? "Vertex edit is not available for block instances" : "Vertex edit";
        UpdateSelectionCount();
    }

    private void DeselectEditableObject()
    {
        if (selectedEditId is null)
        {
            return;
        }

        selectedEditId = null;
        selectedEditIds.Clear();
        UpdateSelectionCount();
        RemoveRotateHandle();
        isVertexEditMode = false;
        ClearVertexHandles();
        SetToggleState(VertexEditButton, false);
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
            ToolTip = "Rotate 90° clockwise (r). Shift+R rotates counter-clockwise."
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
        SetToggleState(VertexEditButton, isVertexEditMode);
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
        if (!Require(Permissions.DrawingDelete, "delete drawing objects"))
        {
            return;
        }

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
        if (!Require(Permissions.DrawingEdit, "save drawing changes"))
        {
            return;
        }

        if (HasEquipmentOverlapBeforeSave())
        {
            return;
        }

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
            var conflictIds = await repository.ApplyLayerEditsAsync(updates, inserts, deletes, changeContext: CurrentChangeContext);
            var appliedUpdates = updates.Count - conflictIds.Count(id => updates.Any(u => u.Id == id));
            var appliedDeletes = deletes.Count - conflictIds.Count(id => deletes.Any(d => d.Id == id));
            var summary = $"Saved edits: {appliedUpdates} updated, {inserts.Count} added, {appliedDeletes} deleted.";
            if (conflictIds.Count > 0)
            {
                summary += $" {conflictIds.Count} object(s) were changed by someone else in the meantime and were NOT overwritten (ids: {string.Join(", ", conflictIds)}) — reload shows their latest state.";
            }

            ExitEditMode();
            StatusText.Text = summary;
            await AuditAsync("drawing.save", "drawing", null,
                $"{appliedUpdates} updated, {inserts.Count} added, {appliedDeletes} deleted, {conflictIds.Count} conflict(s)");
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
            HandleShortcut(e);
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
        UpdateZoomText();
        ScheduleStrokeWidthUpdate();
    }

    private void ResetView()
    {
        mapScale.ScaleX = 1;
        mapScale.ScaleY = 1;
        mapTranslation.X = 0;
        mapTranslation.Y = 0;
        UpdateZoomText();
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
        if (!Require(Permissions.ImportDxf, "import DXF data"))
        {
            return;
        }

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
                await repository.DeleteImportedFileAsync(filePath, cancellationToken, CurrentChangeContext);
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
                cancellationToken,
                CurrentChangeContext);
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
            await AuditAsync("dxf.import", "dxf", filePath,
                singleLayer ? $"{instances.Count} Equipment block object(s)" : $"{blocks.Count} layer(s), {instances.Count} block instance(s)");
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
            ImportButton.IsEnabled = CanImport;
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
        if (!Require(Permissions.ImportDxf, "delete imported DXF data"))
        {
            return;
        }

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

            await repository.DeleteImportedFileAsync(filePath, changeContext: CurrentChangeContext);
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
            await AuditAsync("dxf.delete", "dxf", filePath, "all layers and block objects");
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

    /// <summary>The connection is edited in the server window; other flows just use the current settings.</summary>
    private PostgresConnectionSettings ReadConnectionSettingsFromUi() => connectionSettings;
}
