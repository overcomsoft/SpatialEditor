// ============================================================================
// GeometryService.cs : NetTopologySuite(NTS) 지오메트리 생성 도우미
//
//   생성자에서 SRID(좌표계 id)를 가진 GeometryFactory 를 하나 만들고,
//   CreatePoint / CreateLine / CreatePolygon 이 입력 좌표를 검증한 뒤 그 팩토리로 도형을 만든다.
//   기본 SRID 는 5186(한국 중부원점 TM 좌표계, EPSG:5186).
//
//   검증 규칙:  선 = 좌표 2개 이상 / 폴리곤 = 닫힌 링(첫점==끝점) + 좌표 4개 이상 + IsValid
//   위반 시 ArgumentException 을 던진다.
//   같은 프로젝트의 PolylinePolygonizer 가 DefaultSrid 상수를 공유한다.
// ============================================================================
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace SpatialEditor.Geometry;

/// <summary>지정한 SRID 로 점/선/폴리곤 지오메트리를 만들어 주는 서비스입니다.</summary>
public sealed class GeometryService
{
    /// <summary>기본 좌표계 SRID(EPSG:5186, 한국 중부원점 TM). 생성자 인수를 생략하면 사용됩니다.</summary>
    public const int DefaultSrid = 5186;
    /// <summary>SRID 가 설정된 NTS 지오메트리 팩토리. 만들어지는 모든 도형이 이 SRID 를 가집니다.</summary>
    private readonly GeometryFactory factory;

    /// <summary>서비스를 만들고 해당 SRID 의 지오메트리 팩토리를 준비합니다.</summary>
    /// <param name="srid">생성되는 모든 도형에 부여할 좌표계 id. 생략하면 <see cref="DefaultSrid"/>(5186).</param>
    public GeometryService(int srid = DefaultSrid)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
    }

    /// <summary>점을 만듭니다.</summary>
    /// <param name="x">X 좌표(좌표계 단위, 보통 미터).</param>
    /// <param name="y">Y 좌표(좌표계 단위, 보통 미터).</param>
    /// <returns>SRID 가 설정된 점.</returns>
    public Point CreatePoint(double x, double y) => factory.CreatePoint(new Coordinate(x, y));

    /// <summary>좌표 목록으로 선(LineString)을 만듭니다.</summary>
    /// <param name="coordinates">선을 이루는 꼭짓점 순서 목록. 2개 이상이어야 하며 null 이면 안 됩니다.</param>
    /// <returns>SRID 가 설정된 선.</returns>
    /// <exception cref="ArgumentException">좌표가 2개 미만일 때.</exception>
    public LineString CreateLine(IEnumerable<Coordinate> coordinates)
    {
        // 1) 열거를 한 번만 수행하도록 배열로 고정합니다(검증과 생성에서 같은 값을 쓰기 위함).
        var values = coordinates.ToArray();
        if (values.Length < 2)
        {
            throw new ArgumentException("A line requires at least two coordinates.", nameof(coordinates));
        }

        // 2) 검증을 통과했으므로 팩토리로 선을 만듭니다.
        return factory.CreateLineString(values);
    }

    /// <summary>좌표 목록(닫힌 링)으로 폴리곤을 만듭니다. 구멍(내부 링)은 지원하지 않습니다.</summary>
    /// <param name="coordinates">외곽 링 좌표. 첫 점과 마지막 점이 같아야(닫힘) 하고 4개 이상이어야 합니다.</param>
    /// <returns>SRID 가 설정된 유효한 폴리곤.</returns>
    /// <exception cref="ArgumentException">링이 닫혀 있지 않거나 좌표가 4개 미만이거나, 자기 교차 등으로 유효하지 않은 폴리곤일 때.</exception>
    public Polygon CreatePolygon(IEnumerable<Coordinate> coordinates)
    {
        var values = coordinates.ToArray();
        // 1) 폴리곤 링은 삼각형(3점)+닫는 점 = 최소 4개이고, 첫 점과 끝 점이 같아야 합니다(2D 비교).
        if (values.Length < 4 || !values[0].Equals2D(values[^1]))
        {
            throw new ArgumentException("A polygon requires a closed ring with at least four coordinates.", nameof(coordinates));
        }

        // 2) 링을 만들어 폴리곤을 구성합니다.
        var polygon = factory.CreatePolygon(factory.CreateLinearRing(values));
        // 3) 자기 교차 같은 위상 오류가 있으면 이후 공간 연산이 깨지므로 여기서 거부합니다.
        if (!polygon.IsValid)
        {
            throw new ArgumentException("The polygon is not valid.", nameof(coordinates));
        }

        return polygon;
    }
}