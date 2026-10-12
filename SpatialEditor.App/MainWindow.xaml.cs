// =====================================================================================================
// MainWindow.xaml.cs - OmniDT SE(설비 배치도 DXF 편집기) 메인 창 코드비하인드 (전체 흐름도)
// =====================================================================================================
// 이 클래스는 partial 이며, 다른 부분은 MainWindow.Settings.cs(앱 설정/단축키 처리 HandleShortcut),
// MainWindow.Toolbar.cs(툴바 상태), MainWindow.Undo.cs(PushUndo/CancelEdits), MainWindow.Versions.cs(도면/버전,
// 비교 오버레이, 자동 버전), MainWindow.Export.cs(DXF 내보내기, 레이어 삭제), MainWindow.Capture.cs(화면 캡처)에 있다.
//
// [1] 시작 / 로그인 / 로드 흐름
//   MainWindow() 생성자(변환/타이머/이벤트 연결, InitializeToolbarState)
//     -> Loaded: MainWindow_Loaded -> InitializeSettings(앱 설정) -> PostgresConnectionSettings.LoadAsync(연결 설정)
//     -> ConnectOnStartupAsync : 저장된 설정으로 접속 테스트
//          (설정이 없거나 실패) -> ShowServerDialog(서버 연결 창) -> 사용자가 수정한 설정 사용
//     -> CompleteConnectionAsync : currentUser 초기화, activeLayoutId=1, ClearMapAndLayers, SetConnected(true)
//     -> LoginAndLoadAsync -> LoginAsync : 사용자 스키마 보장 -> 자동 로그인 시도 / LoginWindow
//          -> 비밀번호 강제 변경 반복 -> 마지막 로그인 정보 저장 -> ApplyUserState(권한별 버튼 활성화)
//     -> (DrawingView 권한 있음) EnsureDrawingSchemasAsync -> StartSyncAsync(다중 사용자 동기화 시작)
//     -> PromptLayerSelectionAndLoadAsync : 레이어 목록 조회 -> LayerSelectionWindow -> 선택 저장
//     -> LoadImportedPolygonsAsync : 동기화 기준(baseline) 초기화 -> 외곽 폴리곤 + 블록 인스턴스 조회
//     -> RenderImportedGeometries : 레이어별 StreamGeometry 단일 Path 로 렌더링 -> ResetView
//     -> PopulateLayerPanel(레이어 패널) / ScheduleMiniMapRebuild(미니맵) / RefreshLayoutStatusAsync
//
// [2] 보기(뷰) 모드 상호작용
//   마우스 휠 -> MapCanvas_MouseWheel -> ZoomAt -> ScheduleStrokeWidthUpdate(40ms 디바운스) -> UpdateStrokeWidths
//   가운데 버튼 드래그 -> MapCanvas_MouseDown/Move/Up : 패닝(mapTranslation)
//   왼쪽 클릭 -> TrySelectFeatureAt(월드 좌표 역변환, 레이어 Z-순서/거리/블록 우선) -> SelectFeature -> HighlightFeature
//   레이어 체크/드래그/우클릭/더블클릭 -> LayerVisibilityChanged / LayerRow_* / OpenLayerStyleAsync / ApplyLayerStyle
//
// [3] 편집 모드 흐름
//   편집 버튼 또는 레이어 색 견본 클릭 -> ToggleEditMode_Click / LayerSwatch_MouseLeftButtonDown -> ToggleLayerEditAsync
//     -> EnterEditModeForLayerAsync : 편집 가능 객체/블록 인스턴스 조회 -> editableSourcesById 등록(블록 id 인코딩)
//        -> ApplyEditMask(기반 지도 숨김) -> RenderEditableLayerObjects -> DrawEditableObject(상세 경로 + 투명 외곽 폴리곤)
//   [선택]   EditableShape_MouseLeftButtonDown -> SelectEditableObject(대표/다중 선택, 회전 핸들, 속성 패널)
//            빈 곳 드래그 -> BeginBoxSelect -> FinalizeBoxSelect(블록=교차, 일반=완전 포함)
//   [이동]   BeginMoveDrag -> MapCanvas_MouseMove -> UpdateEditDragPreview(미리보기, 격자 스냅 ComputeMoveDelta)
//            -> MapCanvas_MouseUp -> FinalizeEditDrag -> RejectEquipmentOverlap(겹침 금지) -> PushUndo
//            -> GetOrCreateEdit 로 PendingLayerEdit 갱신(CurrentGeometry/CurrentOuterGeometry, ApplyPlacementMove)
//   [미세이동] Window_PreviewKeyDown -> TryNudgeFromKey (Ctrl+방향키)
//   [회전]   회전 핸들 클릭 / 단축키 -> RotateSelected(90도, 선택 전체의 합친 경계 중심 기준, ApplyPlacementRotation)
//   [복제]   DuplicateSelected_Click(임시 음수 id, IsNew)   [그룹] GroupSelected_Click(볼록 껍질 + 새 블록)
//   [삭제]   DeleteSelected_Click(IsDeleted 표시)
//   [정점편집] ToggleVertexEdit_Click -> RenderVertexHandles -> (드래그/삭제/중점 삽입) -> FinalizeVertexDrag
//   [되돌리기] Esc -> Window_PreviewKeyDown -> CancelEdits(MainWindow.Undo.cs) / PushUndo 로 Undo 스냅샷 기록
//
// [4] 저장 / 동기화
//   SaveEdits_Click -> SaveEditsAsync : 겹침 검사(HasEquipmentOverlapBeforeSave) -> 갱신/추가/삭제 분류
//     -> repository.ApplyLayerEditsAsync(ExpectedUpdatedAt 낙관적 충돌 검사, CurrentChangeContext 기록)
//     -> ExitEditMode -> CreateAutoVersionAsync -> AuditAsync -> ReloadForSyncAsync -> EnterEditModeForLayerAsync(편집 계속)
//   다른 세션 저장 -> SyncCoordinator.CheckRequested -> CheckForSyncChangesAsync -> (편집 중이면 보류) 갱신 여부 질문
//     -> ReloadForSyncAsync(줌/이동/숨김 레이어 유지) ; ExitEditMode 후에는 ScheduleSyncCheck 로 보류분 확인
//   종료/폐기: DiscardEdits_Click / ExitEditMode(편집 상태 전체 정리), Logout_Click(미저장 편집 확인 후 재로그인)
//
// [5] DXF 가져오기 / 삭제
//   ImportDxf_Click : 권한 확인 -> DXF 파싱(백그라운드) -> 기존 데이터 확인/삭제 -> repository.ImportAsync(진행률/취소)
//     -> LogImportCounts(DXF 건수 vs DB 건수 감사 로그) -> LoadImportedPolygonsAsync    /  DeleteImportedDxf_Click
//
// [영역(#region 성격)별 구성]
//   1) 필드/상수/스타일 도우미 ........ 레이어 색/두께/채움, 편집 상태 필드, 블록 id 인코딩, PendingLayerEdit
//   2) 생성자, 연결/로그인/권한 ........ ApplyUserState, LoginAsync, Logout/ChangePassword, 감사 로그
//   3) 미니맵 ............................ RebuildMiniMap, UpdateMiniMapViewport, 미니맵 마우스 핸들러
//   4) 다중 사용자 동기화 ................ StartSync/StopSync, CheckForSyncChangesAsync, ReloadForSyncAsync
//   5) 라이브러리/사용자 관리/서버 연결 ... OpenLibrary, RegisterInLibrary, ManageUsers, ConnectServer
//   6) 로드/렌더링/좌표 변환 ............. LoadImportedPolygonsAsync, RenderImportedGeometries, ProjectToScreen
//   7) 뷰 모드 선택/강조 ................. TrySelectFeatureAt, HighlightFeature
//   8) 레이어 패널 ........................ PopulateLayerPanel, 컨텍스트 메뉴, 순서 변경, 스타일/이름 변경
//   9) 메뉴/상태바/뷰 조작 ............... 화면 복사, 설명서, 패널 표시, 줌/패닝, 마우스 이벤트
//  10) 편집 모드 ........................... 진입/종료, 오버레이 그리기, 선택, 이동/회전/미세이동, 박스 선택
//  11) 설비 영역 겹침 규칙 ................ RectanglesOverlap, EquipmentAreas, RejectEquipmentOverlap
//  12) 정점 편집/복제/그룹/삭제 ........... 핸들, 스냅, 복제, 그룹, 삭제
//  13) 저장/취소/키 처리 .................. SaveEditsAsync, Window_PreviewKeyDown, DiscardEdits_Click
//  14) 줌/선 두께 .......................... ZoomAt, ResetView, UpdateStrokeWidths
//  15) DXF 가져오기/삭제/로그 ............ ImportDxf_Click, DeleteImportedDxf_Click, LogImportCounts
// =====================================================================================================
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
/// OmniDT SE(설비 배치도 DXF 편집기)의 메인 창 코드비하인드(partial class).
/// 이 파일은 지도 캔버스 렌더링, 레이어 패널, 편집 모드(선택/이동/회전/복제/정점 편집),
/// 저장·동기화, 로그인/권한, 도면/버전 관리 등 UI 로직 대부분을 담는다.
/// 데이터 접근은 SpatialEditor.Infrastructure(저장소), 지오메트리 연산은 SpatialEditor.Geometry /
/// NetTopologySuite, DXF 파싱은 SpatialEditor.Cad 가 담당한다.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>도형 연산(변환/회전/겹침 검사 등)을 제공하는 지오메트리 서비스.</summary>
    private readonly GeometryService geometryService = new();
    /// <summary>현재 DB 접속 설정(호스트/포트/DB명, 마지막 로그인 ID, 레이어 스타일 등). 파일로 저장/복원된다.</summary>
    private PostgresConnectionSettings connectionSettings = new();
    /// <summary>표시할 레이어 이름 필터. null 이면 전체 레이어를 표시한다.</summary>
    private List<string>? activeLayerFilter;
    /// <summary>지도 확대/축소 변환(마우스 휠). mapTranslation 과 함께 TransformGroup 으로 묶여 캔버스에 적용된다.</summary>
    private readonly ScaleTransform mapScale = new(1, 1);
    /// <summary>지도 이동(패닝) 변환.</summary>
    private readonly TranslateTransform mapTranslation = new(0, 0);
    /// <summary>현재 화면에 로드된(DXF 임포트 포함) 공간 피처 목록.</summary>
    private readonly List<SpatialFeature> importedFeatures = new();
    /// <summary>도형별 기준 선 두께(월드 단위). 줌 배율에 맞춰 화면 두께를 일정하게 유지할 때 사용한다.</summary>
    private readonly Dictionary<Shape, double> baseStrokeWidths = new();
    /// <summary>레이어 이름 → 해당 레이어에 속한 WPF 도형 목록(표시/숨김, Z-순서 처리용).</summary>
    private readonly Dictionary<string, List<Shape>> shapesByLayer = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 이름 → Z-순서 값(클수록 위에 그려짐).</summary>
    private readonly Dictionary<string, int> layerZOrder = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>숨김 처리된 레이어 이름 집합.</summary>
    private readonly HashSet<string> hiddenLayers = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>임포트 과정을 기록하는 감사(Audit) 로거.</summary>
    private readonly ImportAuditLogger importLogger = new();
    /// <summary>줌 중 선 두께 갱신을 40ms 단위로 모아서(디바운스) 처리하는 타이머. 휠 이벤트마다 전체 갱신하는 비용을 줄인다.</summary>
    private readonly DispatcherTimer strokeWidthUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    /// <summary>Z-순서 증가용 카운터.</summary>
    private int zOrderCounter;
    /// <summary>뷰어 모드에서 선택된 피처를 강조 표시하는 도형.</summary>
    private Shape? selectionHighlight;
    /// <summary>선택된 피처가 속한 레이어 이름.</summary>
    private string? selectedFeatureLayer;
    /// <summary>뷰어 모드에서 선택된 피처.</summary>
    private SpatialFeature? selectedFeature;
    /// <summary>마지막으로 렌더링한 데이터의 경계 상자(월드 좌표). 미니맵/화면 맞춤 계산에 사용.</summary>
    private Envelope? renderedEnvelope;
    /// <summary>렌더링 시 사용한 캔버스 너비(픽셀).</summary>
    private double renderedWidth;
    /// <summary>렌더링 시 사용한 캔버스 높이(픽셀).</summary>
    private double renderedHeight;
    /// <summary>렌더링 시 사용한 여백(픽셀).</summary>
    private double renderedPadding;
    /// <summary>월드 좌표 → 화면 좌표 변환 배율(렌더링 시 확정).</summary>
    private double renderedScale;
    /// <summary>패닝(드래그 이동) 진행 중 여부.</summary>
    private bool isPanning;
    /// <summary>패닝 시작 시점의 마우스 위치(캔버스 기준 화면 좌표).</summary>
    private System.Windows.Point panStart;
    /// <summary>패닝 시작 시점의 mapTranslation.X 값.</summary>
    private double panStartX;
    /// <summary>패닝 시작 시점의 mapTranslation.Y 값.</summary>
    private double panStartY;
    /// <summary>DXF 임포트 작업 취소용 토큰 소스. 진행 중이 아니면 null.</summary>
    private CancellationTokenSource? importCts;

    /// <summary>편집 모드 진입 여부.</summary>
    private bool isEditMode;
    /// <summary>편집 가능한 원본 객체(id → 원본). 편집 모드 진입 시 DB 에서 읽어 채운다.</summary>
    private readonly Dictionary<long, EditableLayerObject> editableSourcesById = new();
    /// <summary>저장 전 미커밋 편집 내용(id → 편집 상태). 저장/되돌리기 시 비워진다.</summary>
    private readonly Dictionary<long, PendingLayerEdit> pendingEdits = new();
    /// <summary>편집 객체 id → 화면에 그려진 WPF 폴리곤 목록(외곽/구멍 등 여러 개일 수 있음).</summary>
    private readonly Dictionary<long, List<System.Windows.Shapes.Polygon>> editableShapesById = new();
    /// <summary>새로 생성한 객체(복제 등)에 부여하는 임시 id. 음수로 감소시켜 DB id(양수)와 충돌하지 않게 한다.</summary>
    private long nextTempId = -1;
    /// <summary>대표(primary) 선택 객체 id. 속성 패널에 표시되는 객체이며 없으면 null.</summary>
    private long? selectedEditId;
    // 속성 패널에 표시되는 대표(primary) 객체 selectedEditId 를 포함한, 선택된 모든 객체의 id 집합.
    /// <summary>다중 선택된 모든 객체의 id 집합.</summary>
    private readonly HashSet<long> selectedEditIds = new();
    /// <summary>드래그 박스 선택 시 화면에 그리는 선택 사각형.</summary>
    private System.Windows.Shapes.Rectangle? boxSelectRectangle;
    /// <summary>박스 선택 시작점(캔버스 화면 좌표).</summary>
    private System.Windows.Point boxSelectStart;
    /// <summary>박스 선택이 기존 선택에 추가(Shift/Ctrl 등)되는 방식인지 여부.</summary>
    private bool boxSelectAdditive;
    /// <summary>회전 핸들(선택 객체 위쪽에 표시되는 원형 손잡이).</summary>
    private Ellipse? rotateHandle;
    /// <summary>회전 핸들과 객체 중심을 잇는 보조선.</summary>
    private Line? rotateHandleLine;
    /// <summary>현재 진행 중인 편집 드래그의 종류(이동/정점/박스).</summary>
    private EditDragKind editDragKind;
    /// <summary>편집 드래그 시작 시점의 기준 위치(캔버스 좌표).</summary>
    private System.Windows.Point editDragAnchor;
    /// <summary>드래그 시작 시점의 각 폴리곤 점 스냅샷. 드래그 중 누적 오차 없이 원본 기준으로 변위를 적용하기 위해 보관한다.</summary>
    private readonly Dictionary<System.Windows.Shapes.Polygon, System.Windows.Point[]> editShapeSnapshot = new();
    /// <summary>회전 핸들 위치 등 핸들의 드래그 시작 스냅샷.</summary>
    private System.Windows.Point[]? editHandleSnapshot;
    /// <summary>정점 편집 모드 여부(true 면 선택 객체의 정점/중점 핸들을 표시).</summary>
    private bool isVertexEditMode;
    /// <summary>정점 편집용 사각형 핸들 목록.</summary>
    private readonly List<System.Windows.Shapes.Rectangle> vertexHandles = new();
    /// <summary>정점 사이에 정점을 추가하기 위한 중점(원형) 핸들 목록.</summary>
    private readonly List<Ellipse> midpointHandles = new();
    /// <summary>드래그 중인 정점(링 인덱스, 정점 인덱스). 없으면 null.</summary>
    private (int RingIndex, int PointIndex)? draggingVertex;
    /// <summary>정점 스냅 허용 거리(화면 픽셀).</summary>
    private const double VertexSnapTolerance = 10.0;
    /// <summary>격자 스냅 사용 여부.</summary>
    private bool isGridSnapEnabled;
    /// <summary>격자 스냅 간격(월드 단위).</summary>
    private const double GridSnapSizeWorldUnits = 10.0;
    /// <summary>편집 객체 id → 블록 객체의 상세(내부 선/호 등) 경로. 객체를 이동/회전할 때 함께 변환한다.</summary>
    private readonly Dictionary<long, Path> editableDetailPathById = new();

    // spatial_features 와 spatial_block_instances 는 각각 독립된 id 시퀀스를 가지므로,
    // 블록 인스턴스의 원본 id 가 편집 모드의 공용 long 키 딕셔너리에서 spatial_features id 와 충돌할 수 있다.
    // 그래서 블록 인스턴스 id 에 이 상위 비트 플래그(실제 id 는 이 값에 근접하지 않음)를 붙여,
    // 기존의 모든 id 기반 조회가 그대로 동작하게 한다. 저장 단계에서만 이 플래그를 해독해
    // 다른 테이블로 라우팅하면 된다.
    /// <summary>블록 인스턴스 id 를 구분하기 위한 상위 비트(2^62) 플래그.</summary>
    private const long BlockInstanceIdFlag = 1L << 62;
    /// <summary>블록 인스턴스의 원본 id 에 구분 플래그를 붙여 편집용 id 로 인코딩한다.</summary>
    /// <param name="rawId">spatial_block_instances 테이블의 원본 id.</param>
    /// <returns>플래그가 설정된 편집용 id.</returns>
    private static long EncodeBlockInstanceId(long rawId) => rawId | BlockInstanceIdFlag;
    /// <summary>주어진 편집용 id 가 블록 인스턴스 id 인지 판별한다.</summary>
    /// <param name="id">편집용 id.</param>
    /// <returns>블록 인스턴스 id 이면 true.</returns>
    private static bool IsEncodedBlockInstanceId(long id) => (id & BlockInstanceIdFlag) != 0;
    /// <summary>인코딩된 편집용 id 에서 플래그를 제거해 DB 원본 id 를 복원한다.</summary>
    /// <param name="encodedId">플래그가 설정된 편집용 id.</param>
    /// <returns>spatial_block_instances 테이블의 원본 id.</returns>
    private static long DecodeBlockInstanceId(long encodedId) => encodedId & ~BlockInstanceIdFlag;

    /// <summary>레이어 자동 색상 팔레트(10색). 처음 등장한 레이어부터 순환하며 할당한다.</summary>
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

    /// <summary>레이어 이름 → 현재 적용 색상(사용자 지정 또는 팔레트 색).</summary>
    private readonly Dictionary<string, Color> layerColors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 이름 → 레이어 패널의 색상 견본 사각형.</summary>
    private readonly Dictionary<string, Rectangle> layerSwatchesByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 이름 → 레이어 패널의 행(Border).</summary>
    private readonly Dictionary<string, Border> layerRowsByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 패널에서 선택된 레이어 이름.</summary>
    private string? selectedLayerName;
    /// <summary>편집 대상(활성 편집) 레이어 이름. 새 객체는 이 레이어에 생성된다.</summary>
    private string? activeEditLayerName;

    /// <summary>
    /// 레이어마다 <see cref="LayerPalette"/> 에서 안정적이고 구분되는 색을 처음 만났을 때(팔레트 순환 순서로)
    /// 할당하고, 이후 지도 렌더링과 레이어 패널 견본에서 동일하게 재사용한다.
    /// 사용자 지정 색(LayerStyles)이 있으면 그 색이 우선하며, 기본 팔레트 색은 별도로 기록해 "기본값 복원"에 쓴다.
    /// </summary>
    /// <param name="layerName">레이어 이름(대소문자 무시).</param>
    /// <returns>해당 레이어의 선 색상.</returns>
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

    /// <summary>레이어 이름 → 팔레트가 정해 준 기본 색(사용자 지정 색을 초기화할 때 복원용).</summary>
    private readonly Dictionary<string, Color> defaultLayerColors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>기본 선 두께(레이어 스타일이 없을 때).</summary>
    private const double DefaultLineThickness = 1.0;

    /// <summary>레이어 스타일에 지정된 선 두께를 반환한다. 지정이 없거나 0 이하이면 기본 두께.</summary>
    /// <param name="layerName">레이어 이름.</param>
    /// <returns>선 두께.</returns>
    private double GetLayerThickness(string layerName) =>
        connectionSettings?.LayerStyles is { } styles && styles.TryGetValue(layerName, out var style) && style.Thickness > 0
            ? style.Thickness
            : DefaultLineThickness;

    /// <summary>레이어 스타일의 채움 불투명도(0~1)를 반환한다. 지정이 없으면 기본값 60/255.</summary>
    /// <param name="layerName">레이어 이름.</param>
    /// <returns>0~1 범위의 불투명도.</returns>
    private double GetLayerFillOpacity(string layerName) =>
        connectionSettings?.LayerStyles is { } styles && styles.TryGetValue(layerName, out var style)
            ? Math.Clamp(style.FillOpacity, 0, 1)
            : 60.0 / 255.0;

    /// <summary>
    /// 레이어의 폴리곤 채움 색(사용자 지정 채움색 또는 선 색)에 레이어의 채움 불투명도를 알파로 적용해 반환한다.
    /// </summary>
    /// <param name="layerName">레이어 이름.</param>
    /// <returns>알파가 적용된 채움 색.</returns>
    private Color GetLayerFill(string layerName)
    {
        var baseColor = GetLayerColor(layerName);
        if (connectionSettings?.LayerStyles is { } styles && styles.TryGetValue(layerName, out var style)
            && TryParseHexColor(style.FillColor, out var custom))
        {
            baseColor = custom;
        }

        return Color.FromArgb((byte)Math.Round(GetLayerFillOpacity(layerName) * 255), baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>"#RRGGBB" 형식 문자열을 Color 로 변환한다.</summary>
    /// <param name="hex">입력 문자열(null 가능). 길이 7 이고 '#'으로 시작해야 한다.</param>
    /// <param name="color">성공 시 변환된 색, 실패 시 default.</param>
    /// <returns>변환 성공 여부.</returns>
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
    /// 둥근(Round) 조인/캡을 적용해, 맞닿은 선분(별개의 DXF 선, 폴리곤 모서리)이 확대하여 두껍게 그려도
    /// 하나의 연속된 선처럼 보이게 한다. WPF 기본값(마이터 조인, 평평한 캡)은 그 지점에 틈과 홈이 생긴다.
    /// </summary>
    /// <param name="shape">스트로크 속성을 설정할 도형.</param>
    private static void ApplyRoundStroke(Shape shape)
    {
        shape.StrokeLineJoin = PenLineJoin.Round;
        shape.StrokeStartLineCap = PenLineCap.Round;
        shape.StrokeEndLineCap = PenLineCap.Round;
    }

    /// <summary>편집 모드의 마우스 드래그 종류.</summary>
    private enum EditDragKind
    {
        /// <summary>드래그 없음.</summary>
        None,
        /// <summary>선택 객체 이동(회전 핸들 드래그 포함).</summary>
        Move,
        /// <summary>정점 하나를 드래그.</summary>
        Vertex,
        /// <summary>빈 곳에서 드래그하여 박스 선택.</summary>
        Box
    }

    /// <summary>
    /// 객체의 현재 편집 상태를 누적 변환이 아니라 "현재 지오메트리" 그대로 추적한다.
    /// 이동/회전과 정점 직접 편집이 자유롭게 섞여도, 변환과 별개로 재구성된 윤곽을 서로
    /// 맞추는 과정이 필요 없기 때문이다. 저장 시 이 정보로 INSERT/UPDATE/DELETE 를 결정한다.
    /// </summary>
    private sealed class PendingLayerEdit
    {
        /// <summary>편집 시작 시점의 DB 원본 객체.</summary>
        public required EditableLayerObject Source { get; init; }
        /// <summary>새로 생성(복제 등)된 객체인지 여부. true 면 저장 시 INSERT.</summary>
        public bool IsNew { get; init; }
        /// <summary>삭제 표시 여부. true 면 저장 시 DELETE.</summary>
        public bool IsDeleted { get; set; }
        /// <summary>원본 대비 변경이 있는지 여부(저장 대상 판단).</summary>
        public bool HasChanges { get; set; }
        /// <summary>현재 지오메트리(편집 반영).</summary>
        public required NetTopologySuite.Geometries.Geometry CurrentGeometry { get; set; }
        /// <summary>현재 외곽(outer) 지오메트리. 블록 객체의 바깥 윤곽 표시용.</summary>
        public required NetTopologySuite.Geometries.Geometry CurrentOuterGeometry { get; set; }
        /// <summary>현재 속성 값(키 → 값, null 가능). 블록 배치 정보(삽입점/회전)도 여기에 포함된다.</summary>
        public required Dictionary<string, string?> Attributes { get; set; }
    }

    /// <summary>속성 그리드의 한 행(키, 값).</summary>
    /// <param name="Key">속성 이름.</param>
    /// <param name="Value">속성 값(null 가능).</param>
    private sealed record AttributeRow(string Key, string? Value);

    /// <summary>숫자/날짜 문자열 변환에 사용하는 불변(Invariant) 문화권. 저장 값이 지역 설정에 영향받지 않게 한다.</summary>
    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>
    /// 지오메트리에 방금 적용한 강체 이동에 맞춰 블록 인스턴스의 배치 속성(삽입점)을 함께 갱신하여,
    /// 저장된 속성이 항상 블록의 실제 위치를 나타내도록 한다.
    /// </summary>
    /// <param name="edit">대상 편집 상태. block_instance 가 아니면 아무것도 하지 않는다.</param>
    /// <param name="dx">X 이동량(월드 단위).</param>
    /// <param name="dy">Y 이동량(월드 단위).</param>
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

    /// <summary>
    /// 지오메트리에 적용한 회전에 맞춰 블록 인스턴스의 삽입점(피벗 기준 회전)과 회전각 속성을 갱신한다.
    /// 회전은 DXF 규약(반시계 방향이 양수, 단위 도)을 따르며 회전각은 0~360 으로 정규화한다.
    /// </summary>
    /// <param name="edit">대상 편집 상태. block_instance 가 아니면 아무것도 하지 않는다.</param>
    /// <param name="pivotX">회전 중심 X(월드 단위).</param>
    /// <param name="pivotY">회전 중심 Y(월드 단위).</param>
    /// <param name="ccwDegrees">반시계 방향 회전량(도).</param>
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

    /// <summary>속성 사전에서 키의 값을 double 로 파싱한다(Invariant 문화권).</summary>
    /// <param name="attributes">속성 사전.</param>
    /// <param name="key">조회할 키.</param>
    /// <param name="value">성공 시 파싱된 값, 실패 시 0.</param>
    /// <returns>키가 있고 숫자로 파싱되면 true.</returns>
    private static bool TryGetDouble(IReadOnlyDictionary<string, string?> attributes, string key, out double value)
    {
        value = 0;
        return attributes.TryGetValue(key, out var text)
            && double.TryParse(text, System.Globalization.NumberStyles.Float, Invariant, out value);
    }

    /// <summary>속성 패널의 요약/힌트/속성 그리드를 비운다.</summary>
    private void ClearPropertyDetails()
    {
        PropertySummaryText.Text = string.Empty;
        PropertyHintText.Text = string.Empty;
        AttributesGrid.ItemsSource = null;
    }

    /// <summary>DB 원본 객체로부터 편집 상태(PendingLayerEdit)를 만든다. 속성은 원본의 복사본을 사용해 원본이 변하지 않게 한다.</summary>
    /// <param name="source">원본 편집 대상 객체(null 불가).</param>
    /// <param name="isNew">새로 생성한 객체인지 여부(기본 false).</param>
    /// <returns>초기 상태가 원본과 같은 편집 상태.</returns>
    private static PendingLayerEdit CreatePendingEdit(EditableLayerObject source, bool isNew = false) => new()
    {
        Source = source,
        IsNew = isNew,
        CurrentGeometry = source.Geometry,
        CurrentOuterGeometry = source.OuterGeometry,
        Attributes = new Dictionary<string, string?>(source.Attributes)
    };

    /// <summary>
    /// 생성자: 컴포넌트를 초기화하고, 복사-화면 단축키(모든 창 공통), 미니맵 갱신 이벤트,
    /// 지도/편집 캔버스 공용 변환, 선 두께 갱신 타이머, 툴바 상태를 설정한 뒤 Loaded 이벤트를 연결한다.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        BuildText.Text = BuildInfo.Label;
        // 화면 복사는 대화상자가 열려 있어도 동작해야 하므로, Shift/Caps Lock 여부와 무관하게 모든 창에 대해 처리한다.
        EventManager.RegisterClassHandler(typeof(Window), System.Windows.Input.Keyboard.PreviewKeyDownEvent, new System.Windows.Input.KeyEventHandler(CopyScreenKeyDown));
        mapScale.Changed += (_, _) => ScheduleMiniMapViewport();
        mapTranslation.Changed += (_, _) => ScheduleMiniMapViewport();
        MapCanvas.SizeChanged += (_, _) => ScheduleMiniMapViewport();
        // 지도 레이어 캔버스와 편집 객체 캔버스가 같은 변환을 공유해 항상 정확히 겹쳐 보이게 한다.
        var mapTransform = new TransformGroup
        {
            Children = { mapScale, mapTranslation }
        };
        MapLayerCanvas.RenderTransform = mapTransform;
        EditableObjectsCanvas.RenderTransform = mapTransform;
        // 줌 변경 후 40ms 동안 추가 변경이 없을 때 한 번만 선 두께를 갱신한다.
        strokeWidthUpdateTimer.Tick += (_, _) =>
        {
            strokeWidthUpdateTimer.Stop();
            UpdateStrokeWidths();
        };
        InitializeToolbarState();
        Loaded += MainWindow_Loaded;
    }

    /// <summary>DB 에 연결된 상태인지 여부.</summary>
    private bool isConnected;

    /// <summary>현재 로그인한 사용자. 로그인 전이면 null.</summary>
    private AppUser? currentUser;

    /// <summary>현재 사용자가 해당 권한을 가지고 있는지 확인한다.</summary>
    /// <param name="permission">확인할 권한 코드(Permissions 상수).</param>
    /// <returns>로그인되어 있고 권한이 있으면 true.</returns>
    private bool Has(string permission) => currentUser?.Has(permission) == true;

    /// <summary>DXF 임포트가 가능한지(연결 + 임포트 권한).</summary>
    private bool CanImport => isConnected && Has(Permissions.ImportDxf);

    /// <summary>연결 상태를 설정하고 UI 활성화 상태를 갱신한다.</summary>
    /// <param name="connected">연결 여부.</param>
    private void SetConnected(bool connected)
    {
        isConnected = connected;
        ApplyUserState();
    }

    /// <summary>연결 여부와 로그인한 사용자의 권한에 따라 각 기능(임포트/삭제/내보내기/편집/사용자·라이브러리 메뉴 등)을 활성/비활성화하고, 연결·사용자 요약 문구를 갱신한다.</summary>
    private void ApplyUserState()
    {
        ImportButton.IsEnabled = CanImport;
        DeleteImportButton.IsEnabled = CanImport;
        ExportButton.IsEnabled = isConnected && Has(Permissions.DrawingExport);
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

    /// <summary>권한을 확인하고, 없으면 경고 메시지를 표시한다.</summary>
    /// <param name="permission">필요한 권한 코드.</param>
    /// <param name="action">경고 문구에 쓸 작업 설명(영문 동사구).</param>
    /// <returns>권한이 있으면 true, 없으면 false.</returns>
    private bool Require(string permission, string action)
    {
        if (Has(permission))
        {
            return true;
        }

        MessageBox.Show(this, $"You do not have permission to {action}.", "Permission denied", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    /// <summary>사용자 행위를 감사 로그 테이블에 기록한다. 로그인하지 않았으면 무시하며, 기록 실패는 호출 흐름에 영향을 주지 않는다(TryWrite).</summary>
    /// <param name="action">행위 코드(예: 저장, 삭제 등).</param>
    /// <param name="entityType">대상 엔티티 종류(null 가능).</param>
    /// <param name="entityId">대상 엔티티 id(null 가능).</param>
    /// <param name="summary">요약 설명(null 가능).</param>
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
    /// 로그인 창(사용자가 없으면 최초 관리자 설정)을 띄우고, 비밀번호 변경이 필요하면 강제로 변경하게 한 뒤,
    /// 다음에 쓰도록 로그인 ID 를 기억한다.
    /// </summary>
    /// <param name="allowAutoLogin">true 이면 설정에서 자동 로그인이 켜져 있고 저장된 ID/비밀번호가 있을 때 창 없이 로그인을 시도한다.</param>
    /// <returns>로그인에 성공하면 true, 취소/실패이면 false.</returns>
    private async Task<bool> LoginAsync(bool allowAutoLogin = true)
    {
        // 1) 사용자 테이블 스키마를 보장하고, 등록된 사용자가 없으면 최초 관리자 설정 모드로 연다.
        try
        {
            await using var users = new UserRepository(connectionSettings.ConnectionString);
            await users.EnsureSchemaAsync();
            var setup = !await users.HasUsersAsync();
            AppUser? user = null;
            var rememberedPassword = string.Empty;
            // 2) 자동 로그인 시도: 저장된 ID/비밀번호가 있으면 대화상자 없이 인증한다.
            if (!setup && allowAutoLogin && appSettings.AutoLogin
                && connectionSettings.LastLoginId.Length > 0 && connectionSettings.LastLoginPassword.Length > 0)
            {
                var auto = await users.AuthenticateAsync(connectionSettings.LastLoginId, connectionSettings.LastLoginPassword);
                if (auto.User is not null)
                {
                    user = auto.User;
                    rememberedPassword = connectionSettings.LastLoginPassword;
                }
                else
                {
                    StatusText.Text = $"Automatic login failed: {auto.Error}";
                }
            }

            // 3) 자동 로그인이 안 되었으면 로그인 대화상자를 표시한다(취소하면 로그인 실패 처리).
            if (user is null)
            {
                var dialog = new LoginWindow(users, connectionSettings.LastLoginId, connectionSettings.LastLoginPassword, setup) { Owner = this };
                if (dialog.ShowDialog() != true || dialog.User is null)
                {
                    return false;
                }

                user = dialog.User;
                rememberedPassword = dialog.EnteredPassword;
            }

            // 4) 비밀번호 변경이 요구되는 계정은 변경이 끝날 때까지 반복해서 강제 변경 창을 띄운다.
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
                    rememberedPassword = change.NewPassword;
                    break;
                }
                catch (InvalidOperationException exception)
                {
                    MessageBox.Show(this, exception.Message, "Change password", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            // 5) 로그인 성공: 사용자 확정 및 마지막 로그인 정보 저장(비밀번호는 "기억" 설정일 때만 저장).
            currentUser = user;
            connectionSettings.LastLoginId = user.LoginId;
            connectionSettings.LastLoginPassword = appSettings.RememberPassword ? rememberedPassword : string.Empty;
            try
            {
                await connectionSettings.SaveAsync();
            }
            catch (Exception)
            {
            // 로그인 ID 기억은 편의 기능일 뿐이므로 저장 실패는 무시한다.
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

    /// <summary>(새) 서버 연결 직후: 사용자/레이아웃 상태를 초기화하고 연결 상태로 바꾼 뒤 로그인하여, 사용자가 볼 수 있는 도면 데이터를 로드한다.</summary>
    private async Task CompleteConnectionAsync()
    {
        currentUser = null;
        activeLayoutId = 1;
        ClearMapAndLayers();
        SetConnected(true);
        await LoginAndLoadAsync();
    }

    /// <summary>
    /// 로그인 후 사용자가 볼 수 있는 도면 데이터를 불러온다. 로그인 실패 시 상태바에 안내만 표시한다.
    /// </summary>
    /// <param name="allowAutoLogin">true 이면 저장된 자격 증명으로 자동 로그인을 시도, false 이면 항상 로그인 창을 띄운다(로그아웃 직후 등).</param>
    private async Task LoginAndLoadAsync(bool allowAutoLogin = true)
    {
        // 1) 로그인(실패/취소 시 버튼 상태만 갱신하고 종료).
        if (!await LoginAsync(allowAutoLogin))
        {
            ApplyUserState();
            StatusText.Text = "Not logged in. Use the Log in button.";
            return;
        }

        // 2) 권한에 따른 UI 갱신 후, 도면 조회 권한이 있으면 스키마 보장 → 동기화 시작 → 레이어 선택/로드 순으로 진행한다.
        ApplyUserState();
        if (Has(Permissions.DrawingView))
        {
            if (!await EnsureDrawingSchemasAsync())
            {
                return;
            }

            await StartSyncAsync();
            await PromptLayerSelectionAndLoadAsync();
        }
        else
        {
            StatusText.Text = "Logged in, but your roles do not allow viewing drawings.";
        }
    }

    /// <summary>지도/레이어 패널/선택 상태/속성 패널을 모두 초기화한다. 동기화를 멈추고 편집 모드였다면 먼저 종료한다(재연결·로그아웃 시 이전 사용자 데이터 노출 방지).</summary>
    private void ClearMapAndLayers()
    {
        StopSync();
        if (isEditMode)
        {
            ExitEditMode();
        }

        ClearDiffOverlay();
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

    /// <summary>
    /// 로그아웃(또는 로그아웃 상태에서는 로그인) 버튼 핸들러. 저장하지 않은 편집이 있으면 확인을 받고,
    /// 감사 로그를 남긴 뒤 화면을 비우고 자동 로그인 없이 로그인 창을 다시 띄운다.
    /// </summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null)
        {
            await LoginAndLoadAsync(allowAutoLogin: false);
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
        await LoginAndLoadAsync(allowAutoLogin: false);
    }

    /// <summary>내 비밀번호 변경 메뉴 핸들러. 기존 비밀번호를 확인한 뒤 서버에 변경을 반영한다.</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

// ---- 개요도(미니맵) ------------------------------------------------------------

    /// <summary>미니맵 전체 재생성 예약 중인지(중복 예약 방지 플래그).</summary>
    private bool miniMapRebuildPending;
    /// <summary>미니맵 뷰포트(빨간 사각형) 갱신 예약 중인지(중복 예약 방지 플래그).</summary>
    private bool miniMapViewportPending;
    /// <summary>미니맵을 마우스로 드래그 중인지 여부.</summary>
    private bool miniMapDragging;

    /// <summary>레이어 내용/표시/스타일이 바뀐 직후 미니맵을 잠시 뒤 다시 그리도록 예약한다(여러 호출을 한 번으로 합침, Background 우선순위).</summary>
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

    /// <summary>미니맵 뷰포트 사각형 갱신을 예약한다. 줌/이동 변경마다 호출되므로 Render 우선순위로 한 번에 합쳐 처리한다.</summary>
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
    /// 지도가 이미 가진(크기 고정) Path 지오메트리를 얇은 고정 폭 펜으로 재사용해, 어떤 줌 수준에서도
    /// 메인 뷰와 같은 레이어/색/순서/표시 상태를 개요도로 보여 준다.
    /// </summary>
    private void RebuildMiniMap()
    {
        // 1) 기존 미니맵 도형 제거 후, 데이터가 없으면 뷰포트 사각형도 숨기고 종료.
        MiniMapShapes.Children.Clear();
        if (renderedEnvelope is null || shapesByLayer.Count == 0 || renderedWidth <= 0 || renderedHeight <= 0)
        {
            MiniMapViewport.Visibility = Visibility.Collapsed;
            return;
        }

        // 2) 미니맵 캔버스를 지도 렌더링 크기와 같게 맞추고, 숨김이 아닌 레이어의 Path 를 Data/색/Z-순서 그대로 복사한다(클릭 방해 방지를 위해 히트테스트 제외).
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

        // 3) 레이아웃을 갱신해 실제 크기를 얻은 뒤 선 두께와 뷰포트를 보정한다.
        MiniMapBox.UpdateLayout();
        ApplyMiniMapStrokeWidths();
        UpdateMiniMapViewport();
    }

    /// <summary>지도 좌표에서 미니맵 픽셀로의 배율.</summary>
    private double MiniMapScale => renderedWidth > 0 && MiniMapBox.ActualWidth > 0 ? MiniMapBox.ActualWidth / renderedWidth : 1;

    /// <summary>미니맵 선 두께를 배율에 반비례하게 설정해 화면상 항상 약 1px(뷰포트 사각형은 1.6px)로 보이게 한다.</summary>
    private void ApplyMiniMapStrokeWidths()
    {
        var thickness = 1.0 / MiniMapScale;
        foreach (var child in MiniMapShapes.Children.OfType<System.Windows.Shapes.Path>())
        {
            child.StrokeThickness = thickness;
        }

        MiniMapViewport.StrokeThickness = 1.6 / MiniMapScale;
    }

    /// <summary>메인 지도 뷰에 현재 보이는 영역을 미니맵 위에 빨간 사각형으로 표시한다(변환의 역으로 지도 좌표 범위를 계산).</summary>
    private void UpdateMiniMapViewport()
    {
        if (renderedEnvelope is null || shapesByLayer.Count == 0
            || mapScale.ScaleX <= 0 || mapScale.ScaleY <= 0 || MapCanvas.ActualWidth <= 0)
        {
            MiniMapViewport.Visibility = Visibility.Collapsed;
            return;
        }

        // 화면 좌표 → 지도(렌더링) 좌표: (화면 - 이동) / 배율.
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

    /// <summary>미니맵 크기가 바뀌면 선 두께와 뷰포트 사각형을 다시 맞춘다.</summary>
    /// <param name="sender">미니맵 컨트롤.</param>
    /// <param name="e">크기 변경 이벤트 인자.</param>
    private void MiniMap_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyMiniMapStrokeWidths();
        ScheduleMiniMapViewport();
    }

    /// <summary>클릭한 미니맵 지점이 뷰의 중앙이 되도록 지도를 이동(패닝)한다.</summary>
    /// <param name="point">미니맵 캔버스 기준 좌표(렌더링 좌표계). 범위를 벗어나면 경계로 보정한다.</param>
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

    /// <summary>미니맵 마우스 누름: 드래그를 시작하고 마우스를 캡처한 뒤 클릭 지점으로 지도를 이동한다.</summary>
    /// <param name="sender">미니맵 테두리.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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

    /// <summary>미니맵 마우스 이동: 드래그 중이면 지도를 마우스 지점으로 계속 이동한다.</summary>
    /// <param name="sender">미니맵 테두리.</param>
    /// <param name="e">마우스 이벤트 인자.</param>
    private void MiniMap_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (miniMapDragging)
        {
            CenterMapOnMiniMapPoint(e.GetPosition(MiniMapCanvas));
        }
    }

    /// <summary>미니맵 마우스 놓음: 드래그를 종료하고 마우스 캡처를 해제한다.</summary>
    /// <param name="sender">미니맵 테두리.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private void MiniMap_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        miniMapDragging = false;
        MiniMapBorder.ReleaseMouseCapture();
    }

// ---- 다중 사용자 동기화 -------------------------------------------------------

    /// <summary>이 실행 인스턴스의 고유 세션 id. 변경 알림에서 "내가 만든 변경"을 구분하는 데 쓴다.</summary>
    private readonly Guid sessionId = Guid.NewGuid();
    /// <summary>다른 사용자의 변경을 감지하는 동기화 코디네이터(PostgreSQL 알림 기반). 동기화 중이 아니면 null.</summary>
    private SyncCoordinator? sync;
    /// <summary>동기화 확인이 진행 중인지(재진입 방지 플래그).</summary>
    private bool syncCheckRunning;

    /// <summary>저장 시 DB 에 함께 기록하는 변경 주체 정보(사용자 id/이름/세션 id). 다른 세션이 변경 출처를 알 수 있게 한다.</summary>
    private ChangeContext CurrentChangeContext => new(currentUser?.Id, currentUser?.DisplayName, sessionId);

    /// <summary>동기화 코디네이터를 만들어 시작한다. 알림이 오면 변경 확인을 요청하고, 연결 상태가 바뀌면 상태 표시를 갱신한다. 실패해도 앱은 계속 쓸 수 있다(동기화 사용 안 함).</summary>
    private async Task StartSyncAsync()
    {
        StopSync();
        try
        {
            var coordinator = new SyncCoordinator(
                connectionSettings.ConnectionString, sessionId,
                () => layerChecksByName.Keys.ToArray(), () => activeLayoutId);
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

    /// <summary>동기화를 중지하고 코디네이터를 비동기로 해제한다(재연결/로그아웃/연결 변경 시 호출).</summary>
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

    /// <summary>UI 가 한가해질 때(ApplicationIdle) 서버 변경 확인을 예약한다. 동기화 중일 때만 동작한다.</summary>
    private void ScheduleSyncCheck()
    {
        if (sync is not null)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => _ = CheckForSyncChangesAsync()));
        }
    }

    /// <summary>상태바의 동기화 표시를 클릭하면 대기 여부와 무관하게 강제로 변경 확인을 수행한다.</summary>
    /// <param name="sender">동기화 상태 텍스트.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private void SyncStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _ = CheckForSyncChangesAsync(force: true);

    /// <summary>상태바 동기화 문구/색을 갱신한다: 갱신 대기(주황, 건수), 연결됨(녹색), 재연결 중(회색).</summary>
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

    /// <summary>다른 사용자의 변경 요약(최대 8건)을 사용자에게 보여 줄 메시지로 만든다.</summary>
    /// <param name="batch">감지된 변경 묶음.</param>
    /// <returns>갱신 여부를 묻는 안내 문자열.</returns>
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
    /// 다른 세션이 로드된 레이어에 저장한 변경이 있는지 확인하고, 새로운 변경이 있으면 갱신할지 묻는다.
    /// 편집 중에는 편집을 저장하거나 취소할 때까지 질문을 보류한다.
    /// </summary>
    /// <param name="force">true 이면 이전에 거절한 변경이어도 다시 묻는다(상태바 클릭 시).</param>
    private async Task CheckForSyncChangesAsync(bool force = false)
    {
        // 1) 코디네이터 없음/이미 확인 중/레이어 미로드/작업 중 오버레이 표시 중이면 건너뛴다(동시 실행 방지).
        var coordinator = sync;
        if (coordinator is null || syncCheckRunning || layerChecksByName.Count == 0
            || BusyOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        syncCheckRunning = true;
        try
        {
            // 2) 서버에 변경 조회. 대기 중 코디네이터가 교체되었으면(재연결 등) 결과를 버린다.
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

            // 3) 편집 중이면 로컬 편집을 덮어쓰지 않도록 갱신 질문을 보류(강제 확인 시에만 안내).
            if (isEditMode)
            {
                if (force)
                {
                    MessageBox.Show(this, "편집을 저장하거나 취소한 뒤에 갱신할 수 있습니다.", "서버 데이터 변경", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                return;
            }

            // 4) 이미 거절한 변경이면 다시 묻지 않고, 아니면 갱신 여부를 확인한다. 예 → 다시 로드, 아니오 → 거절 기록.
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

    /// <summary>로드된 레이어를 다시 읽되, 사용자의 줌/이동 위치와 숨김 레이어 설정은 유지한다.</summary>
    private async Task ReloadForSyncAsync()
    {
        // 1) 현재 화면 중앙의 월드 좌표와 줌 배율, 숨김 레이어 목록을 기억한다.
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

        // 2) 서버 데이터를 다시 로드(경계 상자가 바뀔 수 있음).
        await LoadImportedPolygonsAsync();

        // 3) 기억한 월드 좌표가 화면 중앙에 오도록 이동값을 재계산하고 줌을 복원한 뒤 숨김 레이어를 다시 숨긴다.
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

    /// <summary>블록 라이브러리 메뉴 핸들러. 조회 권한을 확인하고 스키마를 보장한 뒤 라이브러리 창을 모달로 연다.</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>
    /// 사용자가 선택한 저장된 블록 인스턴스(뷰 모드 또는 편집 모드)를 반환한다. 정확히 하나여야 한다.
    /// </summary>
    /// <param name="problem">선택이 부적합할 때의 사유 메시지(성공 시 null).</param>
    /// <returns>(인스턴스 DB id, 블록 이름) 또는 부적합하면 null.</returns>
    private (long InstanceId, string? BlockName)? GetSelectedBlockInstance(out string? problem)
    {
        problem = null;
        // 편집 모드: 선택이 정확히 하나의 저장된 블록 인스턴스이고 미저장 편집이 없어야 라이브러리 등록이 가능하다.
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

        // 뷰 모드: 선택된 피처가 블록 인스턴스이면 그 DB id 와 block_name 속성을 사용한다.
        if (selectedFeature is { FeatureType: "block_instance" } feature)
        {
            feature.Attributes.TryGetValue("block_name", out var blockName);
            return (feature.Id, blockName);
        }

        problem = "Select a block object first.";
        return null;
    }

    /// <summary>
    /// "라이브러리에 등록" 핸들러. 선택된 블록 인스턴스 하나를 블록 라이브러리에 등록한다.
    /// 이미 동일한 형상이 라이브러리에 있으면 새로 만들지 않고 그 항목에 연결한다.
    /// </summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void RegisterInLibrary_Click(object sender, RoutedEventArgs e)
    {
        // 1) 등록 권한 확인 후, 선택(정확히 1개의 저장된 블록)을 검증한다.
        if (currentUser is null || !Require(Permissions.LibraryCreate, "register library blocks"))
        {
            return;
        }

        if (GetSelectedBlockInstance(out var problem) is not { } selection)
        {
            MessageBox.Show(this, problem, "Register in library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 2) 카테고리 목록을 읽어 등록 대화상자를 띄우고, 확인되면 인스턴스로부터 라이브러리 항목을 만든다(감사 로그 기록).
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

    /// <summary>사용자 관리 메뉴 핸들러. 관리 창을 연 뒤, 창 안에서 내 역할/계정이 바뀌었을 수 있으므로 사용자 정보를 다시 읽어 권한을 갱신하고, 비활성화되었으면 로그아웃 처리한다.</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void ManageUsers_Click(object sender, RoutedEventArgs e)
    {
        if (currentUser is null || !Require(Permissions.UserManage, "manage users"))
        {
            return;
        }

        await using var users = new UserRepository(connectionSettings.ConnectionString);
        new UserManagementWindow(users, currentUser) { Owner = this }.ShowDialog();

        // 창 안에서 역할이나 계정 자체가 변경되었을 수 있다.
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

    /// <summary>서버 연결 창을 표시한다.</summary>
    /// <param name="message">창에 보여 줄 안내/실패 사유(null 가능).</param>
    /// <returns>연결에 성공한 설정, 취소하면 null.</returns>
    private PostgresConnectionSettings? ShowServerDialog(string? message)
    {
        var dialog = new ServerConnectionWindow(connectionSettings, message) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Settings : null;
    }

    /// <summary>
    /// 창 로드 완료 핸들러(앱 시작 흐름의 진입점). 앱 설정을 초기화하고 저장된 연결 설정을 읽은 뒤 자동 연결을 시도한다.
    /// </summary>
    /// <param name="sender">MainWindow.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeSettings();
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
    /// 저장된 설정으로 연결한다. 설정이 없거나 연결에 실패하면 서버 창을 사유와 함께 열어 사용자가 수정할 수 있게 한다.
    /// 성공 시 로그인/데이터 로드(CompleteConnectionAsync)로 이어진다.
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
                await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
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

    /// <summary>서버 버튼 핸들러. 편집 중에는 연결을 바꿀 수 없고(미저장 편집 보호), 새 설정으로 연결하면 로그인/로드를 처음부터 다시 수행한다.</summary>
    /// <param name="sender">클릭된 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>
    /// 서버에서 임포트된 레이어 목록을 읽어 레이어 선택 창을 띄우고, 선택 결과를 설정에 저장한 뒤 선택된 레이어의 도형을 로드한다.
    /// 테이블이 없거나 레이어가 없거나 선택을 취소하면 아무 것도 로드하지 않는다.
    /// </summary>
    private async Task PromptLayerSelectionAndLoadAsync()
    {
        IReadOnlyList<LayerInfo> layers;
        ShowBusy("Fetching layer list from PostgreSQL...");
        try
        {
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
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

    /// <summary>
    /// 선택된 레이어(필터)의 외곽 폴리곤과 블록 인스턴스 지오메트리를 DB 에서 읽어 렌더링하고 레이어 패널을 채운다.
    /// 블록 인스턴스가 있는 레이어는 일반 피처 대신 블록 인스턴스 지오메트리를 사용해 중복 표시를 막는다.
    /// </summary>
    private async Task LoadImportedPolygonsAsync()
    {
        ClearDiffOverlay();
        ShowBusy("Fetching imported geometries from PostgreSQL...");
        try
        {
            // 이 시점 이후 저장되는 변경은 지금 보이는 데이터보다 새것이므로 동기화 기준(baseline)을 초기화한다.
            if (sync is not null)
            {
                await sync.ResetBaselineAsync();
            }

            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
            var layerFilter = activeLayerFilter is { Count: > 0 } ? activeLayerFilter : null;
            var layerFeatures = await repository.FindImportedOuterPolygonsAsync(layerFilter);
            var instanceFeatures = await repository.FindImportedBlockInstanceGeometriesAsync(layerFilter);
            if (layerFeatures.Count == 0 && instanceFeatures.Count == 0)
            {
                LayersPanel.Children.Clear();
                StatusText.Text = "No imported DXF polygons found in the configured database.";
                return;
            }

            // 블록 인스턴스를 가진 레이어는 일반 피처를 제외하고 인스턴스 지오메트리만 사용한다.
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
            _ = RefreshLayoutStatusAsync();
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

    /// <summary>작업 중 오버레이를 표시하고 입력을 막으며 대기 커서로 바꾼다.</summary>
    /// <param name="message">오버레이에 표시할 진행 문구.</param>
    private void ShowBusy(string message)
    {
        BusyText.Text = message;
        BusyOverlay.Visibility = Visibility.Visible;
        InputBlocker.Visibility = Visibility.Visible;
        Cursor = System.Windows.Input.Cursors.Wait;
    }

    /// <summary>작업 중 오버레이를 숨기고 입력 차단을 해제하며 커서를 복원한다.</summary>
    private void HideBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        InputBlocker.Visibility = Visibility.Collapsed;
        Cursor = System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>
    /// 피처마다 WPF Shape 하나를 만드는 대신, (레이어, 지오메트리 종류) 쌍의 모든 피처를 하나의
    /// StreamGeometry 로 만든 단일 Path 로 렌더링한다. 임포트된 피처가 수만 개일 때 피처당
    /// FrameworkElement 하나는 비주얼 트리 자체(생성, 히트 테스트, 요소별 속성 갱신)가 병목이 되었다.
    /// 소수의 Path 는 한 번의 그리기 호출로 그려져 이동/확대/선택이 빠르게 유지된다.
    /// </summary>
    /// <param name="features">렌더링할 피처 목록(월드 좌표 지오메트리, null 불가).</param>
    private void RenderImportedGeometries(IReadOnlyList<SpatialFeature> features)
    {
        // 1) 전체 경계 상자(월드 좌표)를 구한다. 비어 있으면 그릴 것이 없으므로 종료.
        var envelope = new Envelope();
        foreach (var feature in features)
        {
            envelope.ExpandToInclude(feature.Geometry.EnvelopeInternal);
        }

        if (envelope.IsNull)
        {
            return;
        }

        // 2) 이전 렌더링 상태(도형/Z-순서/숨김/선택)를 모두 초기화하고 새 피처 목록을 보관한다.
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

        // 3) 캔버스 크기와 여백에 맞는 균일 배율을 계산한다(가로/세로 중 작은 쪽). 이후 ProjectToScreen 이 이 값을 사용한다.
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

        // 4) 레이어별로 폴리곤/선/점 StreamGeometry 컨텍스트를 하나씩만 열어 두고 모든 피처의 도형을 같은 컨텍스트에 추가한다.
        var polygonContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);
        var lineContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);
        var pointContexts = new Dictionary<string, (StreamGeometry Geometry, StreamGeometryContext Context)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>레이어별 StreamGeometry/컨텍스트를 가져오거나 없으면 새로 열어 등록하는 로컬 함수.</summary>
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

        // 5) 피처 지오메트리 종류별로 해당 레이어의 컨텍스트에 도형을 그린다.
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

        // 6) 컨텍스트를 닫고 레이어 색상/채움/두께를 적용해 레이어별 Path 로 등록한다(점은 선 두께 0, 채움만 사용).
        foreach (var (layerName, entry) in polygonContexts)
        {
            entry.Context.Close();
            var color = GetLayerColor(layerName);
            var path = new Path
            {
                Data = entry.Geometry,
                Fill = new SolidColorBrush(GetLayerFill(layerName)),
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

        // 7) 화면 맞춤으로 뷰를 초기화하고 미니맵 재생성을 예약한다.
        ResetView();
        ScheduleMiniMapRebuild();
    }

    /// <summary>폴리곤(외곽 링 + 내부 링들)을 StreamGeometry 컨텍스트에 닫힌 도형으로 추가한다.</summary>
    /// <param name="context">열려 있는 StreamGeometry 컨텍스트.</param>
    /// <param name="polygon">추가할 폴리곤.</param>
    /// <param name="project">월드 좌표를 화면 좌표로 변환하는 함수.</param>
    private static void AddPolygonFigure(StreamGeometryContext context, NtsPolygon polygon, Func<Coordinate, System.Windows.Point> project)
    {
        AddRingFigure(context, polygon.ExteriorRing, project);
        for (var index = 0; index < polygon.NumInteriorRings; index++)
        {
            AddRingFigure(context, polygon.GetInteriorRingN(index), project);
        }
    }

    /// <summary>링 하나를 채움 가능한 닫힌 도형(figure)으로 추가한다. 점이 없으면 무시한다.</summary>
    /// <param name="context">열려 있는 StreamGeometry 컨텍스트.</param>
    /// <param name="ring">추가할 링.</param>
    /// <param name="project">월드 좌표를 화면 좌표로 변환하는 함수.</param>
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

    /// <summary>선을 열린(채우지 않음) 도형으로 추가한다. 점이 없으면 무시한다.</summary>
    /// <param name="context">열려 있는 StreamGeometry 컨텍스트.</param>
    /// <param name="line">추가할 선.</param>
    /// <param name="project">월드 좌표를 화면 좌표로 변환하는 함수.</param>
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

    /// <summary>점 마커(마름모)의 반지름(화면 단위).</summary>
    private const double PointMarkerRadius = 3.0;

    /// <summary>점 피처를 작은 마름모 도형으로 추가한다(점은 면적이 없으므로 보이도록 마커로 표현).</summary>
    /// <param name="context">열려 있는 StreamGeometry 컨텍스트.</param>
    /// <param name="point">추가할 점.</param>
    /// <param name="project">월드 좌표를 화면 좌표로 변환하는 함수.</param>
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

    /// <summary>
    /// 레이어 Path 를 지도 캔버스에 추가하고 레이어별 Z-순서/도형 목록에 등록한다. 기준 선 두께가 있으면
    /// 현재 줌 배율에 맞춰 화면 두께를 보정해 둔다.
    /// </summary>
    /// <param name="path">추가할 Path.</param>
    /// <param name="layerName">소속 레이어 이름.</param>
    /// <param name="baseStrokeWidth">기준 선 두께(0 이하이면 두께 보정 대상에서 제외, 예: 점).</param>
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
    /// 월드 좌표 ↔ "로컬" 좌표(MapLayerCanvas 와 EditableObjectsCanvas 가 공유하는 변환 전 좌표 공간,
    /// 즉 이동/확대 RenderTransform 이 적용되기 전에 Shape.Points 가 정의되는 공간) 변환.
    /// 화면 Y 가 아래로 증가하므로 Y 를 뒤집는다.
    /// </summary>
    /// <param name="coordinate">월드 좌표(null 불가).</param>
    /// <returns>로컬(화면 렌더링) 좌표.</returns>
    private System.Windows.Point ProjectToScreen(Coordinate coordinate) => new(
        renderedPadding + (coordinate.X - renderedEnvelope!.MinX) * renderedScale,
        renderedHeight - renderedPadding - (coordinate.Y - renderedEnvelope.MinY) * renderedScale);

    /// <summary>로컬(화면 렌더링) 좌표를 월드 좌표로 되돌린다(ProjectToScreen 의 역변환).</summary>
    /// <param name="local">로컬 좌표.</param>
    /// <returns>월드 좌표.</returns>
    private Coordinate UnprojectFromScreen(System.Windows.Point local) => new(
        renderedEnvelope!.MinX + (local.X - renderedPadding) / renderedScale,
        renderedEnvelope.MinY + (renderedHeight - renderedPadding - local.Y) / renderedScale);

    /// <summary>블록 그룹: 블록 전체의 경계 상자와 합쳐진 피처(선택/충돌 판정용).</summary>
    /// <param name="Envelope">블록 경계 상자(월드 좌표).</param>
    /// <param name="Combined">블록의 모든 부품을 합친 피처.</param>
    private sealed record BlockGroup(Envelope Envelope, SpatialFeature Combined);

    /// <summary>블록 인스턴스 id → 블록 그룹 캐시. 렌더링이 바뀌면 null 로 무효화하고 필요할 때 다시 계산한다.</summary>
    private Dictionary<long, BlockGroup>? blockGroups;

    /// <summary>
    /// 블록 인스턴스의 선 단위 조각들(같은 인스턴스 id 를 공유하는 별개의 피처로 도착함)을 다시 하나의
    /// 객체로 합친다: 모든 부품의 GeometryCollection 과 전체 경계 상자. 로드된 피처가 바뀔 때까지 캐시한다.
    /// </summary>
    /// <returns>블록 인스턴스 id → 블록 그룹 사전.</returns>
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

    /// <summary>
    /// 지도 클릭 지점 근처의 피처를 찾아 선택한다(뷰 모드 선택).
    /// 규칙: 숨김 레이어 제외 → 위에 있는 레이어(Z-순서) 우선 → 더 가까운 것 → 블록은 더 작은 블록 우선.
    /// </summary>
    /// <param name="canvasPoint">MapCanvas 기준 클릭 위치(화면 좌표).</param>
    private void TrySelectFeatureAt(System.Windows.Point canvasPoint)
    {
        if (renderedEnvelope is null || importedFeatures.Count == 0)
        {
            StatusText.Text = "No imported geometries are loaded yet.";
            return;
        }

        // 1) 화면 좌표 → 로컬(변환 전) 좌표 → 월드 좌표로 역변환한다(이동/확대 변환과 투영의 역).
        try
        {
            var scaleX = Math.Max(mapScale.ScaleX, 0.0001);
            var scaleY = Math.Max(mapScale.ScaleY, 0.0001);
            var localX = (canvasPoint.X - mapTranslation.X) / scaleX;
            var localY = (canvasPoint.Y - mapTranslation.Y) / scaleY;
            var worldX = renderedEnvelope.MinX + (localX - renderedPadding) / renderedScale;
            var worldY = renderedEnvelope.MinY + (renderedHeight - renderedPadding - localY) / renderedScale;

            // 2) 클릭 허용 오차를 화면 10픽셀로 두고 월드 단위로 환산한다(확대해도 클릭 감각이 같도록).
            const double toleranceScreenPixels = 10.0;
            var toleranceWorld = toleranceScreenPixels / (renderedScale * scaleX);
            var searchEnvelope = new Envelope(worldX - toleranceWorld, worldX + toleranceWorld, worldY - toleranceWorld, worldY + toleranceWorld);
            var clickPoint = new NetTopologySuite.Geometries.Point(new Coordinate(worldX, worldY));

            SpatialFeature? best = null;
            var bestDistance = double.MaxValue;
            var bestZOrder = -1;
            var bestArea = double.MaxValue;

            // 블록 인스턴스는 선 여러 개로 이루어진 "하나의 객체"이다: 경계 상자 안쪽 어디든, 또는 어느 선 근처든
            // 통째로 히트 테스트하고 모든 부품을 함께 선택한다.
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
                // 위쪽 레이어가 이기고, 같으면 더 가까운 것이 이긴다. 같은 거리(여러 겹친 경계 상자 안을 클릭)라면
                // 가장 작은 블록이 이겨서 안쪽 블록도 선택할 수 있게 한다.
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

            // 3) 블록이 아닌 일반 피처도 같은 규칙(레이어 Z-순서 → 거리)으로 후보와 비교한다.
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

            // 4) 결과 반영: 선택하거나, 없으면 선택/속성 패널을 비운다.
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

    /// <summary>피처를 선택 상태로 만든다: 속성 패널(요약/속성 그리드)을 채우고 지도에 강조 도형을 그린다.</summary>
    /// <param name="feature">선택할 피처(null 불가).</param>
    private void SelectFeature(SpatialFeature feature)
    {
        SelectedObjectText.Text = $"Feature #{feature.Id}";
        PropertySummaryText.Text = $"Type: {feature.FeatureType}\nGeometry: {feature.Geometry.GeometryType}\nSRID: {feature.Geometry.SRID}\nValid: {feature.Geometry.IsValid}";
        PropertyHintText.Text = string.Empty;
        AttributesGrid.ItemsSource = feature.Attributes.Select(attribute => new AttributeRow(attribute.Key, attribute.Value)).ToArray();
        // HighlightFeature 는 이전 강조(와 함께 selectedFeature/Layer)를 지우므로,
        // 새 선택은 그 뒤에 기록해야 한다. 그렇지 않으면 레이어를 숨겨도 선택이 해제되지 않는다.
        HighlightFeature(feature);
        selectedFeatureLayer = feature.LayerName ?? "(no layer)";
        selectedFeature = feature;
        UpdateSelectionCount();
    }

    /// <summary>설정에서 정한 선택 윤곽 색(고정된 Freeze 브러시). 색 파싱에 실패하면 금색(Gold).</summary>
    private Brush SelectionBrush
    {
        get
        {
            var color = TryParseHexColor(appSettings.SelectionColor, out var parsed) ? parsed : Colors.Gold;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>설정에서 정한 선택 윤곽 두께(0.5~8 로 제한).</summary>
    private double SelectionThickness => Math.Clamp(appSettings.SelectionThickness, 0.5, 8);

    /// <summary>선택된 피처의 지오메트리 종류(블록 묶음/폴리곤/선/점)에 맞는 강조 도형을 만들어 지도 맨 위에 올린다. 히트 테스트는 하지 않으며 줌에 맞춰 두께가 보정되도록 등록한다.</summary>
    /// <param name="feature">강조할 피처.</param>
    private void HighlightFeature(SpatialFeature feature)
    {
        ClearSelectionHighlight();
        if (renderedEnvelope is null)
        {
            return;
        }

        Shape? highlight = feature.Geometry switch
        {
            // 블록 객체 전체: 선 하나가 아니라 모든 부품의 윤곽을 그린다.
            NetTopologySuite.Geometries.GeometryCollection collection => new Path
            {
                Data = BuildHighlightGeometry(collection),
                Stroke = SelectionBrush,
                Fill = Brushes.Transparent,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            },
            NtsPolygon polygon => new System.Windows.Shapes.Polygon
            {
                Points = new PointCollection(polygon.ExteriorRing.Coordinates.Select(ProjectToScreen)),
                Stroke = SelectionBrush,
                Fill = Brushes.Transparent
            },
            LineString line => new Polyline
            {
                Points = new PointCollection(line.Coordinates.Select(ProjectToScreen)),
                Stroke = SelectionBrush,
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
            highlight.StrokeThickness = SelectionThickness;
        }
        else
        {
            baseStrokeWidths[highlight] = SelectionThickness;
            highlight.StrokeThickness = SelectionThickness / Math.Max(mapScale.ScaleX, 0.1);
        }

        Panel.SetZIndex(highlight, 100000);
        MapLayerCanvas.Children.Add(highlight);
        selectionHighlight = highlight;
    }

    /// <summary>블록 객체(GeometryCollection)의 모든 부품을 하나의 StreamGeometry 로 합쳐 강조 윤곽을 만든다.</summary>
    /// <param name="collection">블록의 모든 부품 지오메트리.</param>
    /// <returns>고정(Frozen)된 StreamGeometry.</returns>
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

    /// <summary>점 피처를 표시하기 위한 원형 강조 마커를 만든다.</summary>
    /// <param name="center">마커 중심(로컬 좌표).</param>
    /// <returns>배치된 원형 도형.</returns>
    private Shape CreateHighlightMarker(System.Windows.Point center)
    {
        const double r = 7;
        var ellipse = new Ellipse { Width = r * 2, Height = r * 2, Stroke = SelectionBrush, Fill = Brushes.Transparent };
        Canvas.SetLeft(ellipse, center.X - r);
        Canvas.SetTop(ellipse, center.Y - r);
        return ellipse;
    }

    /// <summary>선택 강조를 제거하고 선택 상태(피처/레이어)와 선택 개수 표시를 초기화한다.</summary>
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

    /// <summary>레이어 이름 → 표시 체크박스.</summary>
    private readonly Dictionary<string, CheckBox> layerChecksByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 이름 → 이름 텍스트(레이어 패널).</summary>
    private readonly Dictionary<string, TextBlock> layerNameTextsByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 이름 → 편집 중 표시 연필 아이콘.</summary>
    private readonly Dictionary<string, System.Windows.Controls.Image> layerPencilsByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>레이어 행 드래그 시작 위치(화면 좌표).</summary>
    private System.Windows.Point layerDragStart;
    /// <summary>드래그가 시작될 후보 레이어 행. 드래그 시작 후 또는 더블클릭 시 null.</summary>
    private Border? layerDragRow;
    /// <summary>레이어 순서 변경 드래그의 DataObject 형식 이름.</summary>
    private const string LayerDragFormat = "SpatialEditor.Layer";

    /// <summary>
    /// QGIS 스타일 레이어 목록을 만든다: 표시 체크박스, 색 견본(클릭 = 해당 레이어 편집), 객체 수가 붙은
    /// 한 줄 이름, 편집 중 연필 아이콘, 더블클릭 = 스타일 창, 우클릭 메뉴, 드래그로 쌓임 순서 변경
    /// (목록 위 = 지도 위).
    /// </summary>
    /// <param name="layers">표시할 레이어 정보 목록(이름, 지오메트리 종류, 인스턴스/엔티티 수).</param>
    private void PopulateLayerPanel(IReadOnlyList<LayerInfo> layers)
    {
        // 1) 기존 패널/컨트롤 참조를 모두 비운다.
        LayersPanel.Children.Clear();
        layerSwatchesByName.Clear();
        layerRowsByName.Clear();
        layerChecksByName.Clear();
        layerNameTextsByName.Clear();
        layerPencilsByName.Clear();
        selectedLayerName = null;
        // 2) 레이어마다 색 견본, 체크박스, 이름(객체 수 포함), 연필 아이콘, 행(Border) 을 만들어 이벤트를 연결한다.
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

        // 3) 현재 순서대로 지도 Z-순서를 적용하고 편집 중 표시를 갱신한다.
        ApplyLayerOrder();
        UpdateLayerSwatchEditState();
    }

    /// <summary>레이어 행의 우클릭 메뉴(레이어로 확대, 스타일, 편집, 이 레이어만 표시, 숨기기, 삭제)를 만든다. 편집/삭제 항목은 메뉴가 열릴 때 권한/상태에 따라 활성화한다.</summary>
    /// <param name="layerName">대상 레이어 이름.</param>
    /// <returns>구성된 컨텍스트 메뉴.</returns>
    private ContextMenu BuildLayerContextMenu(string layerName)
    {
        /// <summary>아이콘이 있는 메뉴 항목을 만드는 로컬 함수.</summary>
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
        menu.Items.Add(new Separator());
        var deleteItem = Item("Delete this layer...", "Icon.DeleteObject", () => _ = DeleteLayerAsync(layerName));
        menu.Opened += (_, _) => deleteItem.IsEnabled = Has(Permissions.DrawingDelete);
        menu.Items.Add(deleteItem);
        return menu;
    }

    /// <summary>레이어 행 마우스 누름: 행을 선택하고 드래그 시작 후보로 기록한다. 더블클릭이면 드래그를 취소하고 스타일 창을 연다.</summary>
    /// <param name="sender">레이어 행(Border, Tag=레이어 이름).</param>
    /// <param name="e">마우스 버튼 이벤트 인자(ClickCount 로 더블클릭 판별).</param>
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

    /// <summary>레이어 행 미리보기 마우스 이동: 왼쪽 버튼을 누른 채 최소 드래그 거리 이상 움직이면 레이어 순서 변경 드래그를 시작한다.</summary>
    /// <param name="sender">레이어 행.</param>
    /// <param name="e">마우스 이벤트 인자.</param>
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

    /// <summary>레이어 행 DragOver: 레이어 드래그 데이터가 있을 때만 이동 효과를 허용한다.</summary>
    /// <param name="sender">대상 레이어 행.</param>
    /// <param name="e">드래그 이벤트 인자.</param>
    private void LayerRow_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(LayerDragFormat) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>레이어 행 Drop: 끌어온 행을 놓은 행의 위치로 옮기고 지도 쌓임 순서를 다시 적용한다.</summary>
    /// <param name="sender">놓인 대상 레이어 행(Border).</param>
    /// <param name="e">드래그 이벤트 인자(레이어 이름 데이터 포함).</param>
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
    /// 레이어 목록 순서대로 지도 도형을 쌓는다(첫 행이 맨 위). 한 레이어 안에서는 채움이 선 아래,
    /// 선이 점 마커 아래에 놓인다. 같은 순위가 "어느 레이어가 클릭을 가져가는가" 규칙에도 쓰인다.
    /// </summary>
    private void ApplyLayerOrder()
    {
        // 목록 맨 위 행이 가장 큰 순위(위에 그려짐). 도형 종류(kind): 0=채움+선, 1=선만, 2=점(채움만) → 같은 레이어 안에서 채움 < 선 < 점 순으로 쌓는다.
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

    /// <summary>레이어 표시 체크박스 상태를 설정한다(Checked/Unchecked 이벤트로 실제 표시가 갱신됨).</summary>
    /// <param name="layerName">대상 레이어 이름.</param>
    /// <param name="isChecked">true 면 표시, false 면 숨김.</param>
    private void SetLayerChecked(string layerName, bool isChecked)
    {
        if (layerChecksByName.TryGetValue(layerName, out var checkBox))
        {
            checkBox.IsChecked = isChecked;
        }
    }

    /// <summary>지정한 레이어만 표시하고 나머지는 모두 숨긴다.</summary>
    /// <param name="layerName">표시할 레이어 이름.</param>
    private void ShowOnlyLayer(string layerName)
    {
        foreach (var (name, checkBox) in layerChecksByName)
        {
            checkBox.IsChecked = string.Equals(name, layerName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>레이어의 모든 피처가 보이도록 지도를 확대/이동한다. 피처가 없으면 안내만 표시한다.</summary>
    /// <param name="layerName">대상 레이어 이름.</param>
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

    /// <summary>
    /// 월드 경계 상자가 캔버스에 약 90% 로 꽉 차게 보이도록 배율(0.1~20 제한)과 이동값을 계산해 적용한다.
    /// 확대 후 선 두께를 줌에 맞게 다시 보정하도록 예약한다.
    /// </summary>
    /// <param name="envelope">보여 줄 영역(월드 좌표).</param>
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
    /// 레이어를 패널의 "현재" 선택으로 표시한다(ListBox 항목처럼 강조). 색 견본의 편집 모드 전환과
    /// 무관한, 순수 표시/참조용이다.
    /// </summary>
    /// <param name="layerName">선택할 레이어 이름.</param>
    private void SelectLayerRow(string layerName)
    {
        selectedLayerName = layerName;
        UpdateLayerRowSelectionHighlight();
    }

    /// <summary>선택된 레이어 행만 연한 파란색으로 강조하고 나머지는 투명하게 한다.</summary>
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
    /// 편집 모드인 레이어의 색 견본을 점선으로, 이름을 굵게, 연필 아이콘을 표시한다. 대상은 특정 레이어를
    /// 클릭해 범위를 좁혔으면 그 레이어, 범위 없이(전역 "편집 모드 진입" 버튼) 들어갔으면 편집 가능한
    /// 객체가 실제로 있는 모든 레이어이다.
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

    /// <summary>
    /// 레이어 체크박스 변경 핸들러. 숨김 집합과 도형 Visibility 를 갱신하고, 숨기는 레이어에 현재 선택이 있으면 선택을 해제한다.
    /// 편집 마스크 중인 레이어는 체크되어도 기반 지도 도형을 보이지 않게 유지한다(오버레이가 대신 그림).
    /// </summary>
    /// <param name="sender">변경된 체크박스(Tag=레이어 이름).</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>편집 오버레이가 객체를 그리므로 기반 지도 그림을 숨겨 둔 레이어 이름 집합.</summary>
    private readonly HashSet<string> editMaskedLayers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>레이어의 기반 지도 도형 표시 여부만 바꾼다(체크박스/숨김 집합은 건드리지 않음).</summary>
    /// <param name="layerName">대상 레이어 이름.</param>
    /// <param name="visible">true 면 표시.</param>
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
    /// 편집 가능한 객체가 있는 모든 레이어의 기반 지도 그림을 숨긴다. 이렇게 하지 않으면 이동하지 않은
    /// 원본이 오버레이 아래에 그대로 남아, 이동/회전한 객체가 복사본을 남긴 것처럼 보인다.
    /// </summary>
    /// <param name="layerNames">마스크할 레이어 이름들.</param>
    private void ApplyEditMask(IEnumerable<string> layerNames)
    {
        ClearEditMask();
        foreach (var layerName in layerNames)
        {
            editMaskedLayers.Add(layerName);
            SetBaseLayerVisibility(layerName, visible: false);
        }
    }

    /// <summary>편집 마스크를 해제하고, 사용자가 숨기지 않은 레이어의 기반 지도 그림을 다시 보이게 한다.</summary>
    private void ClearEditMask()
    {
        foreach (var layerName in editMaskedLayers)
        {
            SetBaseLayerVisibility(layerName, visible: !hiddenLayers.Contains(layerName));
        }

        editMaskedLayers.Clear();
    }

    /// <summary>
    /// 레이어 스타일 창을 열어 색/두께/채움/불투명도/이름 변경 또는 초기화를 적용하고 설정에 저장한다.
    /// 이름이 바뀌면 DB 를 갱신한 뒤 전체를 새 이름으로 다시 그리고, 아니면 이미 그려진 도형만 제자리에서 갱신한다.
    /// </summary>
    /// <param name="layerName">대상 레이어의 현재 이름.</param>
    private async Task OpenLayerStyleAsync(string layerName)
    {
        // 1) 현재 스타일을 대화상자에 넘겨 입력받는다(취소 시 변경 없음).
        var existing = connectionSettings.LayerStyles.TryGetValue(layerName, out var current) ? current : null;
        Color? currentFill = TryParseHexColor(existing?.FillColor, out var fillColor) ? fillColor : null;
        var dialog = new LayerStyleWindow(layerName, GetLayerColor(layerName), GetLayerThickness(layerName),
            currentFill, GetLayerFillOpacity(layerName)) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        // 2) 이름이 바뀌었으면 DB 에서 먼저 이름 변경(거부/실패 시 전체 중단).
        var targetName = layerName;
        if (dialog.NameChanged)
        {
            var renamed = await RenameLayerAsync(layerName, dialog.LayerName);
            if (renamed is null)
            {
                return;
            }

            targetName = renamed;
        }

        // 3) 초기화 요청이면 사용자 지정 스타일을 제거하고 팔레트 기본색으로 되돌리며, 아니면 새 스타일을 저장한다.
        if (dialog.ResetRequested)
        {
            connectionSettings.LayerStyles.Remove(targetName);
            layerColors[targetName] = defaultLayerColors.TryGetValue(targetName, out var original) ? original : LayerPalette[0];
        }
        else
        {
            connectionSettings.LayerStyles[targetName] = new LayerStyleSetting
            {
                Color = LayerStyleWindow.ToHex(dialog.SelectedColor),
                Thickness = dialog.SelectedThickness,
                FillColor = dialog.SelectedFillColor is { } fill ? LayerStyleWindow.ToHex(fill) : null,
                FillOpacity = dialog.SelectedFillOpacity
            };
            layerColors[targetName] = dialog.SelectedColor;
        }

        // 4) 설정 파일 저장(실패해도 적용은 유지하고 상태바에 알림).
        try
        {
            await connectionSettings.SaveAsync();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Style applied, but saving it failed: {exception.Message}";
        }

        if (targetName != layerName)
        {
            // 이름 변경은 DB 행이 바뀌었으므로 새 이름으로 전체를 다시 그린다.
            await ReloadForSyncAsync();
            StatusText.Text = $"Layer renamed to '{targetName}'.";
        }
        else
        {
            ApplyLayerStyle(targetName);
        }
    }

    /// <summary>
    /// DB 에서 레이어 이름을 바꾸고 메모리/저장된 레이어별 상태(스타일, 색, 숨김, 필터)를 새 이름으로 옮긴다.
    /// 변경 출처(CurrentChangeContext)를 함께 기록해 다른 세션의 동기화 알림에 표시되게 한다.
    /// </summary>
    /// <param name="oldName">현재 레이어 이름.</param>
    /// <param name="newName">새 레이어 이름(이미 존재하면 거부).</param>
    /// <returns>새 이름. 거부되었거나 실패하면 null.</returns>
    private async Task<string?> RenameLayerAsync(string oldName, string newName)
    {
        if (!Require(Permissions.DrawingEdit, "rename layers") || !CanReloadLayers())
        {
            return null;
        }

        // 1) 중복 이름 검사 후 DB 이름 변경 및 감사 기록.
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            await using (var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId))
            {
                if (await repository.LayerExistsAsync(newName))
                {
                    MessageBox.Show(this, $"A layer named \"{newName}\" already exists. Choose a different name.",
                        "Rename layer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
                }

                var count = await repository.RenameLayerAsync(oldName, newName, changeContext: CurrentChangeContext);
                importLogger.Write($"RENAME layer={oldName} -> {newName} rows={count}");
                await AuditAsync("layer.rename", "layer", oldName, $"-> {newName} ({count} object(s))");
            }

            // 2) 메모리 상태의 키를 이전 이름에서 새 이름으로 이동한다.
            Move(connectionSettings.LayerStyles);
            Move(layerColors);
            Move(defaultLayerColors);
            if (hiddenLayers.Remove(oldName))
            {
                hiddenLayers.Add(newName);
            }

            if (activeLayerFilter is not null)
            {
                for (var i = 0; i < activeLayerFilter.Count; i++)
                {
                    if (string.Equals(activeLayerFilter[i], oldName, StringComparison.OrdinalIgnoreCase))
                    {
                        activeLayerFilter[i] = newName;
                    }
                }
            }

            return newName;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Rename layer failed: {exception.Message}";
            return null;
        }

        /// <summary>사전의 oldName 키 값을 newName 키로 옮기는 로컬 함수.</summary>
        void Move<T>(Dictionary<string, T> map)
        {
            if (map.Remove(oldName, out var value))
            {
                map[newName] = value;
            }
        }
    }

    /// <summary>
    /// 다시 로드하지 않고 레이어의 이미 그려진 도형의 색/두께를 제자리에서 바꾸고, 패널 색 견본을 갱신하며,
    /// 해당 레이어를 편집 중이면 편집 오버레이도 다시 그린다.
    /// </summary>
    /// <param name="layerName">스타일을 적용할 레이어 이름.</param>
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
                    // 폴리곤 채움은 반투명, 점 마커는 불투명 단색이다.
                    shape.Fill = shape.Stroke is null
                        ? new SolidColorBrush(color)
                        : new SolidColorBrush(GetLayerFill(layerName));
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

    /// <summary>토글 버튼의 켜짐 상태를 Tag("On"/null)로 표시한다(스타일 트리거용).</summary>
    /// <param name="button">대상 버튼.</param>
    /// <param name="on">켜짐 여부.</param>
    private static void SetToggleState(Button button, bool on) => button.Tag = on ? "On" : null;

    /// <summary>상태바 줌 표시를 현재 배율의 백분율로 갱신한다.</summary>
    private void UpdateZoomText() => ZoomText.Text = $"{mapScale.ScaleX * 100:0}%";

    /// <summary>상태바 "선택됨" 개수를 갱신한다(편집 모드는 다중 선택 수, 뷰 모드는 0 또는 1).</summary>
    private void UpdateSelectionCount() =>
        SelectionCountText.Text = $"Selected: {(isEditMode ? selectedEditIds.Count : selectedFeature is null ? 0 : 1)}";

    /// <summary>마우스 위치를 월드 좌표로 변환해 상태바에 표시한다.</summary>
    /// <param name="canvasPoint">MapCanvas 기준 마우스 위치(화면 좌표).</param>
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

    /// <summary>마우스가 지도 캔버스를 벗어나면 좌표 표시를 초기화한다.</summary>
    /// <param name="sender">지도 캔버스.</param>
    /// <param name="e">마우스 이벤트 인자.</param>
    private void MapCanvas_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        CursorCoordText.Text = "X -    Y -";

    /// <summary>선택 해제 메뉴/버튼 핸들러. 편집 모드에서는 편집 객체 선택을, 뷰 모드에서는 강조와 속성 패널을 해제한다.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>종료 메뉴 핸들러. 창을 닫는다.</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ExitApp_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 모든 창에 대해 등록된 키 미리보기 핸들러. 단축키 설정의 "화면 복사"(tools.copyScreen) 제스처와
    /// 일치하면(Shift 는 무시, IME/시스템 키 보정) 대화상자가 열려 있어도 화면 복사를 실행한다.
    /// </summary>
    /// <param name="sender">키 이벤트가 발생한 창.</param>
    /// <param name="e">키 이벤트 인자(반복 입력은 무시).</param>
    private void CopyScreenKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var command = shortcutCommands.FirstOrDefault(c => c.Id == "tools.copyScreen");
        if (command is null || e.Handled || !KeyGesture2.TryParse(command.Gesture, out var gesture))
        {
            return;
        }

        var key = e.Key switch { System.Windows.Input.Key.ImeProcessed => e.ImeProcessedKey, System.Windows.Input.Key.System => e.SystemKey, _ => e.Key };
        if (key != gesture.Key || (System.Windows.Input.Keyboard.Modifiers & ~System.Windows.Input.ModifierKeys.Shift) != (gesture.Modifiers & ~System.Windows.Input.ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        if (!e.IsRepeat)
        {
            CopyScreen_Click(this, new RoutedEventArgs());
        }
    }

    /// <summary>프로그램 창 전체(제목 표시줄 제외 콘텐츠 영역)를 이미지로 만들어 클립보드에 복사한다. 열려 있는 대화상자/도구 창도 화면상 위치에 맞춰 위에 합성한다. DPI 배율을 반영한다.</summary>
    private void CopyScreenWpf()
    {
        try
        {
            var root = (FrameworkElement)Content;
            var dpi = VisualTreeHelper.GetDpi(this);
            var width = (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                    null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));

                // 열려 있는 대화상자와 도구 창을 화면 위치 기준으로 위에 덧그린다.
                var origin = root.PointToScreen(new System.Windows.Point(0, 0));
                foreach (var window in Application.Current.Windows.OfType<Window>().Where(w => w != this && w.IsVisible && w.Content is FrameworkElement))
                {
                    var content = (FrameworkElement)window.Content;
                    var at = content.PointToScreen(new System.Windows.Point(0, 0));
                    var rect = new Rect((at.X - origin.X) / dpi.DpiScaleX, (at.Y - origin.Y) / dpi.DpiScaleY, content.ActualWidth, content.ActualHeight);
                    dc.DrawRectangle(Brushes.White, null, rect);
                    dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                        null, rect);
                }
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            Clipboard.SetImage(bitmap);
            StatusText.Text = "Screen copied to the clipboard (" + width + " x " + height + " px).";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot copy the screen: " + ex.Message, "Copy screen", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>도움말 > 사용자 설명서 핸들러. 설치 폴더의 Manual/UserManual.pdf 를 기본 PDF 뷰어로 연다(없으면 경고).</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void UserManual_Click(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Manual", "UserManual.pdf");
        if (!System.IO.File.Exists(path))
        {
            MessageBox.Show(this, "The user manual file was not found:\n" + path, "User manual", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Cannot open the user manual: " + ex.Message, "User manual", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>정보 창 핸들러. 어셈블리 버전과 빌드 번호를 담아 About 창을 모달로 연다.</summary>
    /// <param name="sender">클릭된 메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0";
        new AboutWindow(version, "Build " + BuildInfo.Number + " (" + BuildInfo.Display + ")") { Owner = this }.ShowDialog();
    }

    /// <summary>왼쪽 패널을 숨기기 전 너비(다시 표시할 때 복원).</summary>
    private double leftPanelWidth = 250;
    /// <summary>오른쪽 패널을 숨기기 전 너비(다시 표시할 때 복원).</summary>
    private double rightPanelWidth = 260;

    /// <summary>보기 > 왼쪽 패널 메뉴 체크 변경 핸들러. 숨길 때 현재 너비를 기억하고, 컬럼/스플리터 크기를 0 또는 복원 크기로 바꾼다.</summary>
    /// <param name="sender">메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>보기 > 오른쪽 패널 메뉴 체크 변경 핸들러. 숨길 때 현재 너비를 기억하고, 컬럼/스플리터 크기를 0 또는 복원 크기로 바꾼다.</summary>
    /// <param name="sender">메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>보기 > 상태 표시줄 메뉴 체크 변경 핸들러. 상태바를 표시/숨김한다.</summary>
    /// <param name="sender">메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void StatusBarMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            MainStatusBar.Visibility = StatusBarMenuItem.IsChecked ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>상태바의 연결 요약을 클릭하면 서버 연결 창을 연다.</summary>
    /// <param name="sender">상태바 연결 표시.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private void StatusConnection_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        ConnectServer_Click(sender, new RoutedEventArgs());

    /// <summary>레이어 목록 동작(다시 로드 등)에는 연결, 로그인, 도면 조회 권한이 필요하다. 조건이 안 되면 사유를 안내하고 false 를 반환한다.</summary>
    /// <returns>다시 로드가 가능하면 true.</returns>
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

    /// <summary>레이어 불러오기 메뉴 핸들러. 레이어 선택 창을 띄워 다시 선택한 뒤 로드한다(조건 미충족 시 무시).</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void LoadLayers_Click(object sender, RoutedEventArgs e)
    {
        if (CanReloadLayers())
        {
            await PromptLayerSelectionAndLoadAsync();
        }
    }

    /// <summary>새로고침 핸들러. 현재 선택된 레이어 필터 그대로 서버에서 다시 읽어 렌더링한다.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void RefreshLayers_Click(object sender, RoutedEventArgs e)
    {
        if (CanReloadLayers())
        {
            await LoadImportedPolygonsAsync();
        }
    }

    /// <summary>모든 레이어 표시 핸들러.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ShowAllLayers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerChecksByName.Values)
        {
            checkBox.IsChecked = true;
        }
    }

    /// <summary>모든 레이어 숨김 핸들러.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void HideAllLayers_Click(object sender, RoutedEventArgs e)
    {
        foreach (var checkBox in layerChecksByName.Values)
        {
            checkBox.IsChecked = false;
        }
    }

    /// <summary>레이어 패널에서 선택된 레이어 이름을 가져온다. 선택이 없으면 안내 문구를 표시한다.</summary>
    /// <param name="layerName">선택된 레이어 이름(없으면 빈 문자열).</param>
    /// <returns>선택된 레이어가 있으면 true.</returns>
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

    /// <summary>선택 레이어 스타일 메뉴 핸들러. 스타일 창을 연다.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void SelectedLayerStyle_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            await OpenLayerStyleAsync(layerName);
        }
    }

    /// <summary>선택 레이어로 확대 메뉴 핸들러.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void SelectedLayerZoom_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            ZoomToLayer(layerName);
        }
    }

    /// <summary>선택 레이어 편집 메뉴 핸들러. 해당 레이어의 편집 모드를 전환한다.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void SelectedLayerEdit_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetSelectedLayer(out var layerName))
        {
            await ToggleLayerEditAsync(layerName);
        }
    }

    /// <summary>확대 버튼(1.25배, 캔버스 중심 기준).</summary>
    /// <param name="sender">버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAt(1.25, GetCanvasCenter());

    /// <summary>축소 버튼(0.8배, 캔버스 중심 기준).</summary>
    /// <param name="sender">버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(0.8, GetCanvasCenter());

    /// <summary>화면 맞춤 버튼. 전체가 보이도록 뷰를 초기화한다.</summary>
    /// <param name="sender">버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void FitView_Click(object sender, RoutedEventArgs e) => ResetView();

    /// <summary>뷰 초기화 메뉴 핸들러(화면 맞춤과 동일).</summary>
    /// <param name="sender">메뉴 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void ResetView_Click(object sender, RoutedEventArgs e) => ResetView();

    /// <summary>마우스 휠 줌 핸들러. 포인터 위치를 기준으로 1.2배 확대/축소하며 이벤트를 소비한다.</summary>
    /// <param name="sender">지도 캔버스.</param>
    /// <param name="e">휠 이벤트 인자(Delta 부호로 방향 결정).</param>
    private void MapCanvas_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        ZoomAt(e.Delta > 0 ? 1.2 : 1 / 1.2, e.GetPosition(MapCanvas));
        e.Handled = true;
    }

    /// <summary>
    /// 지도 캔버스 마우스 누름 핸들러. 왼쪽: 편집 모드면 박스 선택 시작(Shift/Ctrl 은 선택 추가),
    /// 뷰 모드면 클릭 지점 피처 선택. 가운데 버튼: 패닝 시작(마우스 캡처).
    /// 편집 객체 자체를 누른 경우는 EditableShape_MouseLeftButtonDown 에서 먼저 처리된다.
    /// </summary>
    /// <param name="sender">지도 캔버스.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private void MapCanvas_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
        {
            if (isEditMode)
            {
                // 빈 곳을 드래그하면 선택 박스를 그린다(Shift 또는 Ctrl 이면 기존 선택에 추가).
                var additive = IsAdditiveSelectKeyDown();
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

    /// <summary>
    /// 지도 캔버스 마우스 이동 핸들러. 좌표 표시를 갱신하고, 편집 드래그 중이면 미리보기를 갱신하며,
    /// 패닝 중이면 시작 이동값 + 마우스 이동량으로 지도를 이동한다.
    /// </summary>
    /// <param name="sender">지도 캔버스.</param>
    /// <param name="e">마우스 이벤트 인자.</param>
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

    /// <summary>
    /// 지도 캔버스 마우스 놓음 핸들러. 편집 드래그 중이면 드래그를 확정(FinalizeEditDrag)하고,
    /// 가운데 버튼이면 패닝을 종료하고 커서를 복원한다.
    /// </summary>
    /// <param name="sender">지도 캔버스.</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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
        // 편집 모드는 십자 커서를 유지하고, 뷰 모드는 일반 화살표를 사용한다.
        MapCanvas.Cursor = isEditMode ? System.Windows.Input.Cursors.Cross : System.Windows.Input.Cursors.Arrow;
    }

    /// <summary>편집 id 의 원본 객체를 반환한다. 미저장 편집이 있으면 그 편집의 Source, 없으면 편집 원본 사전의 값.</summary>
    /// <param name="id">편집용 객체 id(블록 인스턴스는 인코딩된 id).</param>
    /// <returns>원본 편집 대상 객체.</returns>
    private EditableLayerObject GetEditableSource(long id) =>
        pendingEdits.TryGetValue(id, out var edit) ? edit.Source : editableSourcesById[id];

    /// <summary>저장하지 않은 편집(새 객체/삭제/변경)이 하나라도 있는지 여부.</summary>
    private bool HasPendingEdits() =>
        pendingEdits.Values.Any(edit => edit.IsNew || edit.IsDeleted || edit.HasChanges);

    /// <summary>편집 모드 토글 버튼 핸들러. 편집 중이면(미저장 편집은 확인 후 폐기) 종료하고, 아니면 전체 레이어 범위로 편집 모드에 진입한다.</summary>
    /// <param name="sender">편집 모드 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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
    /// 레이어 색 견본 클릭은 해당 레이어만 편집하는 진입점이다: 클릭한 레이어로 범위를 좁혀 편집 모드에 들어가고,
    /// 다른 레이어를 편집 중이면 (폐기 확인 후) 범위를 전환하며, 같은 레이어 견본을 다시 클릭하면 종료한다.
    /// 편집 중인 레이어의 견본은 <see cref="UpdateLayerSwatchEditState"/> 로 점선 표시된다.
    /// </summary>
    /// <param name="sender">색 견본 사각형(Tag=레이어 이름).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private async void LayerSwatch_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Rectangle swatch || swatch.Tag is not string layerName)
        {
            return;
        }

        e.Handled = true;
        await ToggleLayerEditAsync(layerName);
    }

    /// <summary>레이어 단위 편집 전환: 같은 레이어면 종료, 다른 레이어면 (미저장 편집 폐기 확인 후) 전환, 편집 중이 아니면 진입한다.</summary>
    /// <param name="layerName">대상 레이어 이름.</param>
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

    /// <summary>
    /// 편집 모드로 진입한다: DB 에서 편집 가능한 객체와 블록 인스턴스를 읽어 범위(레이어)에 맞는 것만 편집 원본으로 등록하고,
    /// 기반 지도를 마스킹한 뒤 편집 오버레이를 그리고 편집 관련 버튼 상태를 설정한다.
    /// </summary>
    /// <param name="layerName">편집할 레이어 이름. null 이면 화면에 표시 중인 모든 레이어가 대상.</param>
    private async Task EnterEditModeForLayerAsync(string? layerName)
    {
        // 1) 편집 권한 확인, 최신 연결 설정을 읽고 작업 중 오버레이를 표시한다.
        if (!Require(Permissions.DrawingEdit, "edit drawings"))
        {
            return;
        }

        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            ShowBusy("Loading editable objects...");
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
            // spatial_block_instances.updated_at(블록 인스턴스 편집용으로 추가된 컬럼)이 없는 DB 라도
            // 존재하도록 보장한다. 그렇지 않으면 이후 FindEditableBlockInstancesAsync 가 실패하고
            // 편집 모드가 아무 말 없이 동작하지 않게 된다.
            await repository.EnsureSchemaAsync();
            // 2) 편집 가능 객체(일반 피처)와 블록 인스턴스를 읽는다.
            var objects = await repository.FindEditableLayerObjectsAsync();
            var blockInstances = await repository.FindEditableBlockInstancesAsync();
            var instanceLayers = blockInstances
                .Select(instance => instance.LayerName ?? "(no layer)")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // "편집" 툴바 버튼은 모든 레이어를 한꺼번에 편집하지만, 실제로 화면에 있는 것에만 한정해야 한다.
            // 그렇지 않으면 사용자가 숨겼거나 레이어 선택 창에서 로드하지 않은 레이어가 편집 모드 진입
            // 순간 편집 가능한 도형으로 조용히 다시 나타난다.
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

            // 3) 범위 안의 일반 객체를 편집 원본으로 등록한다. 블록 인스턴스가 있는 레이어의 일반 객체는 제외(중복 방지).
            editableSourcesById.Clear();
            foreach (var obj in objects.Where(o => !instanceLayers.Contains(o.LayerName ?? "(no layer)")))
            {
                if (IsInScope(obj.LayerName ?? "(no layer)"))
                {
                    editableSourcesById[obj.Id] = obj;
                }
            }

            // 블록 인스턴스(예: 설비)는 실제로 그려진 모양(Geometry)과 경계 윤곽(OuterGeometry)을 같은 행에
            // 가지고 있다. spatial_features 객체와 같은 미저장 편집 파이프라인으로 처리하면 이동/회전이
            // 두 지오메트리를 자동으로 함께 변환한다. id 는 상위 비트로 태깅하여 spatial_features id 와
            // 충돌 없이 같은 long 키 사전을 공유하며, 저장 구문을 만들 때만 원래대로 해독한다.
            foreach (var obj in blockInstances)
            {
                if (!IsInScope(obj.LayerName ?? "(no layer)"))
                {
                    continue;
                }

                var encoded = obj with { Id = EncodeBlockInstanceId(obj.Id) };
                editableSourcesById[encoded.Id] = encoded;
            }

            // 4) 이전 편집 상태/Undo 기록/비교 오버레이를 초기화하고, 편집 객체가 있는 레이어의 기반 지도 그림을 숨긴다(마스크).
            pendingEdits.Clear();
            ClearUndoHistory();
            ClearDiffOverlay();
            ApplyEditMask(editableSourcesById.Values.Select(source => source.LayerName ?? "(no layer)").Distinct(StringComparer.OrdinalIgnoreCase));

            // 뷰 모드에서 남은 선택 강조는 저장된 위치의 기반 지도에 그려져 있어, 객체를 이동/회전할 때
            // 유령처럼 남으므로 지운다.
            ClearSelectionHighlight();
            SelectedObjectText.Text = "No object selected";
            ClearPropertyDetails();
            // 5) 선택/정점 편집/버튼 상태를 초기 상태로 되돌리고 편집 모드 플래그를 켠다(임포트/삭제는 편집 중 비활성화).
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
            // 6) 편집 오버레이를 그리고 레이어 견본 표시와 상태 문구를 갱신한다.
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

    /// <summary>
    /// 편집 모드를 종료하고 편집 상태를 모두 정리한다: 드래그/핸들/오버레이/미저장 편집/Undo 기록/선택을 초기화하고,
    /// 기반 지도 마스크를 해제하며 버튼 상태를 복원한 뒤, 편집 중 쌓인 서버 변경 확인을 예약한다.
    /// 호출 전에 미저장 편집의 폐기 여부는 호출자가 확인해야 한다.
    /// </summary>
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
        ClearUndoHistory();
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
        // 편집 중 보류된 다른 사용자의 변경 알림을 이제 확인할 수 있다.
        ScheduleSyncCheck();
    }

    /// <summary>편집 도형/상세 경로가 줌 두께 보정 목록(baseStrokeWidths)에 남지 않도록 제거한다(오버레이를 다시 그리거나 종료하기 전에 호출).</summary>
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

    /// <summary>
    /// 편집 오버레이를 처음부터 다시 그린다: 삭제 표시되지 않은 모든 편집 객체를 DrawEditableObject 로 그리고,
    /// 더 이상 존재하지 않는 선택(삭제/재그룹)은 제외한 뒤 남은 선택을 복원한다.
    /// </summary>
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

        // 더 이상 존재하지 않는 선택(삭제/재그룹됨)을 제거하고 나머지를 복원한다.
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

    /// <summary>
    /// 편집 객체 하나를 오버레이 캔버스에 그린다: (1) 실제 모양인 상세 경로, (2) 클릭/드래그 대상이 되는 투명 외곽 폴리곤.
    /// 현재 편집 상태(이동/회전 반영)의 지오메트리를 사용하며, 새 객체/선택 여부에 따라 스타일이 달라진다.
    /// </summary>
    /// <param name="id">편집용 객체 id(블록 인스턴스는 인코딩된 id).</param>
    private void DrawEditableObject(long id)
    {
        pendingEdits.TryGetValue(id, out var edit);
        var source = edit?.Source ?? editableSourcesById[id];
        var outer = edit?.CurrentOuterGeometry ?? source.OuterGeometry;
        var isNew = edit is { IsNew: true };
        var isSelected = selectedEditIds.Contains(id);

        // 편집 객체는 자신의 전체 모양을 직접 그린다. 레이어를 편집하는 동안 기반 지도 레이어는 숨겨지므로
        // (ApplyEditMask 참조) 객체는 현재(이동/회전되었을 수 있는) 위치에 정확히 한 번만 보인다.
        var currentGeometry = edit?.CurrentGeometry ?? source.Geometry;
        var detailPath = BuildDetailPath(currentGeometry, source.LayerName);
        if (detailPath is not null)
        {
            EditableObjectsCanvas.Children.Add(detailPath);
            editableDetailPathById[id] = detailPath;
        }

        // 블록 인스턴스(특히 빗/톱니 모양 설비)는 하나의 깔끔한 외곽선이 아니라 서로 떨어진 작은 닫힌
        // 영역 여러 개로 폴리곤화되는 경우가 많다(예: 빗살 하나하나가 각자 닫힌 작은 폴리곤).
        // 그대로 쓰면 클릭 대상과 선택 강조가 작은 조각들로 흩어져 객체 전체를 덮지 못한다.
        // 그래서 이런 객체는 경계 상자를 상호작용/선택용 도형으로 써서 전체를 덮는 하나의 깔끔한 클릭
        // 대상을 만든다(실제 외곽은 위에 그린 상세 경로로 계속 보인다).
        // 폴리곤화된 외곽(outer_geom)은 발견한 닫힌 영역만 덮는 경우가 많으므로, 블록의 경계는
        // 모든 부품(geom)의 범위와 항상 객체 전체를 둘러싸는 외곽의 범위를 합친 것으로 한다.
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

    /// <summary>블록의 지오메트리(모든 부품)와 외곽 지오메트리의 경계 상자를 합쳐 블록 전체 경계 상자를 구한다.</summary>
    /// <param name="geometry">블록의 모든 부품 지오메트리.</param>
    /// <param name="outer">폴리곤화된 외곽 지오메트리.</param>
    /// <returns>두 범위를 모두 포함하는 경계 상자(월드 좌표).</returns>
    private static Envelope BlockBounds(NetTopologySuite.Geometries.Geometry geometry, NetTopologySuite.Geometries.Geometry outer)
    {
        var bounds = new Envelope(geometry.EnvelopeInternal);
        bounds.ExpandToInclude(outer.EnvelopeInternal);
        return bounds;
    }

    /// <summary>경계 상자를 닫힌 직사각형 링 좌표(5점, 시작=끝)로 변환한다.</summary>
    /// <param name="envelope">경계 상자.</param>
    /// <returns>직사각형 외곽 좌표 배열.</returns>
    private static Coordinate[] EnvelopeRingCoordinates(Envelope envelope) => new[]
    {
        new Coordinate(envelope.MinX, envelope.MinY),
        new Coordinate(envelope.MaxX, envelope.MinY),
        new Coordinate(envelope.MaxX, envelope.MaxY),
        new Coordinate(envelope.MinX, envelope.MaxY),
        new Coordinate(envelope.MinX, envelope.MinY)
    };

    /// <summary>
    /// 편집 도형 스타일: 선택(편집 중) 객체는 점선 + 선택 색, 저장 전 새 객체는 녹색 점선,
    /// 그 외에는 파란 점선 외곽선. 선 두께는 줌에 관계없이 화면 폭이 일정하도록 등록한다.
    /// </summary>
    /// <param name="shape">스타일을 적용할 도형.</param>
    /// <param name="isSelected">선택 여부.</param>
    /// <param name="isNew">새로 만든(미저장) 객체 여부.</param>
    private void ApplyEditableShapeStyle(Shape shape, bool isSelected, bool isNew)
    {
        shape.Stroke = isSelected ? SelectionBrush : (isNew ? Brushes.MediumSeaGreen : Brushes.DodgerBlue);
        var width = isSelected ? SelectionThickness : 1.0;
        // 확대/축소해도 외곽선의 화면 폭이 일정하도록 기록한다.
        baseStrokeWidths[shape] = width;
        shape.StrokeThickness = width / Math.Max(mapScale.ScaleX, 0.1);
        // 모든 편집 외곽선은 점선이며 색으로 상태를 구분한다(금색 선택, 녹색 신규).
        shape.StrokeDashArray = isSelected
            ? new DoubleCollection { 4, 2 }
            : new DoubleCollection { 3, 2 };
    }

    /// <summary>
    /// 블록 인스턴스의 실제 그려진 모양(geom 컬럼: DXF Insert 를 이루는 엔티티들의 GeometryCollection)을
    /// 하나의 비상호작용 오버레이 Path 로 그린다. 일반 지도 렌더링과 같은 도형 추가 도우미를 쓴다.
    /// 순수 시각용이며, 선택/드래그 히트 테스트는 그 위에 그려진 OuterGeometry 폴리곤이 처리한다.
    /// </summary>
    /// <param name="geometry">블록 지오메트리(단일 도형 또는 GeometryCollection).</param>
    /// <param name="layerName">레이어 이름(색/두께 결정용, null 가능).</param>
    /// <returns>상세 Path. 부품이 없으면 null.</returns>
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
        // 기반 지도 선처럼 줌 중에도 화면상 선 두께가 일정하도록 기록한다.
        baseStrokeWidths[detail] = thickness;
        detail.StrokeThickness = thickness / Math.Max(mapScale.ScaleX, 0.1);
        return detail;
    }

    /// <summary>폴리곤 또는 멀티폴리곤에서 외곽(exterior) 링만 순서대로 추출한다. 구멍(내부 링)은 편집 클릭 대상에서 제외된다.</summary>
    /// <param name="geometry">입력 지오메트리(폴리곤/멀티폴리곤 외에는 결과 없음).</param>
    /// <returns>외곽 링 시퀀스.</returns>
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

    /// <summary>
    /// 편집 도형 마우스 왼쪽 누름 핸들러(편집 객체 클릭/드래그의 시작). 이벤트를 소비해 지도 캔버스의 박스 선택이 시작되지 않게 한다.
    /// Shift/Ctrl 이면 선택 토글만 하고, 일반 클릭이면 선택(그룹 유지) 후 이동 드래그를 시작한다. 정점 편집 모드에서는 이동 드래그를 하지 않는다.
    /// </summary>
    /// <param name="sender">클릭된 편집 폴리곤(Tag=편집 id).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
    private void EditableShape_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!isEditMode || sender is not System.Windows.Shapes.Polygon shape || shape.Tag is not long id)
        {
            return;
        }

        e.Handled = true;
        var ctrl = IsAdditiveSelectKeyDown();
        // Shift/Ctrl+클릭은 드래그 없이 선택 포함 여부만 토글한다. 이미 선택된 객체를 일반 클릭하면
        // 그룹을 유지해 전체를 함께 드래그할 수 있다.
        SelectEditableObject(id, additive: ctrl, keepGroup: true);
        if (!ctrl && !isVertexEditMode)
        {
            BeginMoveDrag(e.GetPosition(EditableObjectsCanvas));
        }
    }

    /// <summary>
    /// 회전 핸들은 드래그가 아니라 한 번 클릭하는 방식이다: 클릭할 때마다 선택 객체를 경계 상자 중심을
    /// 기준으로 시계 방향 90도 회전한다. NTS AffineTransformation.RotationInstance 에 음수 각도
    /// (월드 공간의 표준 반시계 양수 규약)를 주면 화면 투영(표시를 위해 Y 가 뒤집힘) 후 시계 방향 회전으로 보인다.
    /// </summary>
    /// <param name="sender">회전 핸들(원).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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
    /// 선택 전체를 합친 경계 상자의 중심을 기준으로 90도 회전한다: 시계 방향(금색 핸들 또는 "r" 키) 또는
    /// 반시계 방향(Shift+R). 회전한 설비가 다른 설비와 겹치면 "장비영역 겹침" 오류로 거부한다.
    /// </summary>
    /// <param name="clockwise">true 면 시계 방향, false 면 반시계 방향.</param>
    private void RotateSelected(bool clockwise)
    {
        // 1) 선택 도형 전체의 화면 경계 중심을 구해 월드 좌표 회전 중심(pivot)으로 변환한다.
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

        // 월드 공간은 반시계가 양수(화면 Y 는 뒤집힘)이므로 시계 방향은 -90도이다.
        var ccwDegrees = clockwise ? -90.0 : 90.0;
        // 2) 회전을 실제로 적용하기 전에 겹침 검사로 거부 여부를 먼저 판단한다(저장 전 상태 오염 방지).
        var rotation = AffineTransformation.RotationInstance(ccwDegrees * Math.PI / 180.0, pivotWorld.X, pivotWorld.Y);
        if (RejectEquipmentOverlap(selectedEditIds, geometry => rotation.Transform(geometry)))
        {
            return;
        }

        // 3) Undo 스냅샷을 기록한 뒤 선택된 모든 객체에 같은 중심 회전을 적용한다(그룹이 강체로 회전). 블록은 배치 속성(삽입점/회전각)도 갱신한다.
        PushUndo();
        // 선택된 모든 객체가 공통 중심을 기준으로 회전하므로 그룹이 강체처럼 회전한다.
        foreach (var selectedId in selectedEditIds.ToArray())
        {
            var edit = GetOrCreateEdit(selectedId);
            edit.CurrentGeometry = rotation.Transform(edit.CurrentGeometry);
            edit.CurrentOuterGeometry = rotation.Transform(edit.CurrentOuterGeometry);
            ApplyPlacementRotation(edit, pivotWorld.X, pivotWorld.Y, ccwDegrees);
            edit.HasChanges = true;
        }

        // 4) 오버레이를 다시 그리고 저장 버튼을 활성화한다.
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = clockwise ? "Rotated 90° clockwise." : "Rotated 90° counter-clockwise.";
    }

    /// <summary>
    /// Ctrl+방향키로 선택 객체를 미세 이동한다(Ctrl+Shift: 10배). 이동 단위는 격자 스냅이 켜져 있으면 격자 한 칸
    /// (10 단위), 아니면 1 단위. 텍스트 입력 컨트롤에 포커스가 있으면 처리하지 않는다.
    /// </summary>
    /// <param name="e">키 이벤트 인자(처리하면 Handled=true).</param>
    /// <returns>키를 사용했으면 true.</returns>
    private bool TryNudgeFromKey(System.Windows.Input.KeyEventArgs e)
    {
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        if ((modifiers & System.Windows.Input.ModifierKeys.Control) == 0
            || (modifiers & (System.Windows.Input.ModifierKeys.Alt | System.Windows.Input.ModifierKeys.Windows)) != 0
            || System.Windows.Input.Keyboard.FocusedElement is TextBox or PasswordBox or ComboBox or DataGrid)
        {
            return false;
        }

        // 방향 결정: 월드 좌표 기준(위 = +Y).
        var (dirX, dirY) = e.Key switch
        {
            System.Windows.Input.Key.Left => (-1, 0),
            System.Windows.Input.Key.Right => (1, 0),
            System.Windows.Input.Key.Up => (0, 1),
            System.Windows.Input.Key.Down => (0, -1),
            _ => (0, 0)
        };
        if (dirX == 0 && dirY == 0)
        {
            return false;
        }

        e.Handled = true;
        if (selectedEditIds.Count == 0 || editDragKind != EditDragKind.None)
        {
            return true;
        }

        var step = isGridSnapEnabled ? GridSnapSizeWorldUnits : 1.0;
        if ((modifiers & System.Windows.Input.ModifierKeys.Shift) != 0)
        {
            step *= 10;
        }

        var dx = dirX * step;
        var dy = dirY * step;
        // 겹침 검사 → Undo 기록 → 선택 객체 이동(블록 배치 속성 포함) 순으로 적용한다.
        var translation = AffineTransformation.TranslationInstance(dx, dy);
        if (RejectEquipmentOverlap(selectedEditIds, geometry => translation.Transform(geometry)))
        {
            return true;
        }

        PushUndo();
        foreach (var id in selectedEditIds.ToArray())
        {
            var edit = GetOrCreateEdit(id);
            edit.CurrentGeometry = translation.Transform(edit.CurrentGeometry);
            edit.CurrentOuterGeometry = translation.Transform(edit.CurrentOuterGeometry);
            ApplyPlacementMove(edit, dx, dy);
            edit.HasChanges = true;
        }

        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = $"Moved {selectedEditIds.Count} object(s) by ({dx:0.##}, {dy:0.##}).";
        return true;
    }

    /// <summary>격자 스냅 체크박스 변경 핸들러. 스냅 사용 여부를 isGridSnapEnabled 에 반영한다.</summary>
    /// <param name="sender">격자 스냅 체크박스.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void SnapToGridCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        isGridSnapEnabled = SnapToGridCheckBox.IsChecked == true;
    }

    /// <summary>
    /// 선택 객체의 이동 드래그를 시작한다: 시작 위치를 기록하고 도형 점 스냅샷을 저장한 뒤 마우스를 캡처한다.
    /// </summary>
    /// <param name="start">드래그 시작 위치(EditableObjectsCanvas 기준 로컬 좌표).</param>
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

    /// <summary>드래그 시작 시점의 선택 도형 점들과 회전 핸들 선 위치를 저장한다(드래그 중 원본 기준으로 변위를 적용해 오차 누적 방지).</summary>
    /// <param name="shapes">스냅샷할 선택된 편집 도형들.</param>
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

    /// <summary>
    /// 드래그 중 미리보기를 갱신한다. 이동: 도형 점과 핸들, 상세 경로(RenderTransform)를 화면 이동량만큼 옮긴다
    /// (지오메트리 자체는 놓을 때 확정). 박스: 선택 사각형 크기를 갱신. 정점: 정점 드래그 미리보기.
    /// </summary>
    /// <param name="current">현재 마우스 위치(EditableObjectsCanvas 기준 로컬 좌표).</param>
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
    /// 원시 드래그 거리를 화면 공간 이동량(실시간 도형 미리보기용)과 월드 공간 이동량(놓을 때 적용하는
    /// 지오메트리 이동용)으로 변환한다. 격자 스냅이 켜져 있으면 이동한 객체 외곽의 중심점(centroid)이
    /// <see cref="GridSnapSizeWorldUnits"/> 격자 교차점에 놓이도록 둘 다 보정하며, 작은 마우스 이동이
    /// 반복되어도 반올림 오차가 누적되지 않도록 드래그 전 위치 기준으로 계산한다.
    /// </summary>
    /// <param name="current">현재 마우스 위치(로컬 좌표).</param>
    /// <returns>(화면 dx, 화면 dy, 월드 dx, 월드 dy). 월드 Y 는 화면과 반대 방향.</returns>
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

    /// <summary>회전 핸들(원)과 연결선을 스냅샷 위치에서 delta 만큼 옮긴다(이동 드래그 미리보기).</summary>
    /// <param name="delta">화면 이동량(로컬 좌표).</param>
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

    /// <summary>
    /// 마우스를 놓을 때 드래그를 확정한다. 박스는 선택 처리, 이동은 월드 이동량으로 지오메트리를 변환(겹치면 취소하고 원위치),
    /// 정점은 정점 위치 확정. Undo 를 기록하고 오버레이를 다시 그린다.
    /// </summary>
    /// <param name="current">놓은 위치(로컬 좌표).</param>
    private void FinalizeEditDrag(System.Windows.Point current)
    {
        // 1) 드래그 상태 해제와 마우스 캡처 해제, 스냅샷 정리.
        var kind = editDragKind;
        editDragKind = EditDragKind.None;
        MapCanvas.ReleaseMouseCapture();
        editShapeSnapshot.Clear();
        editHandleSnapshot = null;

        // 2) 박스 선택이면 선택 처리로 위임하고 종료.
        if (kind == EditDragKind.Box)
        {
            FinalizeBoxSelect(current);
            return;
        }

        if (selectedEditId is not long id)
        {
            return;
        }

        // 3) 이동/정점 확정: 이동은 겹침 검사 후 Undo 기록 → 선택 객체 모두에 같은 이동 적용(미세 이동은 무시).
        var edit = GetOrCreateEdit(id);

        if (kind == EditDragKind.Move)
        {
            var (_, _, worldDx, worldDy) = ComputeMoveDelta(current);
            if (Math.Abs(worldDx) > 1e-9 || Math.Abs(worldDy) > 1e-9)
            {
                var translation = AffineTransformation.TranslationInstance(worldDx, worldDy);
                if (RejectEquipmentOverlap(selectedEditIds, geometry => translation.Transform(geometry)))
                {
                    // 다른 설비 위에 놓았으면, 집었던 위치로 되돌린다.
                    RenderEditableLayerObjects();
                    return;
                }

                PushUndo();
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

    /// <summary>Shift 또는 Ctrl 이 눌렸는지 여부: 클릭과 선택 박스가 현재 선택에 추가/토글된다.</summary>
    private static bool IsAdditiveSelectKeyDown() =>
        (System.Windows.Input.Keyboard.Modifiers & (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift)) != 0;

    /// <summary>선분 a-b 가 사각형과 닿는지 판정한다(끝점 중 하나가 안에 있거나 변을 가로지름).</summary>
    /// <param name="rect">검사 사각형(로컬 좌표).</param>
    /// <param name="a">선분 시작점.</param>
    /// <param name="b">선분 끝점.</param>
    /// <returns>닿거나 교차하면 true.</returns>
    private static bool SegmentTouchesRect(Rect rect, System.Windows.Point a, System.Windows.Point b)
    {
        if (rect.Contains(a) || rect.Contains(b))
        {
            return true;
        }

        // Liang-Barsky 알고리즘으로 선분을 사각형에 대해 클리핑한다.
        double t0 = 0, t1 = 1;
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        bool Clip(double p, double q)
        {
            if (p == 0)
            {
                return q >= 0;
            }

            var r = q / p;
            if (p < 0)
            {
                if (r > t1) { return false; }
                if (r > t0) { t0 = r; }
            }
            else
            {
                if (r < t0) { return false; }
                if (r < t1) { t1 = r; }
            }

            return true;
        }

        return Clip(-dx, a.X - rect.Left) && Clip(dx, rect.Right - a.X)
            && Clip(-dy, a.Y - rect.Top) && Clip(dy, rect.Bottom - a.Y);
    }

    /// <summary>교차(crossing) 판정: 외곽선의 점이 박스 안에 있거나 외곽선의 어느 변이든 박스에 닿으면 true.</summary>
    /// <param name="rect">선택 박스(로컬 좌표).</param>
    /// <param name="shape">검사할 외곽 폴리곤.</param>
    /// <returns>닿으면 true.</returns>
    private static bool OutlineTouchesRect(Rect rect, System.Windows.Shapes.Polygon shape)
    {
        var points = shape.Points;
        if (points.Count == 0)
        {
            return false;
        }

        if (points.Count == 1)
        {
            return rect.Contains(points[0]);
        }

        for (var i = 0; i < points.Count; i++)
        {
            if (SegmentTouchesRect(rect, points[i], points[(i + 1) % points.Count]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 박스 선택을 시작한다: 시작점/추가 모드를 기록하고 점선 사각형을 오버레이에 추가한 뒤 마우스를 캡처한다.
    /// </summary>
    /// <param name="start">박스 시작 위치(로컬 좌표).</param>
    /// <param name="additive">true 면 기존 선택에 추가(Shift/Ctrl).</param>
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
    /// 러버밴드(박스) 선택을 확정한다. 설비(블록 인스턴스)는 박스가 조금이라도 닿으면(교차 선택: 안에 있거나
    /// 겹치거나 외곽을 가로지름) 선택된다. 그 외 객체(예: 큰 레이어 외곽선)는 박스 안에 완전히 들어올 때만
    /// 선택되어, 박스가 걸쳤다는 이유만으로 큰 외곽선이 잡히지 않는다. Shift/Ctrl 은 기존 선택에 추가한다.
    /// </summary>
    /// <param name="current">박스 끝 위치(로컬 좌표).</param>
    private void FinalizeBoxSelect(System.Windows.Point current)
    {
        if (boxSelectRectangle is not null)
        {
            EditableObjectsCanvas.Children.Remove(boxSelectRectangle);
            boxSelectRectangle = null;
        }

        // 박스가 너무 작으면(3px 미만) 클릭으로 보고 빈 곳 클릭 시 선택을 해제한다.
        var rect = new Rect(boxSelectStart, current);
        if (rect.Width < 3 && rect.Height < 3)
        {
            if (!boxSelectAdditive)
            {
                DeselectEditableObject();
            }

            return;
        }

        // 박스에 걸린 객체 id 수집: 블록 → 교차 판정, 그 외 → 완전 포함 판정.
        var picked = editableShapesById
            .Where(pair => pair.Value.Count > 0 && (IsEncodedBlockInstanceId(pair.Key)
                || (pendingEdits.TryGetValue(pair.Key, out var pickEdit) && pickEdit.Source.FeatureType == "block_instance")
                    ? pair.Value.Any(shape => OutlineTouchesRect(rect, shape))
                    : pair.Value.All(shape => shape.Points.All(rect.Contains))))
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

// ------------------------------------------------------------------ 설비 영역 겹침 금지 규칙

    /// <summary>두 경계 상자가 실제 면적으로 겹치는지 판정한다(변이 닿는 정도는 허용, 허용 오차 1e-6).</summary>
    /// <param name="a">첫 번째 경계 상자(월드 좌표).</param>
    /// <param name="b">두 번째 경계 상자(월드 좌표).</param>
    /// <returns>겹침 면적이 있으면 true.</returns>
    private static bool RectanglesOverlap(Envelope a, Envelope b)
    {
        // 변이 닿는 것은 허용하며, 실제 겹치는 면적이 있을 때만 겹침으로 본다.
        const double tolerance = 1e-6;
        var width = Math.Min(a.MaxX, b.MaxX) - Math.Max(a.MinX, b.MinX);
        var height = Math.Min(a.MaxY, b.MaxY) - Math.Max(a.MinY, b.MinY);
        return width > tolerance && height > tolerance;
    }

    /// <summary>
    /// 편집 세션 내 모든 블록 객체의 설비 영역(점선 사각형)을 반환한다. 미저장 상태의 현재 위치를 사용하고
    /// 삭제 표시된 객체는 제외한다.
    /// </summary>
    /// <param name="exclude">제외할 편집 id 집합(이동 중인 객체 자신 등, null 가능).</param>
    /// <returns>(편집 id, 경계 상자) 목록.</returns>
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

    /// <summary>겹침 오류 메시지에 쓸 설비 설명(블록 이름 + 번호, 새 객체는 "(new)")을 만든다.</summary>
    /// <param name="id">편집용 객체 id.</param>
    /// <returns>표시용 문자열.</returns>
    private string DescribeEquipment(long id)
    {
        var source = GetEditableSource(id);
        var name = string.IsNullOrWhiteSpace(source.BlockName) ? "block" : source.BlockName;
        return id < 0 ? $"{name} (new)" : $"{name} #{(IsEncodedBlockInstanceId(id) ? DecodeBlockInstanceId(id) : id)}";
    }

    /// <summary>설비영역 겹침 오류를 상태 표시와 오류 대화상자로 알린다.</summary>
    /// <param name="first">겹친 첫 번째 설비의 편집 id.</param>
    /// <param name="second">겹친 두 번째 설비의 편집 id.</param>
    /// <param name="consequence">사용자에게 알릴 결과 설명(예: 이동이 취소됨).</param>
    private void ReportEquipmentOverlap(long first, long second, string consequence)
    {
        var detail = $"{DescribeEquipment(first)}  <->  {DescribeEquipment(second)}";
        EditStatusText.Text = $"장비영역 겹침: {detail}";
        MessageBox.Show(this,
            "장비영역 겹침" + Environment.NewLine + Environment.NewLine + detail + Environment.NewLine + Environment.NewLine + consequence,
            "장비영역 겹침", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// 설비 영역은 겹칠 수 없다. 이동 중인 블록 객체가 <paramref name="transform"/> 적용 후 놓일 위치를 다른 모든
    /// 블록 객체와 비교해, 겹치면 오류를 알리고 true 를 반환하여 호출자가 원래 위치를 유지하게 한다.
    /// 상태(미저장 편집)는 변경하지 않으며 변환된 사본으로만 검사한다.
    /// </summary>
    /// <param name="movingIds">이동/회전하려는 객체의 편집 id 모음.</param>
    /// <param name="transform">지오메트리에 적용할 변환(이동/회전).</param>
    /// <returns>겹침이 발견되어 작업을 거부해야 하면 true.</returns>
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

    /// <summary>
    /// 저장 전 검사: 새로 만들었거나 변경된 설비가 다른 설비와 여전히 겹치면 저장을 막는다.
    /// </summary>
    /// <param name="silent">true 면 오류 대화상자를 띄우지 않고 결과만 반환(자동 확인용).</param>
    /// <returns>겹침이 있으면 true.</returns>
    private bool HasEquipmentOverlapBeforeSave(bool silent = false)
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
                    if (!silent)
                    {
                        ReportEquipmentOverlap(id, otherId, "겹친 설비를 옮긴 뒤 다시 저장하세요. 저장하지 않았습니다.");
                    }

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>편집 id 의 미저장 편집 상태를 가져오고, 없으면 원본으로부터 만들어 pendingEdits 에 등록한다.</summary>
    /// <param name="id">편집용 객체 id.</param>
    /// <returns>해당 객체의 편집 상태(null 아님).</returns>
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
    /// <paramref name="id"/> 를 대표(primary) 객체로 선택한다. <paramref name="additive"/>(Ctrl+클릭)는
    /// 다중 선택에서 그 객체를 넣거나 뺀다. <paramref name="keepGroup"/> 은 이미 그 객체를 포함한
    /// 다중 선택을 유지한다(선택된 객체를 일반 클릭할 때 또는 다시 그릴 때).
    /// 선택이 바뀌면 도형 스타일, 회전/정점 핸들, 속성 패널, 버튼 활성 상태를 함께 갱신한다.
    /// </summary>
    /// <param name="id">선택할 편집용 객체 id.</param>
    /// <param name="additive">true 면 기존 선택에 토글로 추가/제거.</param>
    /// <param name="keepGroup">true 면 기존 다중 선택 유지.</param>
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
                // 하나를 뺀 경우: 뺀 것이 대표였을 때만 대표를 바꾸고, 그 외에는 기존 대표를 유지한다.
                id = selectedEditId is long primary && primary != id ? primary : selectedEditIds.First();
            }
        }
        else if (!(keepGroup && selectedEditIds.Contains(id)))
        {
            selectedEditIds.Clear();
            selectedEditIds.Add(id);
        }

        // 1) 대표 객체 확정. 선택이 달라졌으면 정점 편집 모드를 해제한다(정점 편집은 단일 객체에서만 가능).
        var isSameSelection = selectedEditId == id && selectedEditIds.Count == 1;
        selectedEditId = id;
        RemoveRotateHandle();

        if (!isSameSelection || selectedEditIds.Count > 1)
        {
            isVertexEditMode = false;
            ClearVertexHandles();
            SetToggleState(VertexEditButton, false);
        }

        // 2) 모든 편집 도형에 선택/신규 상태에 맞는 스타일을 다시 적용한다.
        foreach (var (shapeId, shapes) in editableShapesById)
        {
            var isSelected = selectedEditIds.Contains(shapeId);
            var isNew = pendingEdits.TryGetValue(shapeId, out var shapeEdit) && shapeEdit.IsNew;
            foreach (var shape in shapes)
            {
                ApplyEditableShapeStyle(shape, isSelected, isNew);
            }
        }

        // 3) 정점 편집 모드면 정점 핸들을, 아니면 회전 핸들을 만든다.
        if (isVertexEditMode)
        {
            RenderVertexHandles(id);
        }
        else
        {
            CreateRotateHandle(id);
        }

        // 4) 속성 패널(요약, 힌트, 속성 그리드)과 편집 버튼(복제/삭제/그룹/정점 편집) 상태를 갱신한다.
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
            ? "Multi-selection: drag any selected shape to move all, click the gold handle or press r to rotate all 90° clockwise (Shift+R: counter-clockwise).\nShift/Ctrl+click toggles an object; drag a box on empty space to select every object it touches (hold Shift/Ctrl to add).\nGroup turns the selection into one block."
            : isBlock
                ? "Block instance: moves, rotates, duplicates and deletes as one object.\nDrag the shape to move it.\nr: rotate 90° clockwise, Shift+R: counter-clockwise (or click the gold handle)."
                : "Drag the shape to move it.\nr: rotate 90° clockwise, Shift+R: counter-clockwise (or click the gold handle).\nToggle Vertex edit to reshape the outline.";
        var shownAttributes = pendingEdits.TryGetValue(id, out var attributeEdit) ? attributeEdit.Attributes : source.Attributes;
        AttributesGrid.ItemsSource = shownAttributes.Select(attribute => new AttributeRow(attribute.Key, attribute.Value)).ToArray();
        DuplicateButton.IsEnabled = true;
        DeleteObjectButton.IsEnabled = true;
        GroupButton.IsEnabled = isMulti;
        // 외곽만 재구성하면 블록의 실제 지오메트리와 어긋나므로 블록/다중 선택에서는 정점 편집을 막는다.
        VertexEditButton.IsEnabled = !isBlock && !isMulti;
        VertexEditButton.ToolTip = isBlock ? "Vertex edit is not available for block instances" : "Vertex edit";
        UpdateSelectionCount();
    }

    /// <summary>편집 객체 선택을 모두 해제하고 핸들/속성 패널/버튼 상태를 초기화하며 모든 도형을 비선택 스타일로 되돌린다.</summary>
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

    /// <summary>현재 선택된 모든 객체의 편집 폴리곤 도형 목록을 반환한다.</summary>
    /// <returns>선택된 도형 목록(선택이 없으면 빈 목록).</returns>
    private List<System.Windows.Shapes.Polygon> SelectedShapes() =>
        selectedEditIds.SelectMany(id => editableShapesById.TryGetValue(id, out var s) ? s : new()).ToList();

    /// <summary>선택 전체의 합친 경계 상단 중앙에서 위쪽(34px)에 금색 회전 핸들(원 + 연결선)을 만든다. 핸들 클릭은 90도 회전 이벤트로 연결된다.</summary>
    /// <param name="id">대표 선택 객체의 편집 id(현재 핸들 위치 계산은 선택 전체를 기준으로 함).</param>
    private void CreateRotateHandle(long id)
    {
        // 핸들은 선택 전체의 합친 경계 위에 놓인다.
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

    /// <summary>회전 핸들과 연결선을 오버레이에서 제거한다.</summary>
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

    /// <summary>정점 편집 토글 버튼 핸들러. 단일 일반 객체(블록 제외)에서만 동작하며, 켜면 정점/중점 핸들을 표시하고 회전 핸들을 숨긴다.</summary>
    /// <param name="sender">정점 편집 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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
    /// <paramref name="geometry"/> 의 외곽 링 정점들을 열린 월드 좌표 배열(마지막의 닫는 중복점 없음)로 반환한다.
    /// 폴리곤 파트마다 배열 하나.
    /// </summary>
    /// <param name="geometry">폴리곤 또는 멀티폴리곤.</param>
    /// <returns>파트별 정점 배열(마지막=처음 중복점 제거됨).</returns>
    private static Coordinate[][] GetOpenExteriorRings(NetTopologySuite.Geometries.Geometry geometry) =>
        ExtractExteriorRings(geometry)
            .Select(ring => ring.Coordinates.Length > 1 && ring.Coordinates[0].Equals2D(ring.Coordinates[^1])
                ? ring.Coordinates[..^1]
                : ring.Coordinates)
            .ToArray();

    /// <summary>
    /// 편집한 외곽 링 하나를 새 정점 배열로 교체해 외곽 지오메트리를 재구성한다. 닫는 점이 없으면 추가하고,
    /// 멀티폴리곤이면 해당 파트만 교체한다.
    /// </summary>
    /// <param name="outer">기존 외곽 지오메트리(폴리곤/멀티폴리곤).</param>
    /// <param name="ringIndex">교체할 파트(링) 인덱스.</param>
    /// <param name="openRing">새 정점 배열(열린 형태).</param>
    /// <returns>재구성된 외곽 지오메트리.</returns>
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

    /// <summary>
    /// 선택 객체의 정점(흰 사각형)과 변 중점(녹색 원) 핸들을 그린다. 편집 상태가 없으면 먼저 만든다.
    /// 핸들 Tag 에는 (링 인덱스, 정점 인덱스)를 넣어 이벤트에서 어느 정점인지 알아낸다.
    /// </summary>
    /// <param name="id">정점 편집 대상 객체의 편집 id.</param>
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

    /// <summary>정점/중점 핸들을 오버레이에서 모두 제거한다.</summary>
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

    /// <summary>정점 핸들 왼쪽 누름 핸들러. 정점 드래그를 시작하고(EditDragKind.Vertex) 마우스를 캡처한다.</summary>
    /// <param name="sender">정점 사각형 핸들(Tag=(링 인덱스, 정점 인덱스)).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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

    /// <summary>정점 핸들 오른쪽 누름 핸들러. 해당 정점을 삭제한다(폴리곤은 정점이 최소 3개 필요).</summary>
    /// <param name="sender">정점 사각형 핸들(Tag=(링 인덱스, 정점 인덱스)).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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

        PushUndo();
        var newRing = rings[ringIndex].Where((_, index) => index != pointIndex).ToArray();
        edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, newRing);
        edit.HasChanges = true;
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = "Vertex deleted.";
    }

    /// <summary>중점 핸들 왼쪽 누름 핸들러. 해당 변의 중점에 새 정점을 삽입한다.</summary>
    /// <param name="sender">중점 원 핸들(Tag=(링 인덱스, 앞 정점 인덱스)).</param>
    /// <param name="e">마우스 버튼 이벤트 인자.</param>
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

        PushUndo();
        edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, newRing.ToArray());
        edit.HasChanges = true;
        RenderEditableLayerObjects();
        SaveEditsButton.IsEnabled = true;
        EditStatusText.Text = "Vertex added.";
    }

    /// <summary>정점 드래그 중 미리보기: 다른 정점에 스냅한 위치로 해당 정점(첫 점이면 닫는 마지막 점도)을 옮겨 그린다.</summary>
    /// <param name="current">현재 마우스 위치(로컬 좌표).</param>
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
    /// (이 객체뿐 아니라 모든 편집 객체의) 다른 정점 중 가장 가까운 정점에 스냅한다. 거리는
    /// <paramref name="current"/> 로부터 <see cref="VertexSnapTolerance"/> 로컬 캔버스 단위 이내이다.
    /// 허용 거리는 실제 화면 픽셀이 아니라 확대 전 로컬 단위라서, 확대하면 느슨해지고 축소하면 조여진다
    /// (현재 단계에서는 허용되는 근사).
    /// </summary>
    /// <param name="current">현재 마우스 위치(로컬 좌표).</param>
    /// <param name="excludeId">제외할 자기 자신 객체 id.</param>
    /// <param name="excludeRing">제외할 링 인덱스.</param>
    /// <param name="excludePoint">제외할 정점 인덱스(0 이면 닫는 마지막 점도 같은 정점으로 간주).</param>
    /// <returns>스냅된 정점 위치. 후보가 없으면 current 그대로.</returns>
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

    /// <summary>정점 드래그를 확정한다: 스냅한 위치를 월드 좌표로 바꿔 Undo 기록 후 외곽 지오메트리를 재구성한다.</summary>
    /// <param name="edit">대상 객체의 편집 상태.</param>
    /// <param name="current">놓은 위치(로컬 좌표).</param>
    private void FinalizeVertexDrag(PendingLayerEdit edit, System.Windows.Point current)
    {
        if (draggingVertex is not var (ringIndex, pointIndex) || selectedEditId is not long id)
        {
            draggingVertex = null;
            return;
        }

        var snapped = SnapToNearbyVertex(current, id, ringIndex, pointIndex);
        var worldPoint = UnprojectFromScreen(snapped);
        PushUndo();

        var rings = GetOpenExteriorRings(edit.CurrentOuterGeometry);
        if (ringIndex < rings.Length)
        {
            rings[ringIndex][pointIndex] = worldPoint;
            edit.CurrentOuterGeometry = RebuildOuterGeometry(edit.CurrentOuterGeometry, ringIndex, rings[ringIndex]);
            edit.HasChanges = true;
        }

        draggingVertex = null;
    }

    /// <summary>
    /// 복제 버튼 핸들러. 선택된 모든 객체를 화면 10px(월드 환산) 오른쪽 아래로 옮긴 새 객체(음수 임시 id)로 복제하고
    /// 새 객체들을 선택한다. 복제본은 DXF 핸들(block_handle)을 갖지 않는다.
    /// </summary>
    /// <param name="sender">복제 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void DuplicateSelected_Click(object sender, RoutedEventArgs e)
    {
        if (selectedEditIds.Count == 0)
        {
            return;
        }

        // 1) Undo 스냅샷 기록 후, 복제 오프셋(월드 단위)과 이동 변환을 만든다.
        PushUndo();
        var offset = 10.0 / Math.Max(renderedScale, 0.0001);
        var translation = AffineTransformation.TranslationInstance(offset, -offset);
        var newIds = new List<long>();
        foreach (var id in selectedEditIds.ToArray())
        {
            var source = GetEditableSource(id);
            pendingEdits.TryGetValue(id, out var existingEdit);
            var baseGeometry = existingEdit?.CurrentGeometry ?? source.Geometry;
            var baseOuter = existingEdit?.CurrentOuterGeometry ?? source.OuterGeometry;

            // 2) 객체마다 현재 편집 상태(없으면 원본)를 기준으로 임시 id 의 새 편집(IsNew)을 만든다.
            var newId = nextTempId--;
            var clone = CreatePendingEdit(source, isNew: true);
            clone.CurrentGeometry = translation.Transform(baseGeometry);
            clone.CurrentOuterGeometry = translation.Transform(baseOuter);
            if (existingEdit is not null)
            {
                clone.Attributes = new Dictionary<string, string?>(existingEdit.Attributes);
            }

            // 복사본은 DXF 와 무관한 새 인스턴스이므로 원본의 DXF 핸들을 공유하면 안 된다.
            clone.Attributes.Remove("block_handle");
            ApplyPlacementMove(clone, offset, -offset);
            clone.HasChanges = true;
            pendingEdits[newId] = clone;
            newIds.Add(newId);
        }

        // 복사본이 새 선택이 되어, 묶음 전체를 함께 드래그해 제자리에 놓을 수 있다.
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
    /// 선택된 객체들을 하나의 새 블록 인스턴스로 묶는다: 지오메트리는 구성원 지오메트리의 합, 외곽은 구성원
    /// 외곽들의 볼록 껍질(convex hull)이며, 배치 속성은 외곽의 무게중심에서 시작한다. 원본은 삭제 표시되므로
    /// 저장 후 그룹은 하나의 객체로 이동/회전/삭제되고 자체 블록 정의를 갖게 된다.
    /// </summary>
    /// <param name="sender">그룹 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void GroupSelected_Click(object sender, RoutedEventArgs e)
    {
        // 1) 2개 이상 선택해야 묶을 수 있다. 구성원의 현재 지오메트리 부품을 모두 모은다.
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

        // 2) 외곽은 볼록 껍질로 만들되 폴리곤이 아니면(점/선 퇴화) 경계 상자로 대체한다.
        var hullSource = factory.CreateGeometryCollection(edits.Select(member => member.CurrentOuterGeometry).ToArray());
        var hull = hullSource.ConvexHull();
        if (hull is not NtsPolygon)
        {
            hull = hullSource.Envelope;
        }

        // 3) 고유한 블록 이름과 초기 배치 속성(삽입점=외곽 무게중심, 회전 0, 배율 1)을 만든다.
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

        // 4) Undo 기록 후 임시 id 로 새 블록 인스턴스를 편집 목록에 등록한다.
        PushUndo();
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

        // 구성원은 그룹에 흡수된다: 저장 전 복사본은 버리고, 저장된 원본은 저장 시 삭제되도록 표시한다.
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

    /// <summary>지오메트리 컬렉션을 재귀적으로 풀어 단일 도형 부품들만 목록에 모은다.</summary>
    /// <param name="geometry">입력 지오메트리(컬렉션이면 재귀 분해).</param>
    /// <param name="parts">결과 부품이 추가되는 목록.</param>
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

    /// <summary>
    /// 삭제 버튼 핸들러. 삭제 권한을 확인한 뒤 선택 객체를 삭제 표시한다(저장 전 새 객체는 바로 제거,
    /// 저장된 객체는 IsDeleted 로 표시되어 저장 시 DELETE). 실제 삭제는 저장해야 반영된다.
    /// </summary>
    /// <param name="sender">삭제 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

        PushUndo();
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

    /// <summary>저장 버튼 핸들러. 수동 저장을 실행한다.</summary>
    /// <param name="sender">저장 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void SaveEdits_Click(object sender, RoutedEventArgs e) => await SaveEditsAsync(auto: false);

    /// <summary>
    /// 미저장 편집을 저장하고, 같은 레이어/같은 뷰로 편집 모드에 다시 진입해 계속 편집할 수 있게 한다.
    /// <paramref name="auto"/>(자동 저장 타이머)는 조용히 동작하며 대화상자를 띄우는 대신 건너뛴다.
    /// </summary>
    /// <param name="auto">true 면 자동 저장(권한/겹침 문제 시 대화상자 없이 건너뜀).</param>
    private async Task SaveEditsAsync(bool auto)
    {
        // 1) 권한 확인 후, 설비 영역 겹침이 있으면 저장하지 않는다(수동: 오류 안내, 자동: 상태바에 건너뜀 표시).
        if (auto ? !Has(Permissions.DrawingEdit) : !Require(Permissions.DrawingEdit, "save drawing changes"))
        {
            return;
        }

        if (HasEquipmentOverlapBeforeSave(silent: auto))
        {
            if (auto)
            {
                StatusText.Text = "Auto-save skipped: equipment overlaps. Fix the overlap and save manually.";
            }

            return;
        }

        // 2) 편집 상태를 갱신/추가/삭제 목록으로 분류한다: 새 객체 → 추가, 삭제 표시 → 삭제, 변경됨 → 갱신.
        //    블록 인스턴스 id 는 플래그를 해독해 원래 테이블 id 로 되돌리고, 낙관적 동시성 검사를 위해 원본의 UpdatedAt 을 함께 보낸다.
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

        // 3) 저장할 것이 없으면 종료한다.
        if (updates.Count == 0 && inserts.Count == 0 && deletes.Count == 0)
        {
            EditStatusText.Text = "No changes to save.";
            return;
        }

        // 4) DB 에 한 번에 적용한다(변경 출처 CurrentChangeContext 기록). 다른 사용자가 먼저 바꾼 객체(충돌)는 덮어쓰지 않고 건수/ID 를 알린다.
        ShowBusy("Saving edits to PostgreSQL...");
        try
        {
            connectionSettings = ReadConnectionSettingsFromUi();
            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
            var conflictIds = await repository.ApplyLayerEditsAsync(updates, inserts, deletes, changeContext: CurrentChangeContext);
            var appliedUpdates = updates.Count - conflictIds.Count(id => updates.Any(u => u.Id == id));
            var appliedDeletes = deletes.Count - conflictIds.Count(id => deletes.Any(d => d.Id == id));
            var summary = $"Saved edits: {appliedUpdates} updated, {inserts.Count} added, {appliedDeletes} deleted.";
            if (conflictIds.Count > 0)
            {
                summary += $" {conflictIds.Count} object(s) were changed by someone else in the meantime and were NOT overwritten (ids: {string.Join(", ", conflictIds)}) — reload shows their latest state.";
            }

            // 5) 편집 모드를 종료하고 자동 버전을 만든 뒤 감사 로그를 남긴다.
            var editLayer = activeEditLayerName;
            ExitEditMode();
            StatusText.Text = summary;
            await CreateAutoVersionAsync();
            await AuditAsync(auto ? "drawing.autosave" : "drawing.save", "drawing", null,
                $"{appliedUpdates} updated, {inserts.Count} added, {appliedDeletes} deleted, {conflictIds.Count} conflict(s)");
            // 작업 화면(뷰, 편집 모드, 편집 레이어)을 유지하고 저장된 상태의 데이터로 다시 보여 준다.
            await ReloadForSyncAsync();
            await EnterEditModeForLayerAsync(editLayer);

            StatusText.Text = auto ? $"Auto-saved at {DateTime.Now:HH:mm:ss}. {summary}" : summary;
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
    /// 창 전역 키 미리보기 핸들러. 비교 오버레이 표시 중 Esc 는 오버레이를 닫는다. 편집 모드에서는 Ctrl+방향키 미세 이동을
    /// 먼저 처리하고, Esc 는 진행 중인 드래그를 취소하거나 아니면 모든 미저장 변경(이동, 회전, 복사, 그룹, 삭제)을
    /// 버리고 저장된 상태로 되돌린다(편집 모드 유지, 되돌릴 것이 없으면 선택만 해제). 그 외 키는 단축키 처리로 넘긴다.
    /// </summary>
    /// <param name="sender">창.</param>
    /// <param name="e">키 이벤트 인자.</param>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && !isEditMode && HasDiffOverlay)
        {
            ClearDiffOverlay();
            e.Handled = true;
            return;
        }

        if (isEditMode && TryNudgeFromKey(e))
        {
            return;
        }

        if (e.Key != System.Windows.Input.Key.Escape || !isEditMode)
        {
            HandleShortcut(e);
            return;
        }

        e.Handled = true;
        CancelEdits();
    }

    /// <summary>편집 모두 취소 버튼 핸들러. 미저장 편집이 있으면 확인 후 폐기하고 편집 모드를 종료한다.</summary>
    /// <param name="sender">버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

    /// <summary>
    /// 지정한 중심점을 고정한 채 배율을 factor 배로 바꾼다(결과 0.1~20 로 제한). 이동값을 보정해 중심 아래의
    /// 지도 점이 움직이지 않게 하며, 선 두께 갱신은 디바운스 타이머로 미룬다.
    /// </summary>
    /// <param name="factor">배율 변화 비율(1 보다 크면 확대, 작으면 축소).</param>
    /// <param name="center">고정할 기준점(MapCanvas 기준 화면 좌표).</param>
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

    /// <summary>뷰를 초기화(배율 1, 이동 0)한다. 렌더링 시 이미 화면 맞춤으로 투영하므로 이 상태가 "전체 보기"가 된다.</summary>
    private void ResetView()
    {
        mapScale.ScaleX = 1;
        mapScale.ScaleY = 1;
        mapTranslation.X = 0;
        mapTranslation.Y = 0;
        UpdateZoomText();
        UpdateStrokeWidths();
    }

    /// <summary>선 두께 갱신 타이머를 재시작한다(연속 줌 중에는 마지막 변경 후 한 번만 갱신).</summary>
    private void ScheduleStrokeWidthUpdate()
    {
        strokeWidthUpdateTimer.Stop();
        strokeWidthUpdateTimer.Start();
    }

    /// <summary>지도 캔버스에 도형을 추가하고 줌에 맞춘 선 두께를 기록/적용한다.</summary>
    /// <param name="shape">추가할 도형.</param>
    /// <param name="strokeWidth">기준 선 두께(화면 픽셀 기준).</param>
    private void AddMapShape(Shape shape, double strokeWidth)
    {
        baseStrokeWidths[shape] = strokeWidth;
        shape.StrokeThickness = strokeWidth / Math.Max(mapScale.ScaleX, 0.1);
        MapLayerCanvas.Children.Add(shape);
    }

    /// <summary>등록된 모든 도형의 선 두께를 기준 두께 / 현재 배율로 갱신해 확대/축소해도 화면상 두께를 일정하게 한다.</summary>
    private void UpdateStrokeWidths()
    {
        var scale = Math.Max(mapScale.ScaleX, 0.1);
        foreach (var (shape, baseWidth) in baseStrokeWidths)
        {
            shape.StrokeThickness = baseWidth / scale;
        }
    }

    /// <summary>지도 캔버스 중앙 좌표를 반환한다.</summary>
    /// <returns>MapCanvas 기준 중심점.</returns>
    private System.Windows.Point GetCanvasCenter() => new(MapCanvas.ActualWidth / 2, MapCanvas.ActualHeight / 2);

    /// <summary>샘플 점 추가 메뉴 핸들러(지오메트리 서비스로 점을 만들어 표시하는 데모 기능).</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void AddPoint_Click(object sender, RoutedEventArgs e)
    {
        var point = geometryService.CreatePoint(210, 160);
        var marker = new Ellipse { Width = 10, Height = 10, Fill = Brushes.OrangeRed };
        Canvas.SetLeft(marker, point.X - 5);
        Canvas.SetTop(marker, point.Y - 5);
            AddMapShape(marker, 1);
        StatusText.Text = $"Point created: ({point.X:0}, {point.Y:0})";
    }

    /// <summary>선택된 피처의 지오메트리 유효성(IsValid)을 상태바에 표시한다.</summary>
    /// <param name="sender">클릭된 항목.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = selectedFeature is null
            ? "No object selected. Click a shape on the map first."
            : $"Feature #{selectedFeature.Id} ({selectedFeature.Geometry.GeometryType}) valid: {selectedFeature.Geometry.IsValid}";
    }

    /// <summary>
    /// DXF 가져오기 버튼 핸들러. 고정 경로의 SmartLayout.dxf 를 읽어(파싱은 백그라운드 스레드) PostGIS 에 적재한다.
    /// 이미 가져온 데이터가 있으면 삭제 후 다시 가져오는지 확인하며, 진행률/취소를 지원한다(취소 시 트랜잭션 롤백).
    /// </summary>
    /// <param name="sender">가져오기 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private async void ImportDxf_Click(object sender, RoutedEventArgs e)
    {
        // 1) 가져오기 권한 확인, 취소 토큰/버튼 상태 준비 후 작업 중 오버레이를 표시한다.
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
            // 2) 연결 설정(비밀번호) 확인 후 DXF 를 파싱한다. 단일 레이어 옵션이면 Equipment INSERT 만 블록 객체로,
            //    아니면 레이어 집계/블록 인스턴스/설비 경계 상자를 모두 읽는다(UI 가 멈추지 않도록 Task.Run).
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
                // Equipment INSERT 만 블록 객체로 가져온다: 레이어 집계도, 다른 것도 없음.
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

            // 3) 스키마 보장. 같은 파일이 이미 있으면 사용자 확인 후 기존 데이터를 삭제한다.
            var allLayerRows = blocks.Concat(equipmentBoundingBoxes).ToArray();
            parseStopwatch.Stop();

            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
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

            // 4) 진행률 막대 설정 후 DB 에 쓴다(변경 출처 기록).
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

            // 5) DB 건수와 DXF 건수를 비교해 로그에 남기고, 전체 레이어를 다시 로드해 결과를 보여 준 뒤 감사 로그를 남긴다.
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
        // 취소/권한 부족/PostgreSQL 오류/기타 예외를 구분해 상태바에 안내한다.
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
        // 6) 정리: 오버레이/진행률/버튼 상태를 복원하고 취소 토큰을 해제한다.
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

    /// <summary>가져오기 취소 버튼 핸들러. 취소 토큰을 발동한다(진행 중인 가져오기는 롤백됨).</summary>
    /// <param name="sender">취소 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
    private void CancelImport_Click(object sender, RoutedEventArgs e)
    {
        importCts?.Cancel();
        CancelImportButton.IsEnabled = false;
        StatusText.Text = "Cancelling import...";
    }

    /// <summary>
    /// 가져온 DXF 데이터 삭제 핸들러. 확인을 받은 뒤 해당 파일의 모든 레이어/블록 객체를 DB 에서 지우고
    /// 화면과 레이어 패널을 비운다. 되돌릴 수 없다.
    /// </summary>
    /// <param name="sender">삭제 버튼.</param>
    /// <param name="e">라우팅 이벤트 인자.</param>
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

            await using var repository = new PostGisFeatureRepository(connectionSettings.ConnectionString, activeLayoutId);
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

    /// <summary>
    /// 가져오기 결과를 감사 로그에 기록한다: DXF 레이어/블록 수, 레이어별 DXF 건수와 DB 건수의 일치 여부,
    /// 지원되지 않는 엔티티 경고, 외곽 지오메트리 대체(fallback) 경고 등. 데이터 손실 여부를 확인하기 위한 것이다.
    /// </summary>
    /// <param name="dxfLayers">DXF 에서 읽은 레이어 결과.</param>
    /// <param name="dxfInstances">DXF 에서 읽은 블록 인스턴스 결과.</param>
    /// <param name="databaseCounts">DB 에 저장된 레이어별 건수.</param>
    /// <param name="sourceFile">가져온 DXF 파일 경로.</param>
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

    /// <summary>연결 정보는 서버 창에서 편집되므로, 다른 흐름은 현재 설정을 그대로 사용한다.</summary>
    /// <returns>현재 연결 설정.</returns>
    private PostgresConnectionSettings ReadConnectionSettingsFromUi() => connectionSettings;
}
