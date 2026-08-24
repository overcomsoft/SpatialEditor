using System.Numerics;
using netDxf;
using netDxf.Entities;
using NetTopologySuite;
using NetTopologySuite.Geometries;

namespace SpatialEditor.Cad;

public sealed class DxfBlockImporter
{
    private readonly GeometryFactory factory;

    public DxfBlockImporter(int srid = 5186)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
    }

        public IReadOnlyList<CadImportResult> Import(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file was not found.", filePath);
            }

            var document = DxfDocument.Load(filePath)
                ?? throw new InvalidDataException("The DXF document could not be loaded.");
            var byLayer = new Dictionary<string, LayerAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in document.Entities.All)
            {
                AddEntity(byLayer, entity, Matrix2D.Identity, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            return byLayer.Values
                .Where(value => value.Geometries.Count > 0)
                .Select(value => value.ToResult(factory))
                .ToArray();
        }

        private void AddEntity(Dictionary<string, LayerAccumulator> byLayer, EntityObject entity, Matrix2D transform, HashSet<string> blockStack)
        {
            var layerName = entity.Layer?.Name ?? "0";
            if (!byLayer.TryGetValue(layerName, out var layer))
            {
                layer = new LayerAccumulator(layerName);
                byLayer.Add(layerName, layer);
            }

            if (entity is Insert insert)
            {
                var blockName = insert.Block.Name;
                if (!blockStack.Add(blockName))
                {
                    layer.UnsupportedEntities.Add("CircularInsert:" + blockName);
                    return;
                }

                var insertTransform = transform.Combine(Matrix2D.FromInsert(insert));
                foreach (var child in insert.Block.Entities)
                {
                    AddEntity(byLayer, child, insertTransform, blockStack);
                }

                blockStack.Remove(blockName);
                return;
            }

            var geometry = CreateGeometry(entity, transform);
            if (geometry is null)
            {
                layer.UnsupportedEntities.Add(entity.Type.ToString());
                return;
            }

            layer.Geometries.Add(geometry);
            layer.EntityCount++;
            if (entity is Text text)
            {
                layer.Attributes[$"text_{layer.EntityCount}"] = text.Value;
            }
            else if (entity is MText mText)
            {
                layer.Attributes[$"mtext_{layer.EntityCount}"] = mText.Value;
            }
        }

        private Geometry? CreateGeometry(EntityObject entity, Matrix2D transform)
        {
            return entity switch
            {
                Line line => factory.CreateLineString(Transform(new[]
                {
                    new Coordinate(line.StartPoint.X, line.StartPoint.Y),
                    new Coordinate(line.EndPoint.X, line.EndPoint.Y)
                }, transform)),
                Polyline2D polyline => CreatePolyline(polyline.Vertexes.Select(vertex => new Coordinate(vertex.Position.X, vertex.Position.Y)), polyline.IsClosed, transform),
                Polyline3D polyline => CreatePolyline(polyline.Vertexes.Select(vertex => new Coordinate(vertex.X, vertex.Y)), polyline.IsClosed, transform),
                netDxf.Entities.Point point => factory.CreatePoint(Transform(new Coordinate(point.Position.X, point.Position.Y), transform)),
                Circle circle => CreateCircle(circle.Center, circle.Radius, transform),
                Arc arc => CreateArc(arc.Center, arc.Radius, arc.StartAngle, arc.EndAngle, transform),
                Text text => factory.CreatePoint(Transform(new Coordinate(text.Position.X, text.Position.Y), transform)),
                MText mText => factory.CreatePoint(Transform(new Coordinate(mText.Position.X, mText.Position.Y), transform)),
                _ => null
            };
        }

        private Geometry CreatePolyline(IEnumerable<Coordinate> source, bool closed, Matrix2D transform)
        {
            var coordinates = Transform(source.ToArray(), transform);
            if (closed && coordinates.Length >= 4)
            {
                if (!coordinates[0].Equals2D(coordinates[^1]))
                {
                    coordinates = coordinates.Append(coordinates[0]).ToArray();
                }

                var polygon = factory.CreatePolygon(factory.CreateLinearRing(coordinates));
                if (polygon.IsValid)
                {
                    return polygon;
                }
            }

            return factory.CreateLineString(coordinates);
        }

        private Geometry CreateCircle(netDxf.Vector3 center, double radius, Matrix2D transform)
            => factory.CreateLineString(SampleCurve(center.X, center.Y, radius, radius, 0, 0, 360, transform, true));

        private Geometry CreateArc(netDxf.Vector3 center, double radius, double startAngle, double endAngle, Matrix2D transform)
            => factory.CreateLineString(SampleCurve(center.X, center.Y, radius, radius, 0, startAngle, endAngle, transform, false));

        private Coordinate[] SampleCurve(double centerX, double centerY, double radiusX, double radiusY, double rotation, double startAngle, double endAngle, Matrix2D transform, bool closed)
        {
            var span = endAngle - startAngle;
            if (span <= 0)
            {
                span += 360;
            }

            var count = Math.Max(16, (int)Math.Ceiling(span / 10));
            var coordinates = Enumerable.Range(0, count + 1).Select(index =>
            {
                var angle = (startAngle + span * index / count) * Math.PI / 180.0;
                return new Coordinate(centerX + radiusX * Math.Cos(angle), centerY + radiusY * Math.Sin(angle));
            }).ToArray();
            if (closed && !coordinates[0].Equals2D(coordinates[^1]))
            {
                coordinates = coordinates.Append(coordinates[0]).ToArray();
            }

            return Transform(coordinates, transform);
        }

        private static Coordinate[] Transform(IEnumerable<Coordinate> coordinates, Matrix2D transform)
            => coordinates.Select(coordinate => Transform(coordinate, transform)).ToArray();

        private static Coordinate Transform(Coordinate coordinate, Matrix2D transform)
            => new(transform.A * coordinate.X + transform.B * coordinate.Y + transform.C,
                transform.D * coordinate.X + transform.E * coordinate.Y + transform.F);

        private sealed class LayerAccumulator
        {
            public LayerAccumulator(string name) => Name = name;
            public string Name { get; }
            public List<Geometry> Geometries { get; } = new();
            public Dictionary<string, string?> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<string> UnsupportedEntities { get; } = new();
            public int EntityCount { get; set; }

            public CadImportResult ToResult(GeometryFactory factory)
            {
                if (UnsupportedEntities.Count > 0)
                {
                    Attributes["unsupported_entities"] = string.Join(",", UnsupportedEntities.Distinct());
                }

                var geometry = factory.CreateGeometryCollection(Geometries.ToArray());
                var outer = geometry.ConvexHull() as Polygon ?? CreateEnvelopePolygon(factory, geometry.EnvelopeInternal);
                return new CadImportResult(Name, Name, geometry, outer, Attributes, "dxf_layer", EntityCount);
            }

            private static Polygon CreateEnvelopePolygon(GeometryFactory factory, Envelope envelope)
            {
                var minX = envelope.MinX;
                var maxX = envelope.MaxX;
                var minY = envelope.MinY;
                var maxY = envelope.MaxY;
                if (minX == maxX)
                {
                    minX -= 0.5;
                    maxX += 0.5;
                }
                if (minY == maxY)
                {
                    minY -= 0.5;
                    maxY += 0.5;
                }

                var coordinates = new[]
                {
                    new Coordinate(minX, minY), new Coordinate(maxX, minY),
                    new Coordinate(maxX, maxY), new Coordinate(minX, maxY),
                    new Coordinate(minX, minY)
                };
                return factory.CreatePolygon(factory.CreateLinearRing(coordinates));
            }
        }

        private readonly record struct Matrix2D(double A, double B, double C, double D, double E, double F)
        {
            public static Matrix2D Identity => new(1, 0, 0, 0, 1, 0);

            public static Matrix2D FromInsert(Insert insert)
            {
                var angle = insert.Rotation * Math.PI / 180.0;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                return new(insert.Scale.X * cos, -insert.Scale.Y * sin, insert.Position.X,
                    insert.Scale.X * sin, insert.Scale.Y * cos, insert.Position.Y);
            }

            public Matrix2D Combine(Matrix2D next) => new(
                A * next.A + B * next.D, A * next.B + B * next.E, A * next.C + B * next.F + C,
                D * next.A + E * next.D, D * next.B + E * next.E, D * next.C + E * next.F + F);
        }
}