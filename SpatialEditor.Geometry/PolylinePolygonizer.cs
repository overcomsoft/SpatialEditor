// ============================================================================
// PolylinePolygonizer.cs : 선(폴리라인) 묶음에서 닫힌 영역(폴리곤)과 외곽 도형을 만든다
//
//   입력 폴리라인들
//     -> (빈 선/점 1개 이하 제외)
//     -> MultiLineString 으로 묶어 NTS Polygonizer 에 투입
//     -> 닫힌 영역 폴리곤 목록(CreatePolygons, 유효하고 비어 있지 않은 것만)
//     -> CreateOuterGeometry:  0개=빈 컬렉션 / 1개=그 폴리곤 / 여러 개=MultiPolygon.Union() 으로 합친 외곽
//
//   도형 입력 오버로드는 ExtractLineStrings 로 선/폴리곤(외곽+구멍 링)/컬렉션을
//   선 목록으로 풀어낸 뒤 같은 경로를 탄다.
//   용도: DXF 블록의 선 도형들로부터 설비의 외곽선(OuterGeometry)을 만드는 데 쓰인다.
// ============================================================================
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace SpatialEditor.Geometry;

/// <summary>폴리라인들로부터 닫힌 폴리곤과 합쳐진 외곽 지오메트리를 만드는 도우미입니다.</summary>
public sealed class PolylinePolygonizer
{
    /// <summary>SRID 가 설정된 NTS 지오메트리 팩토리. 생성되는 도형이 이 SRID 를 가집니다.</summary>
    private readonly GeometryFactory factory;

    /// <summary>지정한 SRID 의 팩토리로 폴리고나이저를 준비합니다.</summary>
    /// <param name="srid">생성 도형에 부여할 좌표계 id. 생략하면 <see cref="GeometryService.DefaultSrid"/>.</param>
    public PolylinePolygonizer(int srid = GeometryService.DefaultSrid)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
    }

    /// <summary>선들이 둘러싸는 닫힌 영역을 모두 폴리곤으로 만듭니다.</summary>
    /// <param name="polylines">입력 선 목록. null 이면 안 되며 빈 선은 무시됩니다.</param>
    /// <returns>유효하고 비어 있지 않은 폴리곤 목록. 만들 수 있는 영역이 없으면 빈 목록.</returns>
    public IReadOnlyList<Polygon> CreatePolygons(IEnumerable<LineString> polylines)
    {
        // 1) 빈 선이나 점이 1개뿐인 선은 면을 이룰 수 없으므로 제외합니다.
        var lines = polylines
            .Where(line => !line.IsEmpty && line.NumPoints >= 2)
            .ToArray();
        // 2) 쓸 수 있는 선이 없으면 폴리고나이저를 돌리지 않고 즉시 빈 결과를 반환합니다.
        if (lines.Length == 0)
        {
            return Array.Empty<Polygon>();
        }

        // 3) 선들을 한 덩어리(MultiLineString)로 묶어 폴리고나이저에 넣고, 닫힌 영역을 추출합니다.
        var polygonizer = new Polygonizer();
        polygonizer.Add(factory.CreateMultiLineString(lines));
        return polygonizer.GetPolygons()
            .Cast<Polygon>()
            .Where(polygon => polygon.IsValid && !polygon.IsEmpty)
            .ToArray();
    }

    /// <summary>선들로부터 만든 영역을 하나의 외곽 지오메트리로 합쳐 반환합니다.</summary>
    /// <param name="polylines">입력 선 목록. null 이면 안 됩니다.</param>
    /// <returns>
    /// 영역이 없으면 빈 GeometryCollection, 하나면 그 폴리곤, 여러 개면 합집합(Union)한 도형.
    /// </returns>
    public NtsGeometry CreateOuterGeometry(IEnumerable<LineString> polylines)
    {
        // 영역 개수에 따라 결과 형태를 달리합니다(여러 개는 겹침/인접을 합쳐 하나의 외곽으로).
        var polygons = CreatePolygons(polylines);
        return polygons.Count switch
        {
            0 => factory.CreateGeometryCollection(),
            1 => polygons[0],
            _ => factory.CreateMultiPolygon(polygons.ToArray()).Union()
        };
    }

    /// <summary>임의의 지오메트리들에서 선분을 추출해 외곽 지오메트리를 만듭니다.</summary>
    /// <param name="geometries">선/폴리곤/컬렉션 등을 포함한 입력 도형 목록. 점 등은 무시됩니다.</param>
    /// <returns><see cref="CreateOuterGeometry(IEnumerable{LineString})"/> 와 같은 규칙의 외곽 도형.</returns>
    public NtsGeometry CreateOuterGeometry(IEnumerable<NtsGeometry> geometries)
    {
        var lines = geometries.SelectMany(ExtractLineStrings).ToArray();
        return CreateOuterGeometry(lines);
    }

    /// <summary>
    /// 지오메트리를 선(LineString) 목록으로 풀어냅니다. 폴리곤은 외곽 링과 모든 구멍 링을 각각 선으로,
    /// 컬렉션은 재귀적으로 펼칩니다. 그 외 종류(점 등)는 결과에 포함되지 않습니다.
    /// </summary>
    /// <param name="geometry">풀어낼 도형.</param>
    /// <returns>추출된 선들의 지연 열거.</returns>
    private static IEnumerable<LineString> ExtractLineStrings(NtsGeometry geometry)
    {
        switch (geometry)
        {
            case LineString line:
                yield return line;
                break;
            case Polygon polygon:
                yield return polygon.ExteriorRing;
                for (var index = 0; index < polygon.NumInteriorRings; index++)
                {
                    yield return polygon.GetInteriorRingN(index);
                }
                break;
            case GeometryCollection collection:
                foreach (var child in collection.Geometries)
                {
                    foreach (var line in ExtractLineStrings(child))
                    {
                        yield return line;
                    }
                }
                break;
        }
    }
}