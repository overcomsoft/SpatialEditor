`SPATIAL_EDITOR_DEVELOPMENT_PLAN.md` 파일로 바로 복사하여 저장할 수 있는 마크다운 전체 내용입니다.

```markdown
# C# 기반 PostGIS 공간데이터 편집기 개발 계획서

본 문서는 CAD 도면(DXF) 내 블록 객체의 외곽선(Polygon)과 속성을 추출하여 PostgreSQL/PostGIS에 적재하고, C/S 환경에서 점·선·면 공간 데이터를 조회·편집·저장하는 데스크톱 GIS 시스템 구축 계획서입니다.

---

## 1. 프로젝트 개요

* **프로젝트명:** C# WPF 기반 PostGIS 공간데이터 편집기 구축
* **목표:**
  * CAD 블록 객체(Block Reference)의 외곽선 추출 및 NTS Polygon 변환
  * CAD 블록 속성(Attributes)의 JSONB 매핑 및 PostGIS 적재
  * C/S 데스크톱 환경에서 PostGIS 공간 객체 조회, 렌더링 및 편집
  * R-Tree 기반 실시간 버텍스 스냅(Snapping) 및 러버밴드 인터랙션 지원
* **타깃 플랫폼:** Windows 10/11 (.NET 8 Desktop Runtime)
* **데이터베이스:** PostgreSQL 15+ / PostGIS 3+

---

## 2. 시스템 아키텍처

계층화 아키텍처(Layered Architecture)를 기반으로 UI, GIS 엔진, 데이터 접근 계층을 분리합니다.


```

┌────────────────────────────────────────────────────────┐
│                   Presentation Layer                   │
│   WPF UI / WindowsFormsHost (SharpMap MapBox)          │
│   (지도 캔버스, 러버밴드 렌더링, 버텍스 조작 툴바, 속성 패널)  │
└───────────────────────────┬────────────────────────────┘
│
┌───────────────────────────▼────────────────────────────┐
│                    GIS Engine Layer                    │
│   - SharpMap: 뷰포트 관리(Pan/Zoom), 레이어 렌더링     │
│   - NTS (NetTopologySuite): 기하 생성/검증/토폴로지 연산 │
│   - Snapping Engine: STRtree 기반 실시간 버텍스 자석 흡착  │
│   - CAD Parser (netDxf): 블록 추출 및 아핀 변환 행렬 연산 │
└───────────────────────────┬────────────────────────────┘
│
┌───────────────────────────▼────────────────────────────┐
│                  Data Access Layer                     │
│   - Npgsql + Npgsql.NetTopologySuite                   │
│   - 트랜잭션 관리, BBOX 공간 쿼리, JSONB 속성 직렬화    │
└───────────────────────────┬────────────────────────────┘
│ (TCP/IP 5432)
┌───────────────────────────▼────────────────────────────┐
│                     Database Layer                     │
│   PostgreSQL / PostGIS (Spatial Index GIST 적용)       │
└────────────────────────────────────────────────────────┘

```

---

## 3. 기술 스택 및 오픈소스 라이브러리

| 구분 | 기술 / 라이브러리 | 버전 | 용도 및 선정 사유 |
| :--- | :--- | :--- | :--- |
| **Framework** | .NET 8 (WPF) | 8.0 | 데스크톱 C/S 클라이언트 UI 구축 |
| **GIS Viewer** | **SharpMap** (`SharpMap.UI`) | 최신 안정화 | 지도 뷰포트(Zoom/Pan), 좌표 투영, 레이어 렌더링 캔버스 |
| **Geometry** | **NetTopologySuite (NTS)** | 최신 안정화 | OGC 표준 기하 연산, WKT/WKB 처리, R-Tree 인덱싱(`STRtree`) |
| **CAD Parser** | **netDxf** | 최신 안정화 | DXF 파일 로딩, 블록 인스턴스(`Insert`) 및 속성(`Attribute`) 추출 |
| **DB Driver** | **Npgsql** / `Npgsql.NetTopologySuite` | 최신 안정화 | PostgreSQL 연결 및 NTS Geometry - PostGIS 바이너리 자동 매핑 |
| **Database** | **PostgreSQL + PostGIS** | 15+ / 3+ | 점, 선, 면 기하 데이터 및 비정형 JSONB 속성 저장소 |

---

## 4. 데이터베이스 스키마 설계

CAD에서 추출한 공간 데이터와 동적 속성을 보관하기 위한 DDL 스키마입니다.

```sql
-- PostGIS 확장 설치
CREATE EXTENSION IF NOT EXISTS postgis;

-- CAD 블록 객체 저장 테이블
CREATE TABLE cad_block_features (
    id SERIAL PRIMARY KEY,
    block_name VARCHAR(100) NOT NULL,               -- CAD 블록 정의명
    layer VARCHAR(100),                              -- CAD 원본 레이어명
    attributes JSONB,                                -- 블록 속성 (Key-Value 매핑)
    geom GEOMETRY(Polygon, 5186) NOT NULL,           -- 평면직각좌표계 (예: EPSG:5186)
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- 공간 인덱스 및 JSONB 인덱스 설정
CREATE INDEX idx_cad_block_geom ON cad_block_features USING GIST(geom);
CREATE INDEX idx_cad_block_attr ON cad_block_features USING GIN(attributes);

```

---

## 5. 상세 기능 명세 (Feature Breakdown)

### 5.1 CAD 데이터 추출 및 적재 (CAD Importer)

* **아핀 변환(Affine Transformation):** 블록 정의 내부의 로컬 엔티티(Line, LwPolyline)에 삽입점(Position), 회전각(Rotation), 축척(Scale)을 적용하여 월드 좌표로 변환.
* **외곽선 폴리곤화:** 분절된 선분을 NTS `Polygonizer`로 조합하여 유효한 폐합 `Polygon` 생성.
* **속성 매핑:** 블록 내 태그/속성(`Attribute`)을 Dictionary 형태로 추출 후 `JSONB` 컬럼에 직렬화 저장.

### 5.2 지도 렌더링 및 뷰포트 제어

* **멀티 레이어 렌더링:**
1. *PostGIS VectorLayer:* DB에 저장된 실제 공간 객체 레이어.
2. *Sketch Layer:* 버텍스 추가 중 표시되는 실시간 가이드라인(점선) 및 점 레이어.
3. *Snap Indicator Layer:* 스냅 흡착 시 시각적 피드백을 주는 강조 마커 레이어.


* **뷰포트 조작:** 마우스 휠 줌(Zoom), 드래그 이동(Pan), 전체 영역 맞춤(Zoom to Extents).

### 5.3 공간 객체 인터랙티브 편집기

* **점(Point) 추가:** 클릭 위치 좌표를 기반으로 Point 객체 생성 및 DB 반영.
* **선(LineString) / 면(Polygon) 그리기:**
* 좌클릭: 연속 버텍스 추가.
* 마우스 이동: 마지막 버텍스와 현재 커서를 잇는 실시간 러버밴드(Rubber-banding) 렌더링.
* 우클릭: 기하 완성 검증 후 DB 트랜잭션 INSERT.


* **스냅(Snapping) 엔진:**
* PostGIS 객체들의 모든 버텍스를 NTS `STRtree`에 인덱싱.
* 화면 픽셀 오차(Tolerance)를 지리 좌표 거리로 변환하여 반경 내 가장 가까운 버텍스 탐색 ($O(\log N)$).



### 5.4 속성 조회 및 수정

* **객체 피킹(Picking):** 마우스 클릭 지점 기준 BBOX 인터섹션 쿼리로 대상 객체 식별.
* **WKT 및 속성 편집:** 선택된 객체의 WKT 텍스트 및 기본 속성을 UI 폼에서 직접 수정한 후 PostGIS UPDATE 실행.

---

## 6. 개발 로드맵 및 일정 계획

```
[1단계: 기반 구축] (1~2주차)
  - PostgreSQL/PostGIS 환경 구성 및 DDL 배포
  - C# WPF 프로젝트 구성 및 Npgsql + NTS 연동 단위 테스트
  - SharpMap WindowsFormsHost 연동 및 뷰포트 제어 검증

[2단계: CAD 임포터 구현] (3~4주차)
  - netDxf 기반 블록 엔티티 로딩 및 아핀 변환 연산
  - NTS Polygonizer를 이용한 폐합 외곽선 생성 로직 구현
  - 속성 정보 JSONB 적재 및 대량 INSERT 트랜잭션 최적화

[3단계: 지도 렌더링 & 편집기 구축] (5~6주차)
  - PostGIS VectorLayer 렌더링 파이프라인 연결
  - 선(LineString), 면(Polygon) 인터랙티브 러버밴드 드로잉 도구 구현
  - STRtree 기반 버텍스 스냅(Snapping) 엔진 탑재

[4단계: 속성 관리 및 안정화] (7~8주차)
  - 객체 피킹(Selection) 및 UI 속성 그리드 연동
  - WKT/Geometry 수정사항 DB 역반영 및 예외 처리
  - 대용량 데이터 로딩 성능 튜닝 및 최종 패키징 배포

```

```

```