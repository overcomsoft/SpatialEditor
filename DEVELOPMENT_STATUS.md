# SpatialEditor 개발 현황 및 기능 목록

기준일: 2026-10-07 / 브랜치: main (초기 커밋 이후 변경분은 아직 미커밋)

## 1. 개요

.NET 8 WPF 기반 **PostGIS 공간데이터 편집기**. CAD(DXF) 도면을 레이어·블록 단위로 PostgreSQL/PostGIS(EPSG:5186)에
적재하고, 지도 화면에서 조회·편집·저장한다. (SharpMap 대신 현재는 WPF Canvas로 렌더링)

## 2. 솔루션 구성

| 프로젝트 | 역할 |
|---|---|
| SpatialEditor.App | WPF UI (MainWindow, LayerSelectionWindow), 지도 렌더링·편집·Import 흐름 |
| SpatialEditor.Domain | 도메인 모델 (SpatialFeature, EditableLayerObject, LayerInfo, ImportLayerCount, ImportProgress) |
| SpatialEditor.Geometry | NTS 기반 Point/Line/Polygon 생성·검증, PolylinePolygonizer(선분→폐합 폴리곤) |
| SpatialEditor.Infrastructure | Npgsql/PostGIS 리포지토리, 연결 설정 저장, Import 감사 로그 |
| SpatialEditor.Cad | netDxf 기반 DXF Import (DxfBlockImporter) |
| SpatialEditor.Geometry.Tests / Cad.Tests | 단위 테스트 (Geometry 2건, Cad 3건) |
| database/001_initial_schema.sql | `spatial_features`, `spatial_block_instances` 테이블 + GIST/GIN 인덱스 |
| tools/dxf_to_shp.py | DXF → Shapefile/CSV 변환 보조 도구 (Python) |

## 3. 기능 목록

### 3.1 데이터베이스 연결
- Host/Port/Database/Username/Password 입력, `Test PostgreSQL connection` (접속 + PostGIS 확장 확인)
- 연결 설정 JSON 저장/자동 로드: `%LOCALAPPDATA%\SpatialEditor\postgres-connection.json` (비밀번호 평문)
- 스키마 자동 보장(EnsureSchema)

### 3.2 DXF Import
- `data/SmartLayout.dxf` 전체 엔티티를 CAD 레이어별 `GeometryCollection`으로 변환 (`geom`)
- 레이어 외곽 Polygon(`outer_geom`, 다중 외곽은 MultiPolygon), 폐합 영역은 Polygonizer로 계산
- DXF `Insert` 1개 = `spatial_block_instances` 1행 (블록명·handle·삽입 위치·회전·축척 속성 자동 생성)
- 재Import 시 기존 데이터 삭제 여부 확인(Yes/No), `Del DXF` 버튼으로 수동 삭제
- 진행률 표시(결정적 ProgressBar), `Cancel import` 취소 시 트랜잭션 롤백
- 블록 인스턴스 500행 단위 배치 INSERT
- Import 감사 로그 `%LOCALAPPDATA%\SpatialEditor\logs\import-YYYYMMDD.log`
  - DXF vs DB 레이어/블록 수량 비교, 미지원 엔티티·외곽선 Bounding Box 폴백 `WARN`, 구간별 `DURATION`

### 3.3 지도 조회
- 시작 시 빈 화면 → 레이어 목록만 조회 후 **"Select layers to load"** 대화상자로 로드할 레이어 선택
  (선택 상태는 연결 JSON에 저장되어 다음 실행에 미리 체크)
- 블록 인스턴스는 구성 객체별(Line/Polygon/Point) 실제 벡터 형상으로 표시
- 줌 인/아웃, Fit, Reset, 마우스 휠(커서 기준 확대), 중간 버튼 드래그 이동
- 좌/우 패널 폭 조절(스플리터), 상단 툴바 약어 버튼 + 툴팁

### 3.4 레이어 패널
- `레이어명 [Point, Line, Polygon] (엔티티 수)` 표시, 체크박스로 표시/숨김
- 레이어별 고정 팔레트 색상 스와치 (지도 색상과 동일)
- 스와치 클릭 = 해당 레이어만 편집 모드 진입/종료 (미저장 변경 시 확인)

### 3.5 객체 속성
- 객체 클릭 시 `Object properties` 패널에 Type/Geometry/SRID/유효성 표시
- Attributes를 Key/Value 그리드로 표시 (블록 속성 포함)

### 3.6 편집 모드
- 이동(드래그, 10mm 스냅 옵션), 회전(핸들 클릭당 시계방향 90°), 복제(`Dup`), 삭제(`Del Obj`)
- 정점 편집(`Vtx`): 정점 드래그(타 객체 정점 스냅), 중간점 클릭으로 추가, 우클릭 삭제(최소 3정점)
  - 대상은 `outer_geom`만, 상세 `geom`은 마지막 이동·회전 위치 유지
- 선택 객체 금색 점선 / 신규 객체 초록 점선 표시
- `Save`: 단일 트랜잭션으로 UPDATE/INSERT/DELETE (출처 테이블 자동 라우팅), `Discard`로 취소
- 낙관적 동시성 제어(`updated_at` 비교): 충돌 객체만 건너뛰고 안내, 나머지는 저장

### 3.7 기타
- 점 추가(`Add Pt`), 형상 검증(`Validate`)
- DXF→SHP 변환 도구 (레이어·geometry 종류별 SHP, `block_instances.csv`, 블록 외곽 SHP)

## 4. 로그인은 어디에?

**앱 자체의 사용자 로그인(계정/권한) 기능은 없습니다.** 코드 전체에서 로그인 화면·인증·권한 처리는 발견되지 않았습니다.
유일한 "로그인" 성격의 기능은 **PostgreSQL 접속 정보 입력**입니다.

- 위치: [MainWindow.xaml:65-68](SpatialEditor.App/MainWindow.xaml#L65-L68) 좌측 패널 `PostgreSQL connection` 영역의 Username / Password 입력란
- 처리: [PostgresConnectionSettings.cs](SpatialEditor.Infrastructure/PostgresConnectionSettings.cs) (저장/로드), 연결은 [PostGisFeatureRepository.cs](SpatialEditor.Infrastructure/PostGisFeatureRepository.cs)
- 즉 인증은 DB 계정(`pg_hba.conf` 포함)에 위임되며, 앱 사용자 구분·편집 이력(작성자)·권한 관리는 미구현입니다.
- 주의: 비밀번호가 JSON에 평문 저장됩니다. 필요 시 Windows DPAPI 암호화 저장 또는 별도 로그인 화면 도입을 검토해야 합니다.

## 5. 개발 계획 진행 상태 (SPATIAL_EDITOR_DEVELOPMENT_PLAN.md)

1. Import 오류 리포트 강화 ✅
2. 편집 저장 동시성 제어 ✅
3. Import 진행률·취소 + 일괄 INSERT ✅
4. 정점 단위 편집 + 스냅 ✅
5. 성능 가시성(Import 소요 시간 로그) ✅

## 6. 남은 과제 / 제안

- 정점 편집 결과를 상세 벡터(`geom`)에도 반영하는 재구성 로직
- 내부 홀이 있는 폴리곤의 정점 편집
- 다중 사용자 동시 부하 테스트
- **앱 로그인/사용자·권한 관리, 비밀번호 암호화 저장** (현재 미구현)
- Import 대상 DXF 경로가 `D:\DINNO\DEV\SD2D\data\SmartLayout.dxf`로 고정 → 파일 선택 대화상자 필요
- 변경분 미커밋 상태 → 커밋 필요
- SharpMap 호환성 검증 후 도입 여부 결정
