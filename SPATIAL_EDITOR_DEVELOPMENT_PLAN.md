# SpatialEditor 다음 단계 개발 계획

README "다음 단계"에 남아 있던 5개 항목을 실제 코드 상태와 대조해, 비어 있는 부분만 추려 우선순위대로 정리한다.
실제 운영 데이터(`%LOCALAPPDATA%\SpatialEditor\logs\import-*.log`)를 보면 `Equipment` 레이어 44,436개 엔티티·126개 블록,
`E-CABL-TRAY` 15,024개 엔티티·48개 블록 규모로 이미 쓰이고 있어, 진행률·성능·오류 가시성이 실제로 필요한 상황이다.

**진행 상태: 5개 항목 모두 구현 완료.** 남은 후속 과제는 README "다음 단계"를 참고.

## 진행 순서

1. ~~Import 오류 리포트 강화~~ ✅
2. ~~편집 저장 동시성 제어~~ ✅
3. ~~Import 진행률·취소 + 일괄 INSERT 성능 (함께 구현)~~ ✅
4. ~~정점 단위 편집 + 스냅~~ ✅
5. ~~성능 가시성 (축소된 범위)~~ ✅ (Import 소요 시간 로그로 구현, 다중 사용자 부하 테스트는 범위 밖으로 유지)

## 1. Import 오류 리포트 강화

**현재 상태**: `DxfBlockImporter`(`SpatialEditor.Cad/DxfBlockImporter.cs`)는 레이어 단위로만 `UnsupportedEntities`를 모아
`attributes["unsupported_entities"]`에 넣지만, **블록 인스턴스 단위**(`ImportBlockInstances`/`CollectInsertGeometry`)는
변환 실패 엔티티를 그냥 버리고 아무 기록도 남기지 않는다. `CreatePolygonizedOuterGeometry`가 폐합 영역을 못 찾아 Envelope로
대체할 때도 완전히 조용히 넘어간다. `ImportAuditLogger`에는 레이어/블록 개수 일치 여부만 남고 이 내용은 전혀 기록되지 않는다.

**작업**:

- `CollectInsertGeometry`에도 레이어와 동일하게 미지원 엔티티 타입을 모아 `CadBlockInstanceResult.Attributes["unsupported_entities"]`로 보존.
- `CreatePolygonizedOuterGeometry`가 Envelope로 폴백할 때 NTS `Polygonizer`의 진단 정보(`GetDangles()`/`GetCutEdges()`/`GetInvalidRingLines()`)를
  얻어 결과 레코드에 `OuterGeometryIsFallback: bool`로 반영.
- `MainWindow.LogImportCounts`를 확장해 레이어/블록별 미지원 엔티티와 폴백 여부를 `WARN` 라인으로 `ImportAuditLogger`에 기록.
- 대상 파일: `SpatialEditor.Cad/DxfBlockImporter.cs`, `SpatialEditor.Cad/CadImportResult.cs`, `SpatialEditor.Cad/CadBlockInstanceResult.cs`,
  `SpatialEditor.App/MainWindow.xaml.cs`.

## 2. 편집 저장 동시성 제어

**현재 상태**: `ApplyLayerEditsAsync`(`SpatialEditor.Infrastructure/PostGisFeatureRepository.cs`)의 UPDATE는 `WHERE id = $3`만
검사해, 편집 모드 진입 후 다른 세션이 같은 행을 바꿔도 무조건 덮어쓴다.

**작업**:

- `EditableLayerObject`(`SpatialEditor.Domain/SpatialFeature.cs`)에 `UpdatedAt` 추가, `FindEditableLayerObjectsAsync`가 함께 조회.
- `ApplyLayerEditsAsync`의 UPDATE/DELETE에 `AND updated_at = $n` 조건을 추가하고 영향받은 행 수를 확인, 0건이면 충돌로 보고.
- `MainWindow.SaveEdits_Click`에서 충돌 시 자동 덮어쓰기 대신 사용자에게 안내.
- 대상 파일: `SpatialEditor.Domain/SpatialFeature.cs`, `SpatialEditor.Infrastructure/PostGisFeatureRepository.cs`,
  `SpatialEditor.App/MainWindow.xaml.cs`.

## 3. Import 진행률·취소 + 일괄 INSERT 성능

**현재 상태**: `ImportDxf_Click`이 레이어/블록마다 개별 INSERT를 순차 `await`하며, `BusyProgressBar`는 `IsIndeterminate`라
진행률이 없고 취소 수단도 없다. 두 항목 모두 같은 쓰기 루프를 건드리므로 함께 구현한다.

**작업**:

- 새 리포지토리 메서드 `ImportAsync(layers, instances, sourceFile, IProgress<ImportProgress>, CancellationToken)`을 추가해
  하나의 트랜잭션 안에서 블록 인스턴스는 다중 행 INSERT로 배치 저장하고, 진행률 보고·취소 체크·오류 시 롤백을 처리.
- `SpatialEditor.Domain`에 `ImportProgress(string Stage, int Completed, int Total)` 레코드 추가.
- `MainWindow.xaml`에 "Cancel import" 버튼 추가, 진행률 바를 결정적(determinate) 모드로 전환.
- 대상 파일: `SpatialEditor.Infrastructure/PostGisFeatureRepository.cs`, `SpatialEditor.Domain`, `SpatialEditor.App/MainWindow.xaml(.cs)`.

## 4. 정점 단위 편집 + 스냅

**현재 상태**: 편집 모드(이동·회전·복제·삭제)는 객체 전체 단위다. README가 원하는 "러버밴드 편집"은 개별 정점을 드래그해
형상 자체를 바꾸는 것이다.

**작업**:

- 선택된 객체에 "Vertex edit" 토글 → 외곽선(`outer_geom`) 각 정점에 핸들 표시.
- 정점 드래그 이동 + 화면 10px 이내 다른 정점으로 스냅.
- 인접 정점 중간점 더블클릭으로 정점 추가, 우클릭으로 삭제(최소 3정점 유지).
- **범위 제한**: `outer_geom`(대표 외곽선)만 정점 편집 대상. 상세 벡터(`geom`)는 재구성하지 않고 마지막 강체 변환 위치 유지.
- 대상 파일: `SpatialEditor.App/MainWindow.xaml(.cs)`.

## 5. 성능 가시성 (축소된 범위)

별도 벤치마크 하네스 대신, 3번에서 만든 `ImportAsync`의 전체/배치별 소요 시간을 `ImportAuditLogger`에 `DURATION` 라인으로 기록한다.
다중 사용자 동시 부하 테스트는 현재 도구 범위 밖이라 제외하고, 필요해지면 별도로 논의한다.

## 검증(공통)

- 매 항목 후 `dotnet build SpatialEditor.sln`, `dotnet test SpatialEditor.Geometry.Tests`, `dotnet test SpatialEditor.Cad.Tests`.
- 최종적으로 `dotnet run --project SpatialEditor.App`으로 PostgreSQL 연결 후 재-Import, 편집 모드(이동/회전/정점 편집), 저장/충돌
  시나리오를 수동 확인.
