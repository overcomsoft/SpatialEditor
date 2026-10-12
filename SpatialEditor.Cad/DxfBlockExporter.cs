using System.Globalization;
using netDxf;
using netDxf.Blocks;
using netDxf.Entities;
using netDxf.Header;
using netDxf.Tables;
using NetTopologySuite.Geometries;
using DxfAttribute = netDxf.Entities.Attribute;

// ============================================================================
// DxfBlockExporter : 편집된 장비 배치를 DXF 파일로 내보내기
// ----------------------------------------------------------------------------
// 흐름도
//   Export(filePath, instances)
//     → 인스턴스마다 빈/0 스케일 건너뜀
//     → 블록 이름(정리됨)별로 BLOCK 정의 1회 생성 (CreateBlock)
//          · 속성 정의(NAME/ROTATION/POS_X/POS_Y) 추가
//          · 월드 지오메트리를 INSERT 역변환으로 블록 로컬 좌표로 변환(ToLocal)
//          · AddGeometry → Polygon/LineString → Polyline2D, Point → Point 엔티티
//     → 레이어(정리됨) 생성/재사용 → INSERT 생성(위치·회전·스케일) → ATTRIB 값 채움(SetAttribute)
//     → DxfDocument.Save → 기록한 INSERT 수 반환
// ============================================================================
namespace SpatialEditor.Cad;

/// <summary>
/// 내보낼 장비 배치 1건: 참조할 블록, 위치, 회전/스케일, 월드 좌표 지오메트리(블록 정의 유도용).
/// </summary>
/// <param name="BlockName">블록 정의 이름(내보낼 때 DXF 금지 문자는 '_' 로 치환). null 불가.</param>
/// <param name="Layer">INSERT 레이어 이름. null/공백이면 "Equipment" 사용.</param>
/// <param name="InsertX">삽입점 X(월드 좌표, 도면 단위).</param>
/// <param name="InsertY">삽입점 Y(월드 좌표, 도면 단위).</param>
/// <param name="RotationDegrees">회전각(도, 반시계 방향 +).</param>
/// <param name="ScaleX">X 축 스케일. 0 에 가까우면 내보내기에서 제외됨.</param>
/// <param name="ScaleY">Y 축 스케일. 0 에 가까우면 내보내기에서 제외됨.</param>
/// <param name="WorldGeometry">월드 좌표 지오메트리. 비어 있으면 제외됨.</param>
public sealed record CadExportInstance(
    string BlockName,
    string? Layer,
    double InsertX,
    double InsertY,
    double RotationDegrees,
    double ScaleX,
    double ScaleY,
    Geometry WorldGeometry);

/// <summary>
/// 편집된 장비를 DXF 로 기록합니다: 블록 이름별 BLOCK 정의 1개(블록 로컬 좌표)와 인스턴스별 INSERT 1개.
/// 각 INSERT 는 고유 삽입점/회전 외에 장비 이름·회전·위치를 블록 속성(ATTRIB)으로도 담아,
/// 다른 CAD 도구에서 일반 속성 데이터로 읽을 수 있게 합니다.
/// </summary>
public sealed class DxfBlockExporter
{
    /// <summary>장비 이름 ATTRIB 태그("NAME").</summary>
    public const string NameTag = "NAME";
    /// <summary>회전각(도) ATTRIB 태그("ROTATION").</summary>
    public const string RotationTag = "ROTATION";
    /// <summary>위치 X ATTRIB 태그("POS_X").</summary>
    public const string PositionXTag = "POS_X";
    /// <summary>위치 Y ATTRIB 태그("POS_Y").</summary>
    public const string PositionYTag = "POS_Y";
    /// <summary>레이어가 지정되지 않은 인스턴스가 들어갈 기본 레이어 이름.</summary>
    private const string DefaultLayerName = "Equipment";
    /// <summary>숫자를 문화권과 무관하게(소수점 '.') 문자열화하기 위한 InvariantCulture.</summary>
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>인스턴스들을 <paramref name="filePath"/> 에 DXF(AutoCAD 2010)로 저장하고, 기록한 INSERT 수를 반환합니다.</summary>
    /// <param name="filePath">저장할 DXF 파일 경로(덮어씀). null 불가.</param>
    /// <param name="instances">내보낼 장비 배치 목록. 빈 지오메트리/0 스케일 항목은 건너뜁니다.</param>
    /// <returns>실제로 기록된 INSERT 개수(건너뛴 항목 제외).</returns>
    public int Export(string filePath, IReadOnlyList<CadExportInstance> instances)
    {
        // 1) 빈 DXF 문서와 블록/레이어 캐시(이름 대소문자 무시)를 준비합니다. 같은 이름 블록·레이어는 한 번만 만듭니다.
        var document = new DxfDocument(DxfVersion.AutoCad2010);
        var blocks = new Dictionary<string, Block>(StringComparer.OrdinalIgnoreCase);
        var layers = new Dictionary<string, Layer>(StringComparer.OrdinalIgnoreCase);
        var written = 0;

        // 2) 인스턴스를 순회하며 블록 정의 → 레이어 → INSERT 순으로 문서에 추가합니다.
        foreach (var instance in instances)
        {
            // 지오메트리가 없거나 스케일이 0 이면 역변환(나눗셈)이 불가능하므로 내보내지 않습니다.
            if (instance.WorldGeometry.IsEmpty || Math.Abs(instance.ScaleX) < 1e-12 || Math.Abs(instance.ScaleY) < 1e-12)
            {
                continue;
            }

            // 블록 정의는 이름별 첫 인스턴스의 월드 지오메트리를 로컬 좌표로 되돌려 한 번만 만듭니다.
            var blockName = SanitizeName(instance.BlockName);
            if (!blocks.TryGetValue(blockName, out var block))
            {
                block = CreateBlock(blockName, instance);
                blocks.Add(blockName, block);
            }

            // 레이어 이름도 DXF 금지 문자를 정리한 뒤, 없으면 만들고 있으면 재사용합니다.
            var layerName = SanitizeName(string.IsNullOrWhiteSpace(instance.Layer) ? DefaultLayerName : instance.Layer);
            if (!layers.TryGetValue(layerName, out var layer))
            {
                layer = new Layer(layerName);
                layers.Add(layerName, layer);
            }

            // INSERT 의 고유 삽입점/회전/스케일을 설정합니다(실제 배치 정보).
            var insert = new Insert(block, new Vector3(instance.InsertX, instance.InsertY, 0))
            {
                Layer = layer,
                Rotation = instance.RotationDegrees,
                Scale = new Vector3(instance.ScaleX, instance.ScaleY, 1)
            };
            // 같은 정보를 ATTRIB 으로도 기록해, 속성만 읽는 외부 도구가 값을 얻을 수 있게 합니다.
            SetAttribute(insert, NameTag, instance.BlockName);
            SetAttribute(insert, RotationTag, instance.RotationDegrees.ToString("0.######", Invariant));
            SetAttribute(insert, PositionXTag, instance.InsertX.ToString("0.######", Invariant));
            SetAttribute(insert, PositionYTag, instance.InsertY.ToString("0.######", Invariant));
            document.Entities.Add(insert);
            written++;
        }

        // 3) 모든 INSERT 추가 후 파일로 저장합니다.
        document.Save(filePath);
        return written;
    }

    /// <summary>첫 인스턴스의 월드 지오메트리로 BLOCK 정의(속성 정의 포함, 로컬 좌표)를 만듭니다.</summary>
    /// <param name="blockName">정리된 블록 이름.</param>
    /// <param name="source">블록 형태를 유도할 기준 인스턴스(삽입점/회전/스케일로 로컬 좌표 역변환).</param>
    /// <returns>엔티티와 숨김 속성 정의가 채워진 Block.</returns>
    private static Block CreateBlock(string blockName, CadExportInstance source)
    {
        // 1) 숨김(Hidden) ATTRIB 정의 4개를 만들어, INSERT 가 값을 보관하도록 합니다.
        var block = new Block(blockName);
        foreach (var tag in new[] { NameTag, RotationTag, PositionXTag, PositionYTag })
        {
            block.AttributeDefinitions.Add(new AttributeDefinition(tag)
            {
                Prompt = tag,
                Value = string.Empty,
                Height = 1,
                Flags = AttributeFlags.Hidden
            });
        }

        // 2) 월드 좌표를 블록 로컬로 변환: INSERT 변환(스케일→회전→이동)의 역순으로 이동, 회전, 스케일을 되돌립니다.
        var radians = source.RotationDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        Coordinate ToLocal(Coordinate world)
        {
            var dx = world.X - source.InsertX;
            var dy = world.Y - source.InsertY;
            return new Coordinate((dx * cos + dy * sin) / source.ScaleX, (-dx * sin + dy * cos) / source.ScaleY);
        }

        // 3) 지오메트리를 로컬 좌표로 변환하며 블록 엔티티로 추가합니다.
        AddGeometry(block, source.WorldGeometry, ToLocal);
        return block;
    }

    /// <summary>지오메트리를 종류별로 DXF 엔티티(폴리라인/점)로 변환해 블록에 추가합니다(컬렉션은 재귀).</summary>
    /// <param name="block">엔티티를 추가할 블록.</param>
    /// <param name="geometry">추가할 NTS 지오메트리(월드 좌표).</param>
    /// <param name="toLocal">월드 좌표 → 블록 로컬 좌표 변환 함수.</param>
    private static void AddGeometry(Block block, Geometry geometry, Func<Coordinate, Coordinate> toLocal)
    {
        switch (geometry)
        {
            case GeometryCollection collection:
                foreach (var part in collection.Geometries)
                {
                    AddGeometry(block, part, toLocal);
                }
                break;
            case Polygon polygon:
                AddPolyline(block, polygon.ExteriorRing.Coordinates, true, toLocal);
                for (var i = 0; i < polygon.NumInteriorRings; i++)
                {
                    AddPolyline(block, polygon.GetInteriorRingN(i).Coordinates, true, toLocal);
                }
                break;
            case LineString line:
                AddPolyline(block, line.Coordinates, line.IsClosed && line.NumPoints >= 4, toLocal);
                break;
            case NetTopologySuite.Geometries.Point point when !point.IsEmpty:
                var local = toLocal(point.Coordinate);
                block.Entities.Add(new netDxf.Entities.Point(local.X, local.Y, 0));
                break;
        }
    }

    /// <summary>좌표열을 Polyline2D 로 만들어 블록에 추가합니다. 닫힌 링은 끝점 중복을 제거합니다.</summary>
    /// <param name="block">엔티티를 추가할 블록.</param>
    /// <param name="coordinates">월드 좌표열(2개 미만이면 무시).</param>
    /// <param name="closed">닫힌 폴리라인 여부(폴리곤 링 등).</param>
    /// <param name="toLocal">월드 → 로컬 좌표 변환 함수.</param>
    private static void AddPolyline(Block block, Coordinate[] coordinates, bool closed, Func<Coordinate, Coordinate> toLocal)
    {
        // 닫힌 링은 마지막 점이 첫 점과 같으므로 DXF 의 closed 플래그를 쓰기 위해 중복 끝점을 뺍니다.
        var count = closed && coordinates.Length > 1 && coordinates[0].Equals2D(coordinates[^1])
            ? coordinates.Length - 1
            : coordinates.Length;
        if (count < 2)
        {
            return;
        }

        var vertexes = new List<Polyline2DVertex>(count);
        for (var i = 0; i < count; i++)
        {
            var local = toLocal(coordinates[i]);
            vertexes.Add(new Polyline2DVertex(local.X, local.Y));
        }

        block.Entities.Add(new Polyline2D(vertexes, closed));
    }

    /// <summary>INSERT 에서 태그가 일치하는(대소문자 무시) ATTRIB 을 찾아 값을 설정합니다. 없으면 무시.</summary>
    /// <param name="insert">대상 INSERT.</param>
    /// <param name="tag">ATTRIB 태그.</param>
    /// <param name="value">설정할 문자열 값.</param>
    private static void SetAttribute(Insert insert, string tag, string value)
    {
        foreach (DxfAttribute attribute in insert.Attributes)
        {
            if (string.Equals(attribute.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                attribute.Value = value;
                return;
            }
        }
    }

    /// <summary>DXF 테이블 이름에 쓸 수 없는 문자(&lt; &gt; / 백슬래시 " : ; ? * | = ` ,)와 제어문자를 '_' 로 치환합니다. 빈 이름은 "BLOCK" 으로 대체.</summary>
    /// <param name="name">원래 이름(null 가능).</param>
    /// <returns>DXF 에서 안전한 이름(비어 있지 않음).</returns>
    private static string SanitizeName(string? name)
    {
        var invalid = "<>/\\\":;?*|=`,".ToCharArray();
        var cleaned = new string((name ?? string.Empty).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim();
        return cleaned.Length == 0 ? "BLOCK" : cleaned;
    }
}
