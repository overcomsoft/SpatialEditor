using System.Globalization;
using System.Numerics;
using netDxf;
using netDxf.Entities;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Polygonize;

// ============================================================================
// DxfBlockImporter : DXF 파일을 읽어 NetTopologySuite 지오메트리로 변환
// ----------------------------------------------------------------------------
// 전체 흐름도
//  [A] 레이어 단위 가져오기  Import(filePath)
//      DXF 읽기(netDxf) → 모든 엔티티 순회 → INSERT 면 블록 정의 안으로 재귀 분해
//        (Matrix2D 누적, blockStack 으로 순환 참조 차단) → 엔티티별 CreateGeometry
//        (선/폴리라인/원/호/타원/스플라인 → 곡선은 flattening 허용오차로 폴리곤화)
//        → 레이어별 LayerAccumulator 에 집계 → ToResult: 닫힌 폴리곤 모아 외곽 결정
//        (없으면 Envelope 사각형으로 대체) → CadImportResult 목록
//  [B] 블록 인스턴스 가져오기  ImportBlockInstances / ImportEquipmentBlockObjects
//      DXF 읽기 → INSERT 마다 BuildInsertResult
//        → CollectInsertGeometry(블록 내부 재귀 분해 + 변환행렬)
//        → CreatePolygonizedOuterGeometry(선화 → Polygonizer → 합집합, 실패 시 Envelope 대체)
//        → BuildInsertAttributes(ATTRIB + 삽입점/회전/스케일) → CadBlockInstanceResult
//      (ImportEquipmentBlockObjects 는 "Equipment" 레이어 INSERT 만 대상)
//  [C] 장비 외곽 박스  ImportEquipmentBoundingBoxes
//      "Equipment" 레이어 INSERT → 지오메트리 수집 → Envelope 사각형 → "Equipment-poly" 레이어 결과
// ============================================================================
namespace SpatialEditor.Cad;

/// <summary>
/// DXF 파일을 읽어 레이어 집계(<see cref="CadImportResult"/>) 또는 블록 인스턴스
/// (<see cref="CadBlockInstanceResult"/>) 형태의 NTS 지오메트리로 변환하는 가져오기 엔진입니다.
/// 인스턴스는 상태(팩토리, 평탄화 허용오차)만 가지며 생성 후 변하지 않습니다.
/// </summary>
public sealed class DxfBlockImporter
{
    /// <summary>곡선(원/호 등)을 근사할 때의 최소 분할 수.</summary>
    private const int MinCurveSegments = 8;
    /// <summary>곡선 근사 분할 수의 상한(과도한 점 생성 방지).</summary>
    private const int MaxCurveSegments = 2000;
    /// <summary>장비 INSERT 가 놓이는 DXF 레이어 이름("Equipment"). 비교는 대소문자 무시.</summary>
    private const string EquipmentLayerName = "Equipment";
    /// <summary>장비 외곽 사각형 결과를 저장할 레이어 이름("Equipment-poly").</summary>
    private const string EquipmentBoundingBoxLayerName = "Equipment-poly";

    /// <summary>지오메트리 생성에 쓰는 NTS 팩토리(SRID 포함).</summary>
    private readonly GeometryFactory factory;
    /// <summary>곡선 평탄화 허용 오차(sagitta, 도면 단위). 작을수록 점이 많아져 정밀해짐.</summary>
    private readonly double flattening;

    /// <summary>가져오기 엔진을 만듭니다.</summary>
    /// <param name="srid">생성할 지오메트리의 SRID(좌표계 코드). 기본 5186(한국 중부원점 TM).</param>
    /// <param name="flattening">곡선을 직선으로 근사할 때의 허용 오차(도면 단위). 0 이하이면 0.01 로 대체. 기본 0.01.</param>
    public DxfBlockImporter(int srid = 5186, double flattening = 0.01)
    {
        factory = NtsGeometryServices.Instance.CreateGeometryFactory(srid);
        this.flattening = flattening > 0 ? flattening : 0.01;
    }

        /// <summary>
        /// DXF 의 모든 엔티티를 레이어별로 모아 가져옵니다(INSERT 는 블록 내부까지 분해).
        /// </summary>
        /// <param name="filePath">읽을 DXF 파일 경로. null 불가.</param>
        /// <returns>지오메트리가 1개 이상인 레이어마다 1건씩의 결과 목록.</returns>
        /// <exception cref="FileNotFoundException">파일이 없을 때.</exception>
        /// <exception cref="InvalidDataException">DXF 를 해석하지 못했을 때.</exception>
        public IReadOnlyList<CadImportResult> Import(string filePath)
        {
            // 1) 파일 존재 확인 후 DXF 를 읽습니다.
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("DXF file was not found.", filePath);
            }

            var document = DxfDocument.Load(filePath)
                ?? throw new InvalidDataException("The DXF document could not be loaded.");
            // 2) 레이어 이름(대소문자 무시)별 누적기를 준비하고, 최상위 엔티티를 변환행렬 항등으로 순회합니다.
            var byLayer = new Dictionary<string, LayerAccumulator>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in document.Entities.All)
            {
                AddEntity(byLayer, entity, Matrix2D.Identity, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }

            // 3) 지오메트리가 있는 레이어만 결과로 변환합니다.
            return byLayer.Values
                .Where(value => value.Geometries.Count > 0)
                .Select(value => value.ToResult(factory))
                .ToArray();
        }

        /// <summary>
        /// DXF 의 모든 최상위 INSERT 를 블록 인스턴스 1건씩으로 가져옵니다(레이어 무관).
        /// </summary>
        /// <param name="filePath">읽을 DXF 파일 경로. null 불가.</param>
        /// <returns>지오메트리를 얻은 INSERT 들의 결과 목록(빈 블록은 제외).</returns>
        /// <exception cref="FileNotFoundException">파일이 없을 때.</exception>
        /// <exception cref="InvalidDataException">DXF 를 해석하지 못했을 때.</exception>
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

        /// <summary>INSERT 1개를 인스턴스 결과(실제 형상 + 폴리곤화 외곽 + 속성)로 만듭니다.</summary>
        /// <param name="insert">변환할 INSERT 엔티티.</param>
        /// <param name="layerName">결과에 기록할 레이어 이름(null 가능).</param>
        /// <returns>변환 결과. 블록에서 얻은 지오메트리가 없으면 null.</returns>
        private CadBlockInstanceResult? BuildInsertResult(Insert insert, string? layerName)
        {
            // 1) 블록 내부를 재귀 분해해 월드 좌표 지오메트리를 모읍니다(순환 INSERT 는 건너뛰고 기록).
            var geometries = new List<Geometry>();
            var unsupportedEntities = new List<string>();
            CollectInsertGeometry(insert, Matrix2D.Identity, geometries, new HashSet<string>(StringComparer.OrdinalIgnoreCase), unsupportedEntities);
            if (geometries.Count == 0)
            {
                return null;
            }

            // 2) 원본 선화는 컬렉션으로 보존하고, 선택/이동용 외곽은 폴리곤화로 별도 계산합니다.
            var actualGeometry = factory.CreateGeometryCollection(geometries.ToArray());
            var (outerGeometry, isFallback, fallbackReason) = CreatePolygonizedOuterGeometry(geometries, actualGeometry.EnvelopeInternal);
            // 3) 속성을 구성하고, 외곽이 대체본이면 그 사유를 속성으로 남겨 호출자가 알 수 있게 합니다.
            var attributes = BuildInsertAttributes(insert, unsupportedEntities);
            if (isFallback && fallbackReason is not null)
            {
                attributes["outer_geom_fallback_reason"] = fallbackReason;
            }

            return new CadBlockInstanceResult(insert.Block.Name, layerName, actualGeometry, outerGeometry, attributes, isFallback);
        }

        /// <summary>
        /// DXF 에서 <see cref="EquipmentLayerName"/> 레이어의 INSERT 만 블록 객체로 읽습니다 —
        /// INSERT 1개당 객체 1개이며, 레이어 집계/낱개 엔티티/다른 레이어는 읽지 않습니다.
        /// 각 객체는 삽입점, 회전, 스케일 등 배치 속성을 가집니다.
        /// </summary>
        /// <param name="filePath">읽을 DXF 파일 경로. null 불가.</param>
        /// <returns>Equipment 레이어 INSERT 별 결과 목록.</returns>
        /// <exception cref="FileNotFoundException">파일이 없을 때.</exception>
        /// <exception cref="InvalidDataException">DXF 를 해석하지 못했을 때.</exception>
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
                // Equipment 레이어가 아닌 INSERT 는 건너뜁니다.
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
        /// 모든 <see cref="EquipmentLayerName"/> 블록 인스턴스(선만 있는 장비 기호)를 2D 축 정렬 외곽 사각형으로
        /// 단순화해 별도 <see cref="EquipmentBoundingBoxLayerName"/> 레이어로 만듭니다. 장비 발자국을 상세한 선화 대신
        /// 단순 사각형으로 선택/이동할 수 있게 합니다.
        /// </summary>
        /// <param name="filePath">읽을 DXF 파일 경로. null 불가.</param>
        /// <returns>장비마다 외곽 사각형 결과(FeatureType="cad_block", EntityCount=수집된 지오메트리 수).</returns>
        /// <exception cref="FileNotFoundException">파일이 없을 때.</exception>
        /// <exception cref="InvalidDataException">DXF 를 해석하지 못했을 때.</exception>
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
                // 수집한 전체 형상의 Envelope 를 사각형 폴리곤으로 만들어 실제/외곽 지오메트리로 함께 사용합니다.
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
        /// 블록 자체의 DXF ATTRIB 태그/값(대부분의 기호 블록은 ATTRIB 이 비어 있음)과 INSERT 의 배치 정보를 합쳐,
        /// DXF 작성자가 속성을 안 넣었어도 모든 인스턴스가 회전·위치·스케일을 조회할 수 있게 합니다.
        /// </summary>
        /// <param name="insert">대상 INSERT.</param>
        /// <param name="unsupportedEntities">변환하지 못한 엔티티 종류 목록(있으면 "unsupported_entities" 로 기록).</param>
        /// <returns>키가 대소문자 무시인 속성 사전. 값은 null 가능.</returns>
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

        /// <summary>
        /// INSERT 가 참조하는 블록 정의의 엔티티를 재귀적으로 변환·수집합니다(중첩 INSERT 포함).
        /// </summary>
        /// <param name="insert">처리할 INSERT.</param>
        /// <param name="transform">부모까지 누적된 변환행렬(블록 로컬 → 월드).</param>
        /// <param name="geometries">결과 지오메트리를 추가할 목록(출력).</param>
        /// <param name="blockStack">현재 재귀 경로의 블록 이름 집합(순환 참조 검출용).</param>
        /// <param name="unsupportedEntities">변환 불가 엔티티 종류를 기록할 목록(출력).</param>
        private void CollectInsertGeometry(Insert insert, Matrix2D transform, List<Geometry> geometries, HashSet<string> blockStack, List<string> unsupportedEntities)
        {
            // 1) 같은 블록이 자기 자신을 (직·간접) 참조하면 무한 재귀이므로 중단하고 사유를 기록합니다.
            if (!blockStack.Add(insert.Block.Name))
            {
                unsupportedEntities.Add("CircularInsert:" + insert.Block.Name);
                return;
            }

            // 2) 부모 변환에 이 INSERT 의 이동·회전·스케일을 합성합니다.
            var insertTransform = transform.Combine(Matrix2D.FromInsert(insert));
            // 3) 블록 안 엔티티를 순회: 중첩 INSERT 는 재귀, 나머지는 지오메트리로 변환(미지원 종류는 기록).
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

            // 4) 재귀 경로에서 제거해, 같은 블록의 형제 INSERT 는 정상 처리되게 합니다.
            blockStack.Remove(insert.Block.Name);
        }

        /// <summary>
        /// 블록 인스턴스 선화를 Polygonize 해 외곽 면을 만듭니다. 닫힌 영역이 없으면 인스턴스의 Envelope 로 대체하고,
        /// Polygonizer 의 dangle/cut-edge/invalid-ring 진단으로 사유를 알려, 부정확한 외곽이 조용히 전달되지 않게 합니다.
        /// </summary>
        /// <param name="geometries">월드 좌표 지오메트리들(선/폴리곤만 사용, 점 등은 무시).</param>
        /// <param name="fallbackEnvelope">대체 시 사용할 전체 Envelope.</param>
        /// <returns>Outer=외곽 지오메트리, IsFallback=대체 여부, FallbackReason=대체 사유(대체가 아니면 null).</returns>
        private (Geometry Outer, bool IsFallback, string? FallbackReason) CreatePolygonizedOuterGeometry(IEnumerable<Geometry> geometries, Envelope fallbackEnvelope)
        {
            // 1) 선과 폴리곤 링을 모두 선분 목록(linework)으로 평탄화합니다.
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

            // 선이 하나도 없으면 폴리곤화 불가 → Envelope 대체.
            if (linework.Count == 0)
            {
                return (CreateEnvelopePolygon(fallbackEnvelope), true, "no line or polygon geometry to polygonize");
            }

            // 2) 선분들로 닫힌 영역을 만들고, 유효한 면만 합집합해 하나의 외곽으로 삼습니다.
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

            // 3) 실패 시 진단 수치(끊어진 끝/절단 변/잘못된 링)를 사유 문자열로 만들어 Envelope 로 대체합니다.
            var dangleCount = polygonizer.GetDangles().Count;
            var cutEdgeCount = polygonizer.GetCutEdges().Count;
            var invalidRingCount = polygonizer.GetInvalidRingLines().Count;
            var reason = $"no closed polygon from {linework.Count} line(s): dangles={dangleCount}, cut-edges={cutEdgeCount}, invalid-rings={invalidRingCount}";
            return (CreateEnvelopePolygon(fallbackEnvelope), true, reason);
        }

        /// <summary>
        /// 엔티티 1개를 레이어 누적기에 추가합니다. INSERT 는 블록 정의 내부로 재귀 분해(변환 누적)하고,
        /// 일반 엔티티는 지오메트리로 변환해 자신의 레이어에 모읍니다.
        /// </summary>
        /// <param name="byLayer">레이어 이름 → 누적기 사전(출력, 없으면 생성).</param>
        /// <param name="entity">처리할 DXF 엔티티.</param>
        /// <param name="transform">누적 변환행렬(블록 로컬 → 월드).</param>
        /// <param name="blockStack">현재 재귀 경로의 블록 이름(순환 참조 검출용).</param>
        private void AddEntity(Dictionary<string, LayerAccumulator> byLayer, EntityObject entity, Matrix2D transform, HashSet<string> blockStack)
        {
            // 1) 엔티티의 레이어 누적기를 찾거나 만듭니다(레이어 없음은 "0").
            var layerName = entity.Layer?.Name ?? "0";
            if (!byLayer.TryGetValue(layerName, out var layer))
            {
                layer = new LayerAccumulator(layerName);
                byLayer.Add(layerName, layer);
            }

            // 2) INSERT: 순환 검사 후 블록 안 엔티티를 재귀 처리합니다. 자식은 자기 자신의 레이어에 집계됩니다.
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

            // 3) 일반 엔티티: 지오메트리로 변환, 미지원이면 종류만 기록, 텍스트 값은 속성(text_N/mtext_N)으로 보존합니다.
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

        /// <summary>DXF 엔티티를 종류별로 NTS 지오메트리로 변환합니다(곡선은 폴리라인 근사).</summary>
        /// <param name="entity">변환할 엔티티.</param>
        /// <param name="transform">적용할 변환행렬(블록 로컬 → 월드).</param>
        /// <returns>변환된 지오메트리. 지원하지 않는 엔티티 종류면 null. 문자(Text/MText)는 삽입점 Point 로 변환.</returns>
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

        /// <summary>Polyline2D 를 변환합니다. 호(bulge)가 있으면 곡선을 분할해 근사합니다.</summary>
        /// <param name="polyline">원본 2D 폴리라인.</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>닫힘이면 Polygon(유효할 때), 아니면 LineString. 정점이 없으면 빈 LineString.</returns>
        private Geometry CreatePolyline2D(Polyline2D polyline, Matrix2D transform)
        {
            var vertices = polyline.Vertexes;
            if (vertices.Count == 0)
            {
                return factory.CreateLineString(Array.Empty<Coordinate>());
            }

            // 호 세그먼트가 있으면 가장 작은 반지름 기준 분할 수로 근사, 없으면 정점 그대로 사용합니다.
            var precision = ComputeBulgePrecision(vertices, polyline.IsClosed);
            var points = precision > 0
                ? polyline.PolygonalVertexes(precision)
                : vertices.Select(vertex => vertex.Position).ToList();
            return CreatePolyline(points.Select(point => new Coordinate(point.X, point.Y)), polyline.IsClosed, transform);
        }

        /// <summary>폴리라인에서 bulge(호) 구간의 가장 작은 반지름을 기준으로 필요한 분할 수를 계산합니다.</summary>
        /// <param name="vertices">폴리라인 정점들.</param>
        /// <param name="closed">닫힌 폴리라인이면 마지막-첫 정점 구간도 검사.</param>
        /// <returns>호가 하나도 없으면 0, 있으면 전체 원 기준 분할 수.</returns>
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

        /// <summary>스플라인을 제어점 규모에 비례한 분할 수로 폴리라인 근사합니다.</summary>
        /// <param name="spline">원본 스플라인.</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>닫힌 스플라인이면 Polygon(유효할 때), 아니면 LineString.</returns>
        private Geometry CreateSplineGeometry(Spline spline, Matrix2D transform)
        {
            var closed = spline.IsClosed || spline.IsClosedPeriodic;
            var radius = EstimateSplineRadius(spline.ControlPoints);
            var precision = Math.Max(PointsPerFullCircle(radius), spline.ControlPoints.Length * 4);
            var points = spline.PolygonalVertexes(Math.Max(precision, 2));
            return CreatePolyline(points.Select(point => new Coordinate(point.X, point.Y)), closed, transform);
        }

        /// <summary>제어점의 경계 상자 대각선의 절반으로 스플라인의 대략적인 반지름을 추정합니다.</summary>
        /// <param name="controlPoints">스플라인 제어점들.</param>
        /// <returns>추정 반지름(최소 1e-6, 제어점이 없으면 1.0).</returns>
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

        /// <summary>원/호/타원의 로컬 점열을 중심 좌표 기준으로 옮겨 변환행렬을 적용한 LineString 으로 만듭니다.</summary>
        /// <param name="localPoints">중심 기준 상대 좌표 점열.</param>
        /// <param name="center">곡선 중심(DXF 좌표).</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>근사된 LineString.</returns>
        private Geometry CreateOpenCurve(IEnumerable<netDxf.Vector2> localPoints, netDxf.Vector3 center, Matrix2D transform)
        {
            var coordinates = localPoints.Select(point => new Coordinate(center.X + point.X, center.Y + point.Y)).ToArray();
            return factory.CreateLineString(Transform(coordinates, transform));
        }

        /// <summary>타원의 분할 수 계산용 대표 반지름(장축·단축 중 작은 쪽의 절반).</summary>
        /// <param name="ellipse">대상 타원.</param>
        /// <returns>대표 반지름.</returns>
        private static double EllipseRadius(netDxf.Entities.Ellipse ellipse)
            => Math.Min(ellipse.MajorAxis, ellipse.MinorAxis) * 0.5;

        /// <summary>호의 시작각~끝각 사이 각도 범위를 라디안으로 구합니다(0 이하이면 360 을 더해 반시계 방향 보정).</summary>
        /// <param name="startAngleDeg">시작각(도).</param>
        /// <param name="endAngleDeg">끝각(도).</param>
        /// <returns>각도 범위(라디안, 양수).</returns>
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
        /// 주어진 반지름의 전체 원을 설정된 평탄화(sagitta) 허용오차 안으로 근사하는 데 필요한 분할 수를 구합니다.
        /// dxf_to_shp.py 의 --flattening 과 같은 방식입니다.
        /// </summary>
        /// <param name="radius">원의 반지름(도면 단위, 절댓값 사용).</param>
        /// <returns>분할 수(MinCurveSegments~MaxCurveSegments 로 제한).</returns>
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

        /// <summary>호처럼 열린 구간의 분할 점 수를 전체 원 기준 분할 수에서 비례해 계산합니다.</summary>
        /// <param name="radius">반지름(도면 단위).</param>
        /// <param name="spanRadians">호의 각도 범위(라디안).</param>
        /// <returns>점 개수(2 이상, MaxCurveSegments+1 이하).</returns>
        private int PointsForOpenSpan(double radius, double spanRadians)
        {
            var perCircle = PointsPerFullCircle(radius);
            var raw = (int)Math.Ceiling(perCircle * spanRadians / (2 * Math.PI)) + 1;
            return Math.Clamp(raw, 2, MaxCurveSegments + 1);
        }

        /// <summary>좌표열에 변환을 적용해 폴리곤(닫힘, 유효) 또는 LineString 을 만듭니다.</summary>
        /// <param name="source">변환 전 좌표열.</param>
        /// <param name="closed">닫힌 폴리라인 여부. 4점 이상일 때만 폴리곤을 시도.</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>유효한 Polygon, 그렇지 않으면 LineString.</returns>
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

        /// <summary>좌표 배열 전체에 변환행렬을 적용합니다.</summary>
        /// <param name="coordinates">변환할 좌표들.</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>변환된 새 좌표 배열.</returns>
        private static Coordinate[] Transform(IEnumerable<Coordinate> coordinates, Matrix2D transform)
            => coordinates.Select(coordinate => Transform(coordinate, transform)).ToArray();

        /// <summary>좌표 1개에 2D 아핀 변환(x' = A·x + B·y + C, y' = D·x + E·y + F)을 적용합니다.</summary>
        /// <param name="coordinate">변환할 좌표.</param>
        /// <param name="transform">적용할 변환행렬.</param>
        /// <returns>변환된 좌표.</returns>
        private static Coordinate Transform(Coordinate coordinate, Matrix2D transform)
            => new(transform.A * coordinate.X + transform.B * coordinate.Y + transform.C,
                transform.D * coordinate.X + transform.E * coordinate.Y + transform.F);

        /// <summary>Envelope 를 사각형 폴리곤으로 만듭니다. 폭/높이가 0 이면 ±0.5 로 늘려 면이 되게 합니다.</summary>
        /// <param name="envelope">원본 경계 상자.</param>
        /// <returns>닫힌 사각형 Polygon.</returns>
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

        /// <summary>레이어 1개의 지오메트리, 속성, 미지원 엔티티 목록을 모아 최종 <see cref="CadImportResult"/> 로 만드는 누적기.</summary>
        private sealed class LayerAccumulator
        {
            /// <summary>누적기를 만듭니다.</summary>
            /// <param name="name">레이어 이름.</param>
            public LayerAccumulator(string name) => Name = name;
            /// <summary>레이어 이름.</summary>
            public string Name { get; }
            /// <summary>이 레이어에 모인 지오메트리들(월드 좌표).</summary>
            public List<Geometry> Geometries { get; } = new();
            /// <summary>레이어 부가 속성(text_N, mtext_N, unsupported_entities 등). 키는 대소문자 무시.</summary>
            public Dictionary<string, string?> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>변환하지 못한 엔티티 종류 이름 목록.</summary>
            public List<string> UnsupportedEntities { get; } = new();
            /// <summary>성공적으로 변환해 추가한 엔티티 수.</summary>
            public int EntityCount { get; set; }

            /// <summary>누적 결과를 가져오기 결과 레코드로 확정합니다.</summary>
            /// <param name="factory">컬렉션/다중폴리곤을 만들 NTS 팩토리.</param>
            /// <returns>레이어 결과. 유효한 닫힌 폴리곤이 없으면 Envelope 사각형 외곽과 대체 사유 속성이 설정됨.</returns>
            public CadImportResult ToResult(GeometryFactory factory)
            {
                if (UnsupportedEntities.Count > 0)
                {
                    Attributes["unsupported_entities"] = string.Join(",", UnsupportedEntities.Distinct());
                }

                // 외곽 결정: 유효한 폴리곤이 0개면 Envelope 대체, 1개면 그대로, 여러 개면 MultiPolygon.
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

            /// <summary>Envelope 를 사각형 폴리곤으로 만듭니다(바깥 클래스 동일 메서드의 정적 버전, 폭/높이 0 은 ±0.5 확장).</summary>
            /// <param name="factory">폴리곤 생성에 쓸 팩토리.</param>
            /// <param name="envelope">원본 경계 상자.</param>
            /// <returns>닫힌 사각형 Polygon.</returns>
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

        /// <summary>
        /// 2D 아핀 변환행렬: [A B C; D E F; 0 0 1]. 블록 로컬 좌표를 월드 좌표로 옮기는 데 쓰입니다.
        /// </summary>
        /// <param name="A">X 스케일·회전 성분(x → x').</param>
        /// <param name="B">Y 가 x' 에 미치는 성분.</param>
        /// <param name="C">X 이동량.</param>
        /// <param name="D">X 가 y' 에 미치는 성분.</param>
        /// <param name="E">Y 스케일·회전 성분(y → y').</param>
        /// <param name="F">Y 이동량.</param>
        private readonly record struct Matrix2D(double A, double B, double C, double D, double E, double F)
        {
            /// <summary>항등 변환(이동/회전/스케일 없음).</summary>
            public static Matrix2D Identity => new(1, 0, 0, 0, 1, 0);

            /// <summary>INSERT 의 스케일 → 회전(도) → 삽입점 이동을 하나의 행렬로 만듭니다.</summary>
            /// <param name="insert">기준 INSERT.</param>
            /// <returns>블록 로컬 → 부모 좌표 변환행렬.</returns>
            public static Matrix2D FromInsert(Insert insert)
            {
                var angle = insert.Rotation * Math.PI / 180.0;
                var cos = Math.Cos(angle);
                var sin = Math.Sin(angle);
                return new(insert.Scale.X * cos, -insert.Scale.Y * sin, insert.Position.X,
                    insert.Scale.X * sin, insert.Scale.Y * cos, insert.Position.Y);
            }

            /// <summary>이 행렬(부모) 다음에 <paramref name="next"/>(자식)를 적용한 합성 행렬을 만듭니다.</summary>
            /// <param name="next">자식 쪽 변환(블록 안에서 먼저 적용되는 변환).</param>
            /// <returns>합성된 변환행렬.</returns>
            public Matrix2D Combine(Matrix2D next) => new(
                A * next.A + B * next.D, A * next.B + B * next.E, A * next.C + B * next.F + C,
                D * next.A + E * next.D, D * next.B + E * next.E, D * next.C + E * next.F + F);
        }
}