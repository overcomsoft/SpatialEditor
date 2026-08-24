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

앱을 다시 시작하면 저장된 PostgreSQL 연결정보로 `spatial_features`에서
`feature_type = 'cad_block'`이고 `outer_geom`이 있는 레코드를 자동 조회합니다.
조회된 DXF 외곽 Polygon은 전체 범위에 맞춰 지도 화면에 복원됩니다.
데이터베이스 연결 실패나 저장 데이터가 없는 경우에도 앱은 실행되며 상태 메시지로 원인을 표시합니다.

## 지도 조작

- 상단 `Zoom in`, `Zoom out`: 지도 확대·축소
- 상단 `Fit`: 현재 지도 범위로 복원
- 상단 `Reset view`: 확대·이동 상태를 초기화
- 마우스 휠: 커서 위치 기준 확대·축소
- 마우스 중간 버튼 드래그: 지도 이동
- Polygon 클릭: 오른쪽 `Object properties` 패널에 ID, Geometry, SRID, 유효성, Attribute 표시

## SmartLayout Import

1. `database/001_initial_schema.sql`을 `dinno` 데이터베이스에서 실행합니다.
2. 앱에서 PostgreSQL 연결정보를 입력하고 `Test PostgreSQL connection`으로 확인합니다.

```powershell
dotnet run --project SpatialEditor.App
```

화면에서 `Import SmartLayout.dxf` 버튼을 누르면
`D:\DINNO\DEV\SD2D\data\SmartLayout.dxf`의 전체 엔티티를 읽어 CAD 레이어별로 묶습니다.
변환된 실제 형상은 레이어 단위 `GeometryCollection`으로 `geom`에, 외곽 Polygon은 `outer_geom`에,
레이어명·원본 파일·문자 Attribute는 별도 컬럼과 JSONB에 저장합니다. 블록 Insert는 내부 엔티티를
삽입 위치·회전·축척으로 변환한 뒤 해당 엔티티의 레이어에 포함합니다.

연결 오류가 발생하면 먼저 Host/Port, 데이터베이스명, 사용자명, 비밀번호와 PostgreSQL의
`pg_hba.conf` 인증 설정을 확인합니다. Import는 연결 테스트와 동일한 저장 설정을 사용합니다.

## 다음 단계

1. Arc/Circle/Spline을 포함한 곡선 엔티티 변환
2. Polygonizer 기반 외곽선 생성 및 Import 오류 리포트
3. 일괄 INSERT, Import 취소·진행률, 중복 Import 방지
4. 지도 좌표계 기반 Pan/Zoom, 선택, 스냅, 러버밴드 편집
5. 동시성 제어와 성능 테스트

SharpMap은 WPF/.NET 8 호환성 검증 후 별도 추가합니다. 현재 UI는 WPF Canvas로 기본 동작을 검증할 수 있게 구성되어 있습니다.