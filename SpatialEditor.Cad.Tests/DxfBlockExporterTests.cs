using NetTopologySuite.Geometries;
using netDxf;
using netDxf.Entities;

// ============================================================================
// DxfBlockExporterTests : DXF 블록 내보내기(DxfBlockExporter) 단위 테스트
// ----------------------------------------------------------------------------
// [실행 조건] 데이터베이스/환경 변수 불필요. 임시 폴더에 .dxf 파일을 만들었다가 finally 에서 삭제한다.
// [테스트 흐름]
//   ExportWritesBlocksWithNameRotationAndPositionAttributes
//     월드 좌표 사각형(가로 4 x 세로 10, 삽입점 (100,200), 90도 회전) 하나를 CadExportInstance 로 만들어
//     → DxfBlockExporter.Export 로 임시 DXF 파일에 기록 → 반환 개수 1 확인
//     → netDxf 의 DxfDocument.Load 로 다시 읽어 INSERT 1개의 블록 이름/회전/위치와
//       속성(NAME, ROTATION, POS_X, POS_Y)이 기대값인지 확인
//     → DxfBlockImporter.ImportEquipmentBlockObjects 로 재가져오기 하여 원래 월드 도형의 범위(x 96..100, y 200..210)가
//       복원되는지(내보내기-가져오기 왕복 일관성) 확인
// ============================================================================
namespace SpatialEditor.Cad.Tests;

/// <summary>설비 블록을 DXF 로 내보낸 결과(블록 이름, 회전, 위치, 속성)와 재가져오기 왕복을 검증하는 테스트 모음.</summary>
public class DxfBlockExporterTests
{
    /// <summary>
    /// 설비 하나를 내보낸 DXF 가 블록 이름, 회전 90도, 삽입 위치 (100,200), 속성(NAME/ROTATION/POS_X/POS_Y)을 올바르게 담고,
    /// 다시 가져오면 원래 월드 도형 범위가 복원되는지 검증한다. 임시 파일을 사용하며 DB 는 필요 없다.
    /// </summary>
    [Fact]
    public void ExportWritesBlocksWithNameRotationAndPositionAttributes()
    {
        // 1) 준비: 로컬 10x4 사각형을 (100,200)에 90도(반시계) 회전해 배치하면 월드 사각형은 x 96..100, y 200..210 이 된다.
        var factory = new GeometryFactory();
        var world = factory.CreatePolygon(new[]
        {
            new Coordinate(100, 200), new Coordinate(100, 210), new Coordinate(96, 210),
            new Coordinate(96, 200), new Coordinate(100, 200)
        });
        // 2) 임시 DXF 파일 경로(충돌 방지를 위해 GUID 사용). 테스트가 끝나면 finally 에서 삭제한다.
        var path = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.dxf");
        try
        {
            // 3) 실행: 인스턴스 하나를 내보낸다.
            var count = new DxfBlockExporter().Export(path, new[]
            {
                new CadExportInstance("EQ-A", "Equipment", 100, 200, 90, 1, 1, world)
            });
            Assert.Equal(1, count);

            // 4) 검증: 파일을 다시 읽어 INSERT 하나의 블록 이름/회전/삽입 위치를 확인한다(소수 6자리 허용오차).
            var doc = DxfDocument.Load(path);
            var insert = Assert.Single(doc.Entities.Inserts);
            Assert.Equal("EQ-A", insert.Block.Name);
            Assert.Equal(90, insert.Rotation, 6);
            Assert.Equal(100, insert.Position.X, 6);
            Assert.Equal(200, insert.Position.Y, 6);
            // 5) 속성 값 확인용 로컬 함수: INSERT 의 속성 중 태그가 일치하는 값을 반환.
            string Value(string tag) => insert.Attributes.Single(a => a.Tag == tag).Value;
            Assert.Equal("EQ-A", Value("NAME"));
            Assert.Equal("90", Value("ROTATION"));
            Assert.Equal("100", Value("POS_X"));
            Assert.Equal("200", Value("POS_Y"));

            // 6) 왕복 검증: 다시 가져오면 원래 월드 도형의 범위가 복원되어야 한다(소수 4자리 허용오차).
            var back = new DxfBlockImporter().ImportEquipmentBlockObjects(path);
            var env = Assert.Single(back).ActualGeometry.EnvelopeInternal;
            Assert.Equal(96, env.MinX, 4);
            Assert.Equal(100, env.MaxX, 4);
            Assert.Equal(200, env.MinY, 4);
            Assert.Equal(210, env.MaxY, 4);
        }
        // 임시 파일 정리.
        finally
        {
            File.Delete(path);
        }
    }
}
