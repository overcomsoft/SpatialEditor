using System.Windows;
using System.Windows.Media;
using NetTopologySuite.Geometries;
using Point = System.Windows.Point;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;

namespace SpatialEditor.App;

/// <summary>Renders a block's local-space geometry as a small vector image (thumbnails and previews).</summary>
internal static class GeometryPreview
{
    private const double Box = 100;
    private const double Margin = 6;

    public static ImageSource? Create(NtsGeometry? geometry, Color stroke)
    {
        if (geometry is null || geometry.IsEmpty)
        {
            return null;
        }

        var envelope = geometry.EnvelopeInternal;
        var width = Math.Max(envelope.Width, 1e-9);
        var height = Math.Max(envelope.Height, 1e-9);
        var scale = (Box - 2 * Margin) / Math.Max(width, height);
        var offsetX = (Box - width * scale) / 2;
        var offsetY = (Box - height * scale) / 2;

        Point Map(Coordinate c) => new(
            offsetX + (c.X - envelope.MinX) * scale,
            Box - offsetY - (c.Y - envelope.MinY) * scale);

        var stream = new StreamGeometry();
        using (var context = stream.Open())
        {
            AddGeometry(context, geometry, Map);
        }

        stream.Freeze();
        var brush = new SolidColorBrush(stroke);
        brush.Freeze();
        var pen = new Pen(brush, 1.1) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        var drawing = new DrawingGroup();
        // Fixed canvas so every thumbnail has the same size regardless of the shape's aspect ratio.
        drawing.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, Box, Box))));
        drawing.Children.Add(new GeometryDrawing(null, pen, stream));
        drawing.Freeze();
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

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
