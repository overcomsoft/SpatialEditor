# Spatial Editor

.NET 8 WPF 기반 PostGIS 공간데이터 편집기 초기 프로젝트입니다.

## 구성

- `SpatialEditor.App`: WPF 작업 화면과 로컬 geometry 미리보기
- `SpatialEditor.Domain`: 공간 feature 도메인 모델
- `SpatialEditor.Geometry`: NTS 기반 Point, LineString, Polygon 생성 및 검증
- `SpatialEditor.Infrastructure`: Npgsql/Npgsql.NetTopologySuite 기반 PostGIS BBOX 조회
- `SpatialEditor.Cad`: netDxf 기반 DXF 전체 엔티티의 레이어별 형상 Import
- `SpatialEditor.Geometry.Tests`: geometry 단위 테스트
- `database/001_initial_schema.sql`: EPSG:5186 공간 feature 초기 스키마

## 실행

```powershell
dotnet build SpatialEditor.sln
dotnet test SpatialEditor.Geometry.Tests/SpatialEditor.Geometry.Tests.csproj
dotnet run --project SpatialEditor.App
```

## PostgreSQL 연결 설정

앱의 `PostgreSQL connection` 영역에서 Host, Port, Database, Username, Password를 입력할 수 있습니다.
`Save connection JSON`을 누르면 다음 사용자 전용 경로에 저장됩니다.

```text
%LOCALAPPDATA%\SpatialEditor\postgres-connection.json
```

앱을 다시 시작하면 저장된 설정을 자동으로 불러옵니다. JSON에는 비밀번호가 평문으로 저장되므로
Windows 사용자 계정 파일 권한을 유지하고 공유 폴더에는 저장하지 않아야 합니다.
`Test PostgreSQL connection`은 PostgreSQL 접속과 PostGIS 확장 설치 여부를 함께 확인합니다.

앱을 시작하면 초기 화면에는 아무 형상도 그려지지 않습니다(과거의 샘플 폴리곤 미리보기는 제거되었습니다).
저장된 PostgreSQL 연결정보에 비밀번호가 있으면 먼저 DB에서 레이어 목록만 가볍게 조회한 뒤
**"Select layers to load"** 대화상자를 띄워 불러올 레이어를 체크박스로 고를 수 있습니다.
`OK`를 누른 레이어만 실제 형상 데이터를 조회해 지도에 표시하며, 이 선택은
`postgres-connection.json`에 저장되어 다음 실행 때 그대로 미리 체크되어 나타나므로
이후에는 확인(OK)만 누르면 이전과 동일한 레이어 구성을 바로 불러올 수 있습니다.
데이터베이스 연결 실패나 저장 데이터가 없는 경우, 또는 대화상자에서 `Cancel`을 누른 경우에도
앱은 실행되며 상태 메시지로 원인을 표시합니다.
블록 인스턴스가 저장된 레이어는 `spatial_block_instances.geom`을 구성 객체별로 읽어
내부 LineString, Polygon, Point를 실제 벡터 도형으로 표시합니다. 따라서 `equipment`처럼
많은 선으로 구성된 블록도 외곽선 하나가 아닌 개별 블록 형상으로 확인할 수 있습니다.
DXF를 새로 Import하면 방금 들어온 데이터를 바로 확인할 수 있도록 레이어 선택과 무관하게
전체 레이어를 다시 불러옵니다(저장된 레이어 선택 자체는 변경되지 않습니다).

## 지도 조작

상단 툴바에 지도 조작, Import/삭제, 편집 모드 버튼이 한 줄로 모여 있습니다(버튼이 많아지면 툴바 자체가
가로 스크롤됩니다). 각 버튼은 약어로 표기되며 마우스를 올리면 전체 이름이 툴팁으로 표시됩니다.

- `Zoom in` / `Zoom out` / `Fit` / `Reset`: 지도 확대·축소·범위 복원·초기화
- `Import` / `Cancel` / `Del DXF` / `Add Pt` / `Validate`: DXF Import, Import 취소, 삭제, 점 추가, 형상 검증
- `Edit` / `Dup` / `Del Obj` / `Vtx: Off` / `Snap 10mm` / `Save` / `Discard`: 전체 레이어 편집 모드 진입·종료, 복제, 삭제, 정점 편집 토글, 이동 시 10mm 격자 스냅, 저장, 취소
- 마우스 휠: 커서 위치 기준 확대·축소
- 마우스 중간 버튼 드래그: 지도 이동
- Polygon 클릭: 오른쪽 `Object properties` 패널에 ID, Geometry, SRID, 유효성, Attribute 표시

좌측 패널은 PostgreSQL 연결 정보와 레이어 목록만 담아 스크롤 없이 한 화면에 보이도록 구성했습니다.
좌우 패널과 지도 사이의 경계선을 드래그하면 폭을 조절할 수 있습니다.

## 레이어 패널

DXF Import 후 앱을 다시 시작하면 DB에서 레이어 목록을 조회하여
`레이어명 [Point, Line, Polygon] (엔티티 수)` 형식으로 표시합니다.
레이어 체크박스를 해제하면 해당 레이어의 지도 형상이 숨겨지고 다시 선택하면 표시됩니다.

각 레이어는 고정 팔레트에서 순서대로 배정된 고유 색상을 가지며, 체크박스 앞의 사각형 스와치가
지도에 실제로 그려지는 Fill/Line 색상을 그대로 보여줍니다(QGIS 레이어 패널의 범례 아이콘과 동일한 방식).

레이어 색상 스와치를 클릭하면 그 레이어만 편집 모드로 들어갑니다(상단 툴바의 `Edit` 버튼은
모든 레이어를 한꺼번에 편집 모드로 엽니다). 편집 중인 레이어의 스와치는 점선 테두리로 표시되고,
같은 스와치를 다시 클릭하면 편집 모드를 종료합니다. 편집 중 저장하지 않은 변경 사항이 있는 상태로
다른 레이어의 스와치를 클릭하면 변경 사항을 버릴지 확인합니다.

## SmartLayout Import

1. `database/001_initial_schema.sql`을 `dinno` 데이터베이스에서 실행합니다.
2. 앱에서 PostgreSQL 연결정보를 입력하고 `Test PostgreSQL connection`으로 확인합니다.

```powershell
dotnet run --project SpatialEditor.App
```

화면 상단 툴바의 `Import` 버튼을 누르면
`D:\DINNO\DEV\SD2D\data\SmartLayout.dxf`의 전체 엔티티를 읽어 CAD 레이어별로 묶습니다.
변환된 실제 형상은 레이어 단위 `GeometryCollection`으로 `geom`에, 외곽 Polygon은 `outer_geom`에,
블록 인스턴스 수량이 필요한 레이어는 `spatial_block_instances`에도 저장합니다.
이 테이블은 DXF의 `Insert` 1개당 1행이며, `equipment` 수량은 다음 쿼리로 확인할 수 있습니다.

```sql
SELECT layer_name, COUNT(*) AS block_instance_count
FROM spatial_block_instances
WHERE layer_name = 'equipment'
GROUP BY layer_name;
```

Import가 끝나면 DXF 레이어별 엔티티 수와 블록 인스턴스 수를 PostgreSQL과 비교한 로그가 다음 위치에 기록됩니다.

```text
%LOCALAPPDATA%\SpatialEditor\logs\import-YYYYMMDD.log
```

레이어/블록 인스턴스 단위로 변환하지 못한 엔티티가 있거나 폐합 영역을 찾지 못해 외곽선을 Bounding Box로 대체한 경우
`WARN` 라인으로 원인(미지원 엔티티 종류, Polygonizer의 dangle/cut-edge/invalid-ring 수)이 함께 기록됩니다.
Import 전체 소요 시간(DXF 파싱/DB 저장 구간별)도 `DURATION` 라인으로 남습니다.

툴바의 `Equip only` 체크박스를 켜고 `Import`하면 `Equipment` 레이어의 INSERT 126개만 블록 객체로 저장합니다(INSERT 1개 = 객체 1개).
다른 레이어, 낱개 엔티티, 레이어 집계 행(`spatial_features`)은 저장하지 않습니다.

Import 중에는 `Cancel import` 버튼으로 취소할 수 있으며, 취소 시 트랜잭션이 롤백되어 부분 데이터가 남지 않습니다.
블록 인스턴스는 개별 INSERT 대신 다중 행 배치(500행 단위)로 저장해 대량 데이터에서도 빠르게 반영됩니다.

기존 `SmartLayout.dxf` 데이터가 있으면 Import 전에 삭제 여부를 `Yes/No`로 확인합니다.
`Yes`를 선택하면 해당 DXF에 연결된 모든 레이어 집계와 블록 인스턴스를 삭제한 뒤 새 데이터를 저장합니다.
또한 상단 툴바의 `Del DXF` 버튼으로 동일한 삭제 작업을 수동 실행할 수 있습니다.

레이어명·원본 파일·문자 Attribute는 별도 컬럼과 JSONB에 저장합니다. 블록 Insert는 내부 엔티티를
삽입 위치·회전·축척으로 변환한 뒤 해당 엔티티의 레이어에 포함합니다.
한 레이어에 여러 폐합 외곽선이 있으면 `outer_geom`에 `MultiPolygon`으로 저장하고,
프로그램 시작 시 `ST_Dump`으로 각각 읽어 화면에 표시합니다. 같은 DXF를 다시 Import하면
기존 해당 파일 결과를 먼저 삭제하여 이전 Convex Hull 결과와 중복 데이터가 남지 않습니다.

연결 오류가 발생하면 먼저 Host/Port, 데이터베이스명, 사용자명, 비밀번호와 PostgreSQL의
`pg_hba.conf` 인증 설정을 확인합니다. Import는 연결 테스트와 동일한 저장 설정을 사용합니다.

## 편집 모드

상단 툴바의 `Edit` 버튼(또는 레이어 색상 스와치 클릭)으로 `spatial_features`에 저장된 폴리곤 객체(DXF 레이어 외곽선)와
`spatial_block_instances`에 저장된 개별 블록 인스턴스(장비 등)를 함께 편집할 수 있습니다. 블록 인스턴스는 실제 도면 형상
(`geom`, LineString/Polygon/Point 구성요소)과 바운딩 외곽선(`outer_geom`)을 같은 행에 함께 가지고 있으므로, 편집 모드에
들어가면 그 실제 형상이 얇은 레이어 색상 선으로 그대로 표시되고, 이동·회전 시 바운딩 외곽선과 완전히 같이 움직입니다
(선택·드래그 히트테스트는 바운딩 외곽선이 담당하고, 실제 형상은 시각적으로만 따라옵니다).

현재 선택되어 편집 중인 객체는 금색 점선 외곽선으로, 아직 저장되지 않은 신규(복제) 객체는 초록 점선으로 표시되어
실선(일반 편집 대상)과 구분됩니다.

- **이동**: 객체를 드래그하면 전체가 평행이동합니다. 상단 툴바의 `Snap 10mm` 체크박스를 켜면 이동 후 위치(외곽선
  중심)가 10mm 격자에 자동으로 맞춰집니다(드래그 중 미리보기에도 동일하게 반영).
- **회전**: 선택 시 나타나는 금색 핸들을 클릭할 때마다 객체 중심 기준으로 시계방향 90도씩 회전합니다.
- **추가**: `Dup`(Duplicate selected)으로 선택한 객체를 복제해 새 객체를 만들고 원하는 위치로 옮깁니다.
- **삭제**: `Del Obj`(Delete selected)로 객체를 삭제 대상으로 표시합니다.
- **정점 편집**: `Vtx: Off/On`(Vertex edit)을 켜면 외곽선의 각 정점에 사각 핸들이, 변 중간에는 초록 점(중간점)이 표시됩니다.
  정점을 드래그해 이동(다른 객체 정점 근처에서는 자동 스냅), 중간점 클릭으로 정점 추가, 정점 우클릭으로 삭제(최소 3정점 유지)할 수 있습니다.
  정점 편집은 대표 외곽선(`outer_geom`)만 바꾸며, 실제 형상(`geom`)은 마지막 이동·회전 위치를 그대로 유지합니다.

- **다중 선택**: `Ctrl`+클릭으로 객체를 선택에 추가/제외하고, 빈 곳을 드래그하면 선택 박스(박스 안에 완전히 들어온 객체만 선택,
  `Ctrl`을 누르면 기존 선택에 추가)가 그려집니다. 선택된 객체 중 하나를 드래그하면 전체가 함께 이동하고, 금색 핸들(선택 전체 상단)을
  클릭하면 선택 전체가 공통 중심 기준으로 90도 회전합니다. `Dup`/`Del Obj`도 선택 전체에 적용됩니다(정점 편집은 단일 선택 전용).
- **그룹(블록 생성)**: 2개 이상 선택 후 `Group`을 누르면 선택한 객체들이 하나의 새 블록 인스턴스(`GROUP_...`)로 합쳐지고 원본은
  삭제 대상이 됩니다. `Save` 후에는 한 객체처럼 이동·회전·삭제되며 자체 `block_definitions` 행도 생성됩니다.

이동·회전·추가·삭제·정점 편집은 모두 메모리에만 쌓이며, `Save`(Save changes)를 눌러야 PostgreSQL에 한 번에 반영됩니다
(트랜잭션으로 대상 테이블에 UPDATE/INSERT/DELETE 처리 — 객체 출처에 따라 `spatial_features` 또는
`spatial_block_instances`로 자동 라우팅됩니다). `Discard`(Discard changes)나 편집 모드를 다시 끄면 DB에 아무 영향 없이 취소됩니다.

저장 시점에 다른 세션이 같은 객체를 먼저 바꿔놓았다면(낙관적 동시성 제어, `updated_at` 비교) 해당 객체만 덮어쓰지 않고
충돌로 안내합니다 — 나머지 변경 사항은 정상 저장됩니다.

객체를 클릭하면 오른쪽 `Object properties` 패널에 Type/Geometry/SRID/Valid 요약과 함께, Attributes가
Key/Value 2컬럼의 데이터 그리드로 표시됩니다. DXF Insert에서 온 블록 객체는 `block_name`(블록 정의 이름),
`block_handle`(DXF 문서 내 고유 코드), `block_insert_x/y`, `block_rotation_deg`, `block_scale_x/y`가
Import 시 항상 자동 생성되어 이 그리드에 표시됩니다.

## 다음 단계

1. 편집 모드의 정점 편집을 상세 벡터(`geom`)에도 반영하는 재구성 로직
2. 정점 편집에서 내부 홀(구멍)이 있는 폴리곤 지원
3. 다중 사용자 동시 부하 성능 테스트(현재는 단일 세션 기준으로만 확인)

SharpMap은 WPF/.NET 8 호환성 검증 후 별도 추가합니다. 현재 UI는 WPF Canvas로 기본 동작을 검증할 수 있게 구성되어 있습니다.