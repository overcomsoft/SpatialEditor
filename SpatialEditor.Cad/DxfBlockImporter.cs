using System.Globalization;
using System.Numerics;
using netDxf;
using netDxf.Entities;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;

namespace SpatialEditor.Cad;

public sealed class DxfBlockImporter
{
    private const int MinCurveSegments = 8;
    private const int MaxCurveSegments = 2000;
    private const string EquipmentLayerName = "Equipment";
    private const string EquipmentBoundingBoxLayerName = "Equipment-poly";

    private readonly GeometryFactory factory;
    private readonly double flattening;

    public DxfBlockImporter(int srid = 5186, double flattening = 0.01)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        this.flattening = flattening > 0 ? flattening : 0.01;
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

        public IReadOnlyList<CadBlockInstanceResult> ImportBlockInstances(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file was not found.", filePath);
            }

            var document = DxfDocument.Load(filePath)
                ?? throw new InvalidDataException("The DXF document could not be loaded.");
            var results = new List<CadBlockInstanceResult>();
            foreach (var insert in document.Entities.Inserts)
            {
                var result = BuildInsertResult(insert, insert.Layer?.Name);
                if (result is not null)
                {
                    results.Add(result);
                }
            }

            return results;
        }

        private CadBlockInstanceResult? BuildInsertResult(Insert insert, string? layerName)
        {
            var geometries = new List<Geometry>();
            var unsupportedEntities = new List<string>();
            CollectInsertGeometry(insert, Matrix2D.Identity, geometries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), unsupportedEntities);
            if (geometries.Count == 0)
            {
                return null;
            }

            var actualGeometry = factory.CreateGeometryCollection(geometries.ToArray());
            var (outerGeometry, isFallback, fallbackReason) = CreatePolygonizedOuterGeometry(geometries, actualGeometry.EnvelopeInternal);
            var attributes = BuildInsertAttributes(insert, unsupportedEntities);
            if (isFallback && fallbackReason is not null)
            {
                attributes["outer_geom_fallback_reason"] = fallbackReason;
            }

            return new CadBlockInstanceResult(insert.Block.Name, layerName, actualGeometry, outerGeometry, attributes, isFallback);
        }

        /// <summary>
        /// Reads only the <see cref="EquipmentLayerName"/> INSERTs of the DXF as block objects —
        /// one object per INSERT, nothing else (no layer aggregates, no loose entities, no other
        /// layers). Each carries the usual placement attributes (insert point, rotation, scale).
        /// </summary>
        public IReadOnlyList<CadBlockInstanceResult> ImportEquipmentBlockObjects(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file was not found.", filePath);
            }

            var document = DxfDocument.Load(filePath)
                ?? throw new InvalidDataException("The DXF document could not be loaded.");
            var results = new List<CadBlockInstanceResult>();
            foreach (var insert in document.Entities.Inserts)
            {
                if (!string.Equals(insert.Layer?.Name, EquipmentLayerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = BuildInsertResult(insert, insert.Layer?.Name);
                if (result is not null)
                {
                    results.Add(result);
                }
            }

            return results;
        }

        /// <summary>
        /// Simplifies every <see cref="EquipmentLayerName"/> block instance (line-only equipment
        /// symbols) down to its 2D axis-aligned bounding box, saved as its own
        /// <see cref="EquipmentBoundingBoxLayerName"/> layer so equipment footprints can be
        /// selected/moved as plain rectangles instead of their detailed line art.
        /// </summary>
        public IReadOnlyList<CadImportResult> ImportEquipmentBoundingBoxes(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file was not found.", filePath);
            }

            var document = DxfDocument.Load(filePath)
                ?? throw new InvalidDataException("The DXF document could not be loaded.");
            var results = new List<CadImportResult>();
            foreach (var insert in document.Entities.Inserts)
            {
                if (!string.Equals(insert.Layer?.Name, EquipmentLayerName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var geometries = new List<Geometry>();
                var unsupportedEntities = new List<string>();
                CollectInsertGeometry(insert, Matrix2D.Identity, geometries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), unsupportedEntities);
                if (geometries.Count == 0)
                {
                    continue;
                }

                var actualGeometry = factory.CreateGeometryCollection(geometries.ToArray());
                var boundingBox = CreateEnvelopePolygon(actualGeometry.EnvelopeInternal);
                var attributes = BuildInsertAttributes(insert, unsupportedEntities);

                results.Add(new CadImportResult(
                    insert.Block.Name,
                    EquipmentBoundingBoxLayerName,
                    boundingBox,
                    boundingBox,
                    attributes,
                    "cad_block",
                    geometries.Count));
            }

            return results;
        }

        /// <summary>
        /// Combines the block's own DXF ATTRIB tag/value pairs (often empty — most symbol blocks,
        /// e.g. equipment gears, define no ATTRIBs) with the insert's placement data, so every
        /// block instance always carries queryable/inspectable rotation, position, and scale
        /// even when the DXF author never added explicit attributes.
        /// </summary>
        private static Dictionary<string, string?> BuildInsertAttributes(Insert insert, List<string> unsupportedEntities)
        {
            var attributes = insert.Attributes.ToDictionary(attribute => attribute.Tag, attribute => (string?)attribute.Value, StringComparer.OrdinalIgnoreCase);
            // insert.Handle is the DXF document's own unique identifier for this specific
            // instance (group code 5); insert.Block.Name is only the shared block *type* name,
            // repeated by every instance of that block, so both are worth surfacing separately.
            attributes["block_name"] = insert.Block.Name;
            attributes["block_handle"] = insert.Handle;
            if (!string.IsNullOrWhiteSpace(insert.Block.Description))
            {
                attributes["block_description"] = insert.Block.Description;
            }

            attributes["block_insert_x"] = insert.Position.X.ToString("0.####", CultureInfo.InvariantCulture);
            attributes["block_insert_y"] = insert.Position.Y.ToString("0.####", CultureInfo.InvariantCulture);
            attributes["block_rotation_deg"] = insert.Rotation.ToString("0.####", CultureInfo.InvariantCulture);
            attributes["block_scale_x"] = insert.Scale.X.ToString("0.####", CultureInfo.InvariantCulture);
            attributes["block_scale_y"] = insert.Scale.Y.ToString("0.####", CultureInfo.InvariantCulture);
            if (unsupportedEntities.Count > 0)
            {
                attributes["unsupported_entities"] = string.Join(",", unsupportedEntities.Distinct());
            }

            return attributes;
        }

        private void CollectInsertGeometry(Insert insert, Matrix2D transform, List<Geometry> geometries, HashSet<string> blockStack, List<string> unsupportedEntities)
        {
            if (!blockStack.Add(insert.Block.Name))
            {
                unsupportedEntities.Add("CircularInsert:" + insert.Block.Name);
                return;
            }

            var insertTransform = transform.Combine(Matrix2D.FromInsert(insert));
            foreach (var entity in insert.Block.Entities)
            {
                if (entity is Insert childInsert)
                {
                    CollectInsertGeometry(childInsert, insertTransform, geometries, blockStack, unsupportedEntities);
                    continue;
                }

                var geometry = CreateGeometry(entity, insertTransform);
                if (geometry is not null)
                {
                    geometries.Add(geometry);
                }
                else
                {
                    unsupportedEntities.Add(entity.Type.ToString());
                }
            }

            blockStack.Remove(insert.Block.Name);
        }

        /// <summary>
        /// Polygonizes the linework of a block instance. When no closed region can be formed,
        /// falls back to the instance's bounding envelope and reports why (via Polygonizer's
        /// dangle/cut-edge/invalid-ring diagnostics) so the caller can surface it instead of
        /// silently shipping an inaccurate outline.
        /// </summary>
        private (Geometry Outer, bool IsFallback, string? FallbackReason) CreatePolygonizedOuterGeometry(IEnumerable<Geometry> geometries, Envelope fallbackEnvelope)
        {
            var linework = new List<LineString>();
            foreach (var geometry in geometries)
            {
                switch (geometry)
                {
                    case LineString line:
                        linework.Add(line);
                        break;
                    case Polygon polygon:
                        linework.Add(polygon.ExteriorRing);
                        for (var index = 0; index < polygon.NumInteriorRings; index++)
                        {
                            linework.Add(polygon.GetInteriorRingN(index));
                        }
                        break;
                }
            }

            if (linework.Count == 0)
            {
                return (CreateEnvelopePolygon(fallbackEnvelope), true, "no line or polygon geometry to polygonize");
            }

            var polygonizer = new Polygonizer();
            polygonizer.Add(factory.CreateMultiLineString(linework.ToArray()));
            var polygons = polygonizer.GetPolygons()
                .Cast<Polygon>()
                .Where(polygon => polygon.IsValid && !polygon.IsEmpty)
                .ToArray();
            if (polygons.Length > 0)
            {
                var union = factory.CreateMultiPolygon(polygons).Union();
                if (!union.IsEmpty && union.IsValid)
                {
                    return (union, false, null);
                }
            }

            var dangleCount = polygonizer.GetDangles().Count;
            var cutEdgeCount = polygonizer.GetCutEdges().Count;
            var invalidRingCount = polygonizer.GetInvalidRingLines().Count;
            var reason = $"no closed polygon from {linework.Count} line(s): dangles={dangleCount}, cut-edges={cutEdgeCount}, invalid-rings={invalidRingCount}";
            return (CreateEnvelopePolygon(fallbackEnvelope), true, reason);
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
                Polyline2D polyline => CreatePolyline2D(polyline, transform),
                Polyline3D polyline => CreatePolyline(polyline.Vertexes.Select(vertex => new Coordinate(vertex.X, vertex.Y)), polyline.IsClosed, transform),
                netDxf.Entities.Point point => factory.CreatePoint(Transform(new Coordinate(point.Position.X, point.Position.Y), transform)),
                Circle circle => CreateOpenCurve(circle.PolygonalVertexes(PointsPerFullCircle(circle.Radius)), circle.Center, transform),
                Arc arc => CreateOpenCurve(arc.PolygonalVertexes(PointsForOpenSpan(arc.Radius, ArcSpanRadians(arc.StartAngle, arc.EndAngle))), arc.Center, transform),
                netDxf.Entities.Ellipse ellipse => CreateOpenCurve(
                    ellipse.PolygonalVertexes(ellipse.IsFullEllipse
                        ? PointsPerFullCircle(EllipseRadius(ellipse))
                        : PointsForOpenSpan(EllipseRadius(ellipse), ArcSpanRadians(ellipse.StartAngle, ellipse.EndAngle))),
                    ellipse.Center,
                    transform),
                Spline spline => CreateSplineGeometry(spline, transform),
                Text text => factory.CreatePoint(Transform(new Coordinate(text.Position.X, text.Position.Y), transform)),
                MText mText => factory.CreatePoint(Transform(new Coordinate(mText.Position.X, mText.Position.Y), transform)),
                _ => null
            };
        }

        private Geometry CreatePolyline2D(Polyline2D polyline, Matrix2D transform)
        {
            var vertices = polyline.Vertexes;
            if (vertices.Count == 0)
            {
                return factory.CreateLineString(Array.Empty<Coordinate>());
            }

            var precision = ComputeBulgePrecision(vertices, polyline.IsClosed);
            var points = precision > 0
                ? polyline.PolygonalVertexes(precision)
                : vertices.Select(vertex => vertex.Position).ToList();
            return CreatePolyline(points.Select(point => new Coordinate(point.X, point.Y)), polyline.IsClosed, transform);
        }

        private int ComputeBulgePrecision(IReadOnlyList<Polyline2DVertex> vertices, bool closed)
        {
            var count = vertices.Count;
            var segments = closed ? count : count - 1;
            var minRadius = double.MaxValue;
            var hasBulge = false;
            for (var i = 0; i < segments; i++)
            {
                var bulge = vertices[i].Bulge;
                if (Math.Abs(bulge) < 1e-9)
                {
                    continue;
                }

                var next = vertices[(i + 1) % count];
                var (_, radius, _, _) = netDxf.MathHelper.ArcFromBulge(vertices[i].Position, next.Position, bulge);
                if (radius > 1e-9)
                {
                    hasBulge = true;
                    minRadius = Math.Min(minRadius, radius);
                }
            }

            return hasBulge ? PointsPerFullCircle(minRadius) : 0;
        }

        private Geometry CreateSplineGeometry(Spline spline, Matrix2D transform)
        {
            var closed = spline.IsClosed || spline.IsClosedPeriodic;
            var radius = EstimateSplineRadius(spline.ControlPoints);
            var precision = Math.Max(PointsPerFullCircle(radius), spline.ControlPoints.Length * 4);
            var points = spline.PolygonalVertexes(Math.Max(precision, 2));
            return CreatePolyline(points.Select(point => new Coordinate(point.X, point.Y)), closed, transform);
        }

        private static double EstimateSplineRadius(IReadOnlyList<netDxf.Vector3> controlPoints)
        {
            if (controlPoints.Count == 0)
            {
                return 1.0;
            }

            var minX = controlPoints.Min(point => point.X);
            var maxX = controlPoints.Max(point => point.X);
            var minY = controlPoints.Min(point => point.Y);
            var maxY = controlPoints.Max(point => point.Y);
            var diagonal = Math.Sqrt(Math.Pow(maxX - minX, 2) + Math.Pow(maxY - minY, 2));
            return Math.Max(diagonal / 2, 1e-6);
        }

        private Geometry CreateOpenCurve(IEnumerable<netDxf.Vector2> localPoints, netDxf.Vector3 center, Matrix2D transform)
        {
            var coordinates = localPoints.Select(point => new Coordinate(center.X + point.X, center.Y + point.Y)).ToArray();
            return factory.CreateLineString(Transform(coordinates, transform));
        }

        private static double EllipseRadius(netDxf.Entities.Ellipse ellipse)
            => Math.Min(ellipse.MajorAxis, ellipse.MinorAxis) * 0.5;

        private static double ArcSpanRadians(double startAngleDeg, double endAngleDeg)
        {
            var span = endAngleDeg - startAngleDeg;
            if (span <= 0)
            {
                span += 360;
            }

            return span * Math.PI / 180.0;
        }

        /// <summary>
        /// Number of segments needed to keep a full circle of the given radius within the
        /// configured flattening (sagitta) tolerance, mirroring dxf_to_shp.py's --flattening.
        /// </summary>
        private int PointsPerFullCircle(double radius)
        {
            radius = Math.Abs(radius);
            if (radius <= 1e-9)
            {
                return MinCurveSegments;
            }

            var ratio = Math.Clamp(1 - flattening / radius, -1.0, 1.0);
            var maxSegmentAngle = 2 * Math.Acos(ratio);
            if (maxSegmentAngle <= 1e-9)
            {
                return MaxCurveSegments;
            }

            var segments = (int)Math.Ceiling(2 * Math.PI / maxSegmentAngle);
            return Math.Clamp(segments, MinCurveSegments, MaxCurveSegments);
        }

        private int PointsForOpenSpan(double radius, double spanRadians)
        {
            var perCircle = PointsPerFullCircle(radius);
            var raw = (int)Math.Ceiling(perCircle * spanRadians / (2 * Math.PI)) + 1;
            return Math.Clamp(raw, 2, MaxCurveSegments + 1);
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

        private static Coordinate[] Transform(IEnumerable<Coordinate> coordinates, Matrix2D transform)
            => coordinates.Select(coordinate => Transform(coordinate, transform)).ToArray();

        private static Coordinate Transform(Coordinate coordinate, Matrix2D transform)
            => new(transform.A * coordinate.X + transform.B * coordinate.Y + transform.C,
                transform.D * coordinate.X + transform.E * coordinate.Y + transform.F);

        private Polygon CreateEnvelopePolygon(Envelope envelope)
        {
            var minX = envelope.MinX;
            var maxX = envelope.MaxX;
            var minY = envelope.MinY;
            var maxY = envelope.MaxY;
            if (minX == maxX) { minX -= 0.5; maxX += 0.5; }
            if (minY == maxY) { minY -= 0.5; maxY += 0.5; }
            var coordinates = new[]
            {
                new Coordinate(minX, minY), new Coordinate(maxX, minY),
                new Coordinate(maxX, maxY), new Coordinate(minX, maxY),
                new Coordinate(minX, minY)
            };
            return factory.CreatePolygon(factory.CreateLinearRing(coordinates));
        }

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
                var polygons = Geometries.OfType<Polygon>().Where(polygon => polygon.IsValid).ToArray();
                var isFallback = polygons.Length == 0;
                if (isFallback)
                {
                    Attributes["outer_geom_fallback_reason"] = "no closed polygon entities in layer";
                }

                Geometry outer = polygons.Length switch
                {
                    0 => CreateEnvelopePolygon(factory, geometry.EnvelopeInternal),
                    1 => polygons[0],
                    _ => factory.CreateMultiPolygon(polygons)
                };
                return new CadImportResult(Name, Name, geometry, outer, Attributes, "dxf_layer", EntityCount, isFallback);
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