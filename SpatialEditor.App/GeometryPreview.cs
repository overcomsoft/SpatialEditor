using System.Windows;
using System.Windows.Media;
using NetTopologySuite.Geometries;
using Point = System.Windows.Point;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

// ============================================================================
// GeometryPreview : 블록 로컬 지오메트리를 작은 벡터 이미지(썸네일)로 그리기
// ----------------------------------------------------------------------------
// 흐름도
//   Create(geometry, stroke)
//     → 비었으면 null
//     → Envelope 로 100x100 상자(여백 6)에 맞는 배율·중앙 정렬 오프셋 계산
//     → Map: 지오메트리 좌표 → 상자 좌표 (Y 축 뒤집기: 화면은 아래가 +)
//     → AddGeometry: Polygon(외곽+구멍 닫힘)/LineString/컬렉션(재귀) → AddLine → StreamGeometry
//     → 펜(1.1)으로 DrawingGroup 구성(투명 사각형으로 캔버스 크기 고정) → DrawingImage(Freeze)
// ============================================================================
namespace SpatialEditor.App;

/// <summary>블록의 로컬 좌표 지오메트리를 작은 벡터 이미지(썸네일/미리보기)로 그립니다.</summary>
internal static class GeometryPreview
{
    /// <summary>미리보기 캔버스의 한 변 길이(고정, 도면과 무관한 논리 단위).</summary>
    private const double Box = 100;
    /// <summary>캔버스 가장자리 여백(논리 단위).</summary>
    private const double Margin = 6;

    /// <summary>지오메트리를 캔버스에 맞게 축소·중앙정렬해 윤곽선 이미지로 만듭니다.</summary>
    /// <param name="geometry">그릴 지오메트리(블록 로컬 좌표). null 이거나 비어 있으면 null 반환.</param>
    /// <param name="stroke">윤곽선 색.</param>
    /// <returns>Freeze 된 DrawingImage(스레드 간 공유 가능). 그릴 것이 없으면 null.</returns>
    public static ImageSource? Create(NtsGeometry? geometry, Color stroke)
    {
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }

        // 1) 경계 상자로 배율을 정합니다(폭/높이가 0 이어도 나눗셈이 안전하도록 아주 작은 값으로 하한).
        var envelope = geometry.EnvelopeInternal;
        var width = Math.Max(envelope.Width, 1e-9);
        var height = Math.Max(envelope.Height, 1e-9);
        var scale = (Box - 2 * Margin) / Math.Max(width, height);
        var offsetX = (Box - width * scale) / 2;
        var offsetY = (Box - height * scale) / 2;

        // 2) 좌표 변환: 중앙 정렬 오프셋을 더하고 Y 를 뒤집습니다(지오메트리 Y 는 위가 +, 화면은 아래가 +).
        Point Map(Coordinate c) => new(
            offsetX + (c.X - envelope.MinX) * scale,
            Box - offsetY - (c.Y - envelope.MinY) * scale);

        // 3) 지오메트리를 StreamGeometry 경로로 옮기고 Freeze 합니다.
        var stream = new StreamGeometry();
        using (var context = stream.Open())
        {
            AddGeometry(context, geometry, Map);
        }

        stream.Freeze();
        // 4) 선 스타일(둥근 연결/끝)을 갖는 펜을 만들고 DrawingGroup 으로 묶습니다.
        var brush = new SolidColorBrush(stroke);
        brush.Freeze();
        var pen = new Pen(brush, 1.1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var drawing = new DrawingGroup();
        // 고정 캔버스: 형상의 가로세로 비율과 상관없이 모든 썸네일이 같은 크기가 되도록 투명 사각형을 넣습니다.
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Box, Box))));
        drawing.Children.Add(new GeometryDrawing(null, pen, stream));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    /// <summary>지오메트리 종류별로 경로를 추가합니다(폴리곤은 외곽+구멍, 컬렉션은 재귀, 점 등은 무시).</summary>
    /// <param name="context">경로를 기록할 StreamGeometry 컨텍스트.</param>
    /// <param name="geometry">추가할 지오메트리.</param>
    /// <param name="map">지오메트리 좌표 → 캔버스 좌표 변환 함수.</param>
    private static void AddGeometry(StreamGeometryContext context, NtsGeometry geometry, Func<Coordinate, Point> map)
    {
        switch (geometry)
        {
            case Polygon polygon:
                AddLine(context, polygon.ExteriorRing.Coordinates, map, close: true);
                foreach (var hole in polygon.Holes)
                {
                    AddLine(context, hole.Coordinates, map, close: true);
                }

                break;
            case LineString line:
                AddLine(context, line.Coordinates, map, close: false);
                break;
            case NetTopologySuite.Geometries.GeometryCollection collection:
                foreach (var part in collection.Geometries)
                {
                    AddGeometry(context, part, map);
                }

                break;
        }
    }

    /// <summary>좌표열을 하나의 도형(figure)으로 추가합니다(점이 2개 미만이면 무시).</summary>
    /// <param name="context">경로를 기록할 컨텍스트.</param>
    /// <param name="coordinates">추가할 좌표열.</param>
    /// <param name="map">좌표 변환 함수.</param>
    /// <param name="close">true 면 시작점으로 닫아 폐곡선으로 그림(폴리곤 링).</param>
    private static void AddLine(StreamGeometryContext context, Coordinate[] coordinates, Func<Coordinate, Point> map, bool close)
    {
        if (coordinates.Length < 2)
        {
            return;
        }

        context.BeginFigure(map(coordinates[0]), false, close);
        for (var i = 1; i < coordinates.Length; i++)
        {
            context.LineTo(map(coordinates[i]), true, true);
        }
    }
}
