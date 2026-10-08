# OmniDT SE (Spatial Editor) 개발 현황 및 기능 목록

기준일: 2026-10-08 / 브랜치: main

## 1. 개요

.NET 8 WPF 기반 **PostGIS 공간데이터 편집기**. CAD(DXF) 도면의 장비 블록을 PostgreSQL/PostGIS(EPSG:5186)에 적재하고,
지도 화면에서 조회·편집·저장하며, 여러 사용자가 같은 데이터를 함께 쓴다. 제품명은 **OmniDT SE (Spatial Editor)**이고
프로젝트·네임스페이스·설정 폴더 이름은 호환을 위해 `SpatialEditor.*`를 유지한다.

## 2. 솔루션 구성

| 프로젝트 | 역할 |
|---|---|
| SpatialEditor.App | WPF UI: 메인 창(지도·편집·레이어 패널·미니맵·동기화), 로그인·서버 연결·사용자/역할 관리·라이브러리·레이어 스타일 창 |
| SpatialEditor.Domain | 도메인 모델: 공간 객체, 사용자·권한(`Users.cs`), 라이브러리(`Library.cs`), 변경 기록(`ChangeFeed.cs`) |
| SpatialEditor.Geometry | NTS 기반 Point/Line/Polygon 생성·검증, PolylinePolygonizer |
| SpatialEditor.Infrastructure | Npgsql/PostGIS 리포지토리, 사용자(`UserRepository`)·비밀번호 해시(`PasswordHasher`), 라이브러리(`LibraryRepository`), 변경 기록·알림(`ChangeFeedRepository`, `ChangeListener`), 설정 저장, Import 감사 로그 |
| SpatialEditor.Cad | netDxf 기반 DXF Import (`DxfBlockImporter`) |
| SpatialEditor.Geometry.Tests / Cad.Tests / Infrastructure.Tests | 단위 테스트 (Geometry 2, Cad 4, Infrastructure 14 = 20건 통과) |
| database/001_initial_schema.sql | 초기 스키마 참고용(실제 스키마는 앱 시작 시 `EnsureSchema`가 멱등 SQL로 보장) |
| tools/dxf_to_shp.py | DXF → Shapefile/CSV 변환 보조 도구 (Python) |

## 3. 기능 목록

### 3.1 화면 구성 (QGIS 스타일)
- 메뉴 막대: File / Edit / View / Layer / Tools / Admin / Help. 항목의 활성 상태는 툴바·권한과 연동된다.
- 컬러 아이콘 툴바(벡터 아이콘 30종, 줄바꿈 지원), 하단 상태 표시줄(서버 연결, 로그인 사용자, 동기화 상태, 빌드 번호, 좌표, 확대율, 선택 수, EPSG, 진행률).
- 좌측 패널(미니맵 + 레이어 관리), 가운데 지도, 우측 속성 패널. 패널은 스플리터로 폭 조절, 메뉴로 숨김 가능.
- 서버 접속 정보는 상태 표시줄에 최소화해 표시한다. 편집 모드에서는 마우스 커서가 바뀐다.
- 프로그램 아이콘: 설비 도면을 편집한다는 의미(설계도 격자, 설비 평면, 치수선, 연필). 로그인 창 배경에도 같은 그림을 쓴다.
- **빌드 번호**: 빌드한 날짜·시각(`yyyyMMddHHmmss`)을 개발 빌드 번호로 쓰고, 상태 표시줄 오른쪽 끝과 About 창에 표시한다.

### 3.2 서버 연결·로그인
- **서버 연결 창**: Host/Port/Database/Username/Password 입력 후 접속 시험. 연결 정보는 `%LOCALAPPDATA%\SpatialEditor\postgres-connection.json`에 저장하고(비밀번호 평문), 앱 시작 시 이 정보로 자동 연결한다. 실패하면 서버 연결 창이 뜬다.
- **앱 로그인**: DB 연결 후 ID/비밀번호 로그인(마지막 ID 기억). 사용자가 없으면 첫 관리자 생성 화면이 나온다. 로그인 전에는 데이터 기능이 막힌다.
- 비밀번호: PBKDF2-SHA256(반복 횟수·솔트 포함 문자열), 5회 연속 실패 시 15분 잠금, 임시 비밀번호 변경 강제.
- **사용자·역할 관리**(`user.manage`): 사용자 등록·수정·비활성화·비밀번호 초기화·잠금 해제, 역할 생성과 권한 키 정의. 기본 역할 Admin / LibraryManager / Designer / Viewer. 마지막 Admin 보호.
- **권한 검사**: `user.manage`, `library.view/create/edit/delete/publish`, `drawing.view/edit/delete/export`, `import.dxf`, `audit.view`. 앱에서 검사한다(모든 사용자가 같은 DB 계정을 쓰는 한계는 `LIBRARY_DESIGN.md` 7.4).
- **감사 로그**(`audit_log`): 로그인, 저장, Import, 사용자·역할·라이브러리 변경을 사용자 이름과 함께 기록한다.

### 3.3 DXF Import
- `data/SmartLayout.dxf` 엔티티를 CAD 레이어별 `GeometryCollection`으로 변환, 레이어 외곽 Polygon 계산.
- **Equipment 블록 Import**: DXF `Insert` 1개 = 블록 객체 1개(126개). 구성 선분은 한 객체의 `geom`, 외곽은 `outer_geom`, 삽입점·회전·축척을 속성과 변환 컬럼으로 저장한다.
- 재Import 시 기존 데이터 삭제 확인, `Del DXF`로 수동 삭제, 진행률 표시와 취소(트랜잭션 롤백), 500행 단위 배치 INSERT.
- Import 감사 로그 `%LOCALAPPDATA%\SpatialEditor\logs\import-YYYYMMDD.log`(DXF와 DB 수량 비교, 경고, 구간별 소요 시간).

### 3.4 지도 조회
- 시작 시 레이어 목록만 조회하고 **"Select layers to load"** 창에서 불러올 레이어를 고른다(선택은 설정에 저장).
- 블록 객체는 실제 벡터 형상으로 표시한다. 선 연결이 매끄럽도록 둥근 접합·끝 처리를 쓴다.
- 줌 인/아웃, Fit, Reset, 마우스 휠(커서 기준 확대), 가운데 버튼 드래그 이동.
- **블록 전체 선택**: 블록의 어느 부분을 눌러도 블록 전체(모든 선)가 한 객체로 선택되고 금색으로 강조된다. 레이어를 숨기면 선택 강조도 사라진다.
- **미니맵**: 좌측 패널 맨 위에 전체 도면을 보여주고 현재 지도 화면 범위를 빨간 사각형으로 표시한다. 클릭·드래그하면 그 위치로 지도가 이동한다.
- 속성 패널: 객체 종류·형상·SRID·유효성과 속성 표.

### 3.5 레이어 패널
- 레이어별 표시/숨김, 색 스와치, 객체 수. 드래그로 레이어 쌓임 순서 변경, 우클릭 메뉴(해당 레이어로 확대, 스타일, 편집 등).
- **레이어 스타일**: 레이어별 선 색상·두께(고정 화면 두께). 설정 JSON에 저장되어 다음 실행에 복원된다.
- 스와치 클릭 또는 메뉴로 해당 레이어 편집 모드 진입/종료.

### 3.6 편집 모드
- 블록 객체는 **한 객체**처럼 이동(드래그, 10mm 스냅 옵션), 회전(핸들 또는 `r` 시계 90°, `R` 반시계 90°), 복제(`Dup`/Ctrl+D), 삭제(`Del`), 그룹(Ctrl+G, 여러 블록을 한 객체로).
- 다중 선택(상자 선택 등), 편집 중 객체의 전체 외곽을 금색 점선으로 표시. 원본 위치의 잔상·복사본이 보이지 않는다(편집 중에는 해당 레이어의 기본 도형을 숨기고 편집 도형만 그린다).
- **ESC**: 저장되지 않은 편집을 버리고 저장된 상태로 되돌린다.
- **장비 영역 겹침 금지**: 이동·회전·복제로 장비 외곽이 겹치면 "장비영역 겹침" 오류를 알리고 이전 위치로 되돌린다. 저장 직전에도 검사한다.
- 정점 편집(`Vtx`), 점 추가, 형상 검증. 블록 속성(삽입점·회전)은 이동·회전과 함께 갱신된다.
- `Save`: 단일 트랜잭션으로 UPDATE/INSERT/DELETE. 낙관적 동시성(`updated_at`) 충돌 객체는 건너뛰고 안내한다.

### 3.7 블록 모델
- `block_definitions`: 블록의 로컬 좌표계 형상(삽입점 기준). 인스턴스(`spatial_block_instances`)는 정의 ID, 삽입점, 회전, 축척과 월드 좌표 캐시(`geom`, `outer_geom`)를 가진다. 멱등 SQL로 동기화한다.

### 3.8 블록 라이브러리 (1단계)
- 전역 라이브러리 DB 스키마: `block_categories`, `block_library`, `block_library_versions`(형상 해시 포함), `block_ports`(테이블만), 인스턴스의 `library_id`.
- **자동 등록**: Import 후 블록 이름별로 라이브러리 항목을 만들고 배치된 인스턴스를 연결한다(같은 형상 중복 방지).
- **객체에서 등록**: `Tools > Register selected block in library...`. 같은 형상이 이미 있으면 새로 만들지 않고 연결한다.
- **라이브러리 창**(`Tools > Block library...`): 분류 트리, 검색(코드·이름·제조사·모델), 상태 필터, 썸네일 목록, 미리보기·속성, 분류 추가와 **분류 이름 변경**, 항목 편집·삭제(사용 중이면 거부하고 `deprecated` 안내).
- 미구현(2단계 이후): 단독 DXF에서 등록, 라이브러리에서 배치, 모델 교체, 링크 해제, 정의 편집·게시·인스턴스 자동 갱신, 버전 복원, 포트·점유 영역 편집, DXF 내보내기. 자세한 설계는 `LIBRARY_DESIGN.md`.

### 3.9 다중 사용자 동기화
- 저장·DXF Import·삭제 때 같은 트랜잭션에서 `change_log`에 기록하고 `NOTIFY drawing_changed`를 보낸다(롤백된 저장은 알리지 않음).
- 다른 사용자가 내가 불러온 레이어를 바꾸면 "서버 데이터가 변경되었습니다. 갱신하시겠습니까?" 창이 뜨고(누가·어느 레이어·몇 개), **예**를 누르면 해당 레이어를 다시 불러온다(줌·숨긴 레이어 유지). **아니오**는 상태 표시줄 "갱신 대기 (N건)"으로 남는다.
- 편집 모드 중에는 묻지 않고 저장/취소 후에 묻는다. 내가 저장한 변경과 불러오지 않은 레이어의 변경은 무시한다.
- 알림 연결이 끊기면 1·2·5·10·30초 간격으로 재연결하고, 연결 직후와 60초마다 놓친 변경을 확인한다. 변경 기록은 30일 지난 행을 정리한다.
- 설계와 결정은 `SYNC_PLAN.md`. 남은 과제: 변경된 객체만 다시 읽는 부분 갱신(5단계).

## 4. 개발 이력 요약

1. 초기 프로젝트, DXF Import, 지도 조회·편집, 정점 편집, 동시성 제어, Import 감사 로그(커밋 a6ca5b6)
2. 블록 객체 편집, 다중 선택·그룹, 블록 정의, Import 감사(커밋 26b1390)
3. Equipment 126개 블록 Import, 블록 전체 선택·강조, 점선 외곽, ESC 복원, 선 접합 보정, 레이어 스타일
4. 서버 연결 창·자동 연결, 사용자·역할·권한·로그인·감사 로그(라이브러리 설계 0단계)
5. QGIS 스타일 UI, 새 아이콘과 제품명 변경, 로그인 창 배경
6. 장비 겹침 금지, `r`/`R` 회전, 선택 강조 잔상·숨김 레이어 버그 수정
7. 블록 라이브러리 1단계, 분류 이름 변경
8. 다중 사용자 동기화(1~4단계), 미니맵, 빌드 번호 표시

## 5. 검증 상태

- 자동 테스트: 단위 테스트 20건 통과(Geometry 2, Cad 4, Infrastructure 14). 전체 솔루션 빌드 오류 0.
- 임시 PostgreSQL DB로 확인: 라이브러리 이관 멱등성·중복 감지·등록·삭제 차단·분류 이름 변경, 변경 기록·알림·세션 제외·레이어 필터·충돌 시 미기록.
- **직접 확인하지 못한 것**: 앱 화면을 두 개 띄운 동기화 확인 창 흐름, 알림 연결 끊김 후 재연결, 라이브러리·미니맵·메뉴 화면의 클릭 동작, 단축키·드래그 정렬·커서 등 대부분의 UI 조작. 실제 도면(`SD_DB`)에 대한 사용자·라이브러리 SQL 실행.

## 6. 남은 과제 / 제안

- 라이브러리 2단계(배치, 정의 편집·게시·자동 갱신, 버전 복원), 3단계(도면 관리, DXF 내보내기, BOM), 4단계(포트·점유 영역, 겹침·이격 검증)
- 동기화 5단계(부분 갱신)와 대형 레이어 갱신 시간 측정
- 접속 설정 JSON의 DB 비밀번호 암호화(Windows DPAPI)
- 정점 편집 결과를 상세 벡터(`geom`)에도 반영, 내부 홀이 있는 폴리곤의 정점 편집
- Import 대상 DXF 경로 고정(`data\SmartLayout.dxf`) → 파일 선택 대화상자
- 다중 사용자 동시 부하 테스트, 실제 DB에서의 마이그레이션 시험

## 7. 문서

| 문서 | 내용 |
|---|---|
| README.md | 실행·화면·조작 방법 |
| DEVELOPMENT_STATUS.md | 이 문서: 개발 현황과 기능 목록 |
| DATABASE_SCHEMA.md | 서버 데이터베이스 테이블 정의서와 ERD |
| LIBRARY_DESIGN.md | 블록 라이브러리·사용자 관리 설계와 1단계 구현 내용 |
| SYNC_PLAN.md | 다중 사용자 동기화 계획과 구현 결과 |
| SPATIAL_EDITOR_DEVELOPMENT_PLAN.md | 초기 개발 계획 |
