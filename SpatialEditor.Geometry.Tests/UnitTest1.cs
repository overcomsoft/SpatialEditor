using NetTopologySuite.Geometries;
using SpatialEditor.Geometry;

// ============================================================================
// SpatialEditor.Geometry.Tests / UnitTest1 : 기하 서비스(GeometryService)와 폴리라인 폴리곤화(PolylinePolygonizer) 테스트
// ----------------------------------------------------------------------------
// [실행 조건] 데이터베이스/환경 변수/외부 파일 모두 불필요(메모리 상의 좌표만 사용).
// [테스트 흐름]
//   1) PolygonHasConfiguredSridAndIsValid
//        GeometryService.CreatePolygon 으로 (0,0)-(10,0)-(10,10)-(0,10) 닫힌 링 폴리곤을 만들고
//        → SRID 가 설정값 5186 인지, 도형이 유효(IsValid)한지 확인
//   2) PolylinePolygonizerCreatesOnePolygonFromSegmentedRing
//        정사각형 둘레를 4개의 개별 선분(CreateLine)으로 쪼개 입력
//        → PolylinePolygonizer.CreateOuterGeometry 가 하나의 "Polygon" 으로 합치고
//          면적이 100, 도형이 유효한지 확인
// ============================================================================
namespace SpatialEditor.Geometry.Tests;

/// <summary>GeometryService 의 SRID/유효성 설정과 PolylinePolygonizer 의 선분 → 폴리곤 변환을 검증하는 테스트 모음.</summary>
public class UnitTest1
{
    /// <summary>GeometryService 가 만든 폴리곤이 설정된 SRID(5186)를 갖고 유효한 도형인지 검증한다.</summary>
    [Fact]
    public void PolygonHasConfiguredSridAndIsValid()
    {
        // 1) 10x10 정사각형 닫힌 링으로 폴리곤을 만든다.
        var service = new GeometryService();
        var polygon = service.CreatePolygon(new[]
        {
            new Coordinate(0, 0), new Coordinate(10, 0),
            new Coordinate(10, 10), new Coordinate(0, 10),
            new Coordinate(0, 0)
        });

        // 2) 좌표계(SRID)와 유효성을 확인한다.
        Assert.Equal(5186, polygon.SRID);
        Assert.True(polygon.IsValid);
    }

    /// <summary>정사각형 둘레를 이루는 4개의 분리된 선분이 하나의 폴리곤(면적 100)으로 합쳐지는지 검증한다.</summary>
    [Fact]
    public void PolylinePolygonizerCreatesOnePolygonFromSegmentedRing()
    {
        // 1) 정사각형 둘레를 4개의 개별 선분으로 준비한다(끝점이 서로 맞닿는 링).
        var service = new GeometryService();
        var polygonizer = new PolylinePolygonizer();
        var lines = new[]
        {
            service.CreateLine(new[] { new Coordinate(0, 0), new Coordinate(10, 0) }),
            service.CreateLine(new[] { new Coordinate(10, 0), new Coordinate(10, 10) }),
            service.CreateLine(new[] { new Coordinate(10, 10), new Coordinate(0, 10) }),
            service.CreateLine(new[] { new Coordinate(0, 10), new Coordinate(0, 0) })
        };

        // 2) 선분들로 외곽 도형을 만들고, 단일 Polygon / 면적 100 / 유효성을 확인한다.
        var result = polygonizer.CreateOuterGeometry(lines);

        Assert.Equal("Polygon", result.GeometryType);
        Assert.Equal(100, result.Area);
        Assert.True(result.IsValid);
    }
}