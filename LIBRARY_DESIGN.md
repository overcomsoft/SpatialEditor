# 장비 블록 라이브러리 · 사용자 관리 설계

작성일: 2026-10-08 / 상태: 설계 확정, **0단계(사용자·권한), 1단계(라이브러리 기반) 구현됨**, 2단계 이후 미구현

CAD 장비 형상 블록을 라이브러리로 관리하고, 배치한 인스턴스를 도면(DXF)으로 저장하는 기능과, 이를 쓰는 사용자의 등록·수정·삭제·권한 관리 기능의 설계 문서이다.

## 1. 확정된 결정

| # | 항목 | 결정 |
|---|---|---|
| 1 | 라이브러리 범위 | **전역** (도면·프로젝트와 무관한 공용 자산 하나) |
| 2 | 라이브러리 수정 시 인스턴스 | **자동으로 최신 형상으로 갱신** |
| 3 | 내보내기 형식 | **DXF만** (DWG 제외) |
| 4 | 사용자 | **사용자 관리 필요**: 등록·수정·삭제, 역할·권한 정의 |

## 2. 현재 상태와 격차

| 이미 있는 것 | 격차 |
|---|---|
| `block_definitions`: 블록 로컬 좌표 형상, `(source_file, block_name)`당 1행 | 파일별 종속. 도면 간 재사용·전역 관리 불가 |
| `spatial_block_instances`: 삽입점·회전·축척 컬럼, 월드 좌표 `geom`/`outer_geom` | 라이브러리 참조 없음(이름 기반 연결), 도면 개념 없음 |
| `Group` 도구, DXF Import, netDxf | 분류·규격 메타데이터, 버전, 내보내기 없음 |
| PostgreSQL 연결 설정 JSON, `Server` 연결 창 | **앱 사용자 로그인·권한 없음** (DB 계정만 있음), 작성자·변경 이력 없음 |

## 3. 전체 구조

```
SpatialEditor.App            WPF UI: 로그인, 라이브러리 패널, 사용자 관리, 배치/편집
SpatialEditor.Domain         User/Role/Permission, BlockLibraryItem, Drawing 등 모델
SpatialEditor.Library (신규) 라이브러리·버전·배치 서비스, 권한 검사(AuthorizationService)
SpatialEditor.Infrastructure 저장소(Npgsql), 비밀번호 해시, 감사 로그
SpatialEditor.Cad            DXF Import + DXF Export(신규, netDxf)
```

- 모든 쓰기 작업은 서비스 계층을 거치며, 서비스가 **현재 사용자의 권한을 검사**한다. UI는 권한이 없는 버튼을 비활성화할 뿐 보안 경계가 아니다.
- DB 접속 계정은 지금처럼 공용 하나이고, 앱 사용자는 DB 안의 `app_users` 테이블로 관리한다(7장 보안 한계 참조).

## 4. 데이터 모델

### 4.1 사용자·권한

```sql
app_users        (id, login_id UNIQUE, display_name, password_hash(알고리즘·반복 횟수·솔트 포함 문자열),
                  must_change_password, is_active, failed_attempts, locked_until,
                  last_login_at, created_at, created_by, updated_at)
roles            (id, name UNIQUE, description, is_system)       -- is_system: 기본 역할은 삭제 불가
role_permissions (role_id, permission_key)                      -- 역할이 가진 권한 키 집합
user_roles       (user_id, role_id)
audit_log        (id, at, user_id, action, entity_type, entity_id, summary JSONB)
```

**권한 키**

| 영역 | 키 |
|---|---|
| 사용자 | `user.manage` (사용자·역할 생성/수정/삭제, 권한 정의) |
| 라이브러리 | `library.view`, `library.create`, `library.edit`, `library.delete`, `library.publish` |
| 도면 | `drawing.view`, `drawing.edit`, `drawing.delete`, `drawing.export` |
| 데이터 | `import.dxf`, `audit.view` |

**기본 역할**(시스템 역할, 권한 구성은 관리자가 수정 가능)

| 역할 | 권한 |
|---|---|
| Admin | 전체 |
| LibraryManager | `library.*`, `drawing.view`, `drawing.export`, `import.dxf` |
| Designer | `library.view`, `drawing.*`, `import.dxf` |
| Viewer | `library.view`, `drawing.view` |

사용자 지정 역할을 만들고 권한 키를 체크해서 정의할 수 있다.

### 4.2 라이브러리

```sql
block_categories (id, parent_id, name)                           -- 분류 트리
block_library    (id, code UNIQUE, name, category_id, vendor, model_no, description,
                  width, depth, height, status,                   -- draft / released / deprecated
                  current_version, thumbnail BYTEA,
                  created_by, created_at, updated_by, updated_at, is_deleted)
block_library_versions (id, library_id, version, local_geom, local_outer_geom,
                  footprint, base_x, base_y, note, created_by, created_at)
block_ports      (id, version_id, name, port_type, local_x, local_y, direction_deg)
```

- `local_geom`: 블록 자신의 좌표계 형상(삽입점 기준). 현재 `block_definitions`와 같은 의미이다.
- `footprint`: 장비가 차지하는 점유 영역(검증용). 없으면 `local_outer_geom`을 쓴다.
- 삭제는 `is_deleted`로 논리 삭제한다. 배치된 인스턴스가 있으면 삭제를 막고 `deprecated`로 바꾸도록 안내한다.

### 4.3 도면·인스턴스

```sql
drawings (id, name, description, srid, status, created_by, created_at, updated_by, updated_at)

spatial_block_instances (확장)
  + drawing_id      -- 소속 도면
  + library_id      -- 라이브러리 참조 (NULL = 라이브러리 미연결 독립 블록)
  + tag_no, status, instance_attrs JSONB
  + created_by, updated_by
```

- 인스턴스는 **버전이 아니라 `library_id`만 참조**한다(결정 2: 항상 최신). 월드 좌표 `geom`/`outer_geom`은 기존처럼 캐시한다.

## 5. 라이브러리 기능과 필요 권한

| 기능 | 설명 | 권한 |
|---|---|---|
| 라이브러리 패널 | 분류 트리, 검색, 썸네일 목록, 규격 필터, 미리보기 | `library.view` |
| 등록 ① Import | DXF Import 때 고유 블록을 자동 추출해 등록, 형상 해시로 중복 감지 | `import.dxf` + `library.create` |
| 등록 ② 객체에서 | 선택한 인스턴스/`Group` 객체를 "라이브러리에 등록" | `library.create` |
| 등록 ③ 파일 | 블록 단독 DXF에서 등록 | `library.create` |
| 정의 편집 | 이름·분류·규격, 기준점, 점유 영역, 포트 | `library.edit` |
| 게시 | 수정한 새 버전을 확정 → **모든 인스턴스 자동 갱신**(6장) | `library.publish` |
| 이전 버전 복원 | 이전 버전 형상을 새 버전으로 게시 | `library.publish` |
| 삭제/폐기 | 논리 삭제, 사용 중이면 `deprecated` | `library.delete` |
| 배치 | 패널에서 지도로 끌어놓기/클릭 배치, 회전(R), 스냅, 거울, 배열 배치 | `drawing.edit` |
| 모델 교체 | 인스턴스를 다른 라이브러리 블록으로 교체(위치·회전 유지) | `drawing.edit` |
| 링크 해제 | 라이브러리와 연결을 끊고 독립 블록으로 | `drawing.edit` |
| 수량 집계(BOM) | 모델별 수량 CSV | `drawing.view` |
| 검증 | 점유 영역 겹침, 이격거리, 외곽(Floor) 밖 배치 검사 | `drawing.view` |

## 6. 자동 갱신 방식 (결정 2)

라이브러리 항목에 새 버전을 **게시**하면 한 트랜잭션에서 다음을 수행한다.

1. `block_library_versions`에 새 버전을 추가하고 `block_library.current_version`을 갱신한다.
2. 이 `library_id`를 참조하는 모든 인스턴스에 대해 `local_geom`에 인스턴스의 변환(축척 → 회전 → 이동)을 적용해 `geom`, `outer_geom`을 다시 계산한다(`ST_Affine`).
3. 인스턴스의 `updated_at`을 갱신하고 감사 로그에 영향받은 개수를 기록한다.

주의할 점은 다음과 같다.
- **편집 중인 사용자와의 충돌**: 기존 `updated_at` 낙관적 동시성이 그대로 동작해, 갱신 전에 편집을 시작한 사용자의 저장은 충돌로 안내된다.
- **기준점 변경**: 기준점(base)이 바뀌면 같은 삽입점이라도 형상 위치가 달라진다. 게시 전 확인 창에 "N개 인스턴스가 이동합니다"를 보여준다.
- **되돌리기**: 자동 갱신이므로 잘못 게시했을 때를 위해 이전 버전 복원(5장)을 제공한다.
- 링크 해제한 인스턴스와 `library_id`가 NULL인 블록은 갱신 대상이 아니다.

## 7. 사용자 관리 설계

### 7.1 인증
- **로그인 순서**: ① PostgreSQL 서버 연결(현재 구현됨) → ② **앱 로그인 창**(ID/비밀번호). 로그인 성공 전에는 데이터 조회·편집 기능을 비활성화한다.
- **비밀번호 저장**: 평문 금지. `PBKDF2-SHA256`(반복 횟수 210,000 이상), 사용자별 솔트로 해시해서 저장한다. 비밀번호는 JSON·로그에 남기지 않고, 로그인 창에는 마지막 ID만 기억한다.
- **계정 잠금**: 연속 5회 실패 시 15분 잠금(`locked_until`). 관리자가 해제할 수 있다.
- **최초 실행**: `app_users`가 비어 있으면 `admin` 계정을 만들고 비밀번호를 설정하게 한다. 이후 `must_change_password`로 임시 비밀번호 변경을 강제할 수 있다.
- **세션**: 앱 실행 중 `CurrentUser`를 메모리에 보관하고, 유휴 시간 제한은 선택 사항(미정).

### 7.2 사용자 관리 화면 (`user.manage` 필요)
- **사용자 목록/등록/수정**: ID, 표시 이름, 역할, 활성 여부. 비밀번호 초기화와 잠금 해제.
- **삭제**: 기본은 **비활성화**(이력의 작성자 참조 보존). 참조 이력이 전혀 없는 계정만 완전 삭제를 허용한다.
- **역할 관리**: 역할 생성/수정/삭제, 권한 키 체크박스로 **권한 정의**. 시스템 기본 역할은 삭제할 수 없다.
- **안전장치**: 마지막 남은 Admin은 삭제·비활성화·역할 박탈할 수 없다. 자기 자신을 삭제할 수 없다. 사용 중인 역할은 삭제 전에 사용자 재배정을 요구한다.

### 7.3 감사 로그
- 사용자·역할 변경, 라이브러리 등록/수정/게시/삭제, 도면 삭제, DXF Import/Export를 `audit_log`에 기록한다(누가, 언제, 무엇을, 요약).
- 라이브러리·도면·인스턴스에는 `created_by`/`updated_by`를 남긴다. `audit.view` 권한자는 목록 화면에서 조회한다.

### 7.4 보안 한계 (알고 있어야 할 점)
- 모든 앱 사용자가 **같은 PostgreSQL 계정**으로 접속하고 권한은 앱이 검사한다. DB 계정 정보를 아는 사람은 앱을 거치지 않고 직접 접속해서 권한을 우회할 수 있다.
- 접속 설정 JSON의 DB 비밀번호는 현재 평문이다. 최소한 Windows DPAPI 암호화 저장을 도입할 것을 권장한다.
- 엄격한 통제가 필요하면 후속 과제로 사용자별 PostgreSQL 롤과 행 수준 보안(RLS)을 검토한다.

## 8. DXF 내보내기 (결정 3)

- **라이브러리 블록** → DXF `BLOCK` 정의(로컬 좌표 형상, 기준점 = 삽입점). 형상 부품은 LINE/LWPOLYLINE/POINT로 쓴다.
- **인스턴스** → `INSERT`: 삽입점(`insert_x/y`), 회전(`rotation_deg`), 축척(`scale_x/y`), 레이어. `tag_no`와 인스턴스 속성은 `ATTRIB`로 쓴다.
- **라이브러리 미연결 객체**(링크 해제 블록, 레이어 집계 형상) → 객체별 고유 이름의 `BLOCK` + `INSERT`, 또는 일반 엔티티로 내보낸다(옵션).
- 좌표계는 EPSG:5186(미터) 그대로 내보내고, `$INSUNITS`는 단위(미터)를 기록한다.
- 기존 `block_handle`이 있으면 `INSERT` 핸들로 재사용해 왕복 변환의 객체 대응을 유지한다.
- **검증**: Import → Export → 재Import 후 객체 수, 삽입점, 회전, 외곽 영역이 일치하는지 자동 테스트로 확인한다(`SmartLayout.dxf` 사용). 완전 일치가 어려운 곡선 근사 항목은 허용 오차를 정한다.

## 9. 마이그레이션 (기존 데이터)

앱 시작 시 `EnsureSchema`가 멱등 SQL로 처리한다(현재 `block_definitions` 동기화 방식과 동일).

1. 새 테이블·컬럼 생성(`app_users` 등, `drawing_id`, `library_id` …).
2. 기존 `block_definitions` 행 → `block_library`(`code` = `block_name`, 분류 "미분류", 상태 `released`)와 버전 1로 이관. 같은 `block_name`이 여러 파일에 있으면 하나로 병합하고 충돌 목록을 감사 로그에 남긴다.
3. 기존 인스턴스 → `library_id` 연결, `source_file` 단위로 `drawings` 행을 만들어 `drawing_id` 연결.
4. 기본 역할·권한 키 시드, 최초 `admin` 계정 생성 절차 안내.
5. 기존 `block_definitions`는 이관 확인 전까지 읽기 전용으로 유지하고, 다음 릴리스에서 정리한다.

## 10. 단계별 로드맵

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **0. 사용자·권한** | `app_users`/`roles` 스키마, 로그인 창, 사용자·역할 관리 화면, 권한 검사 서비스, 감사 로그 기초 | admin으로 로그인해 사용자·역할 CRUD, 권한 없는 기능 차단, 마지막 Admin 보호 |
| **1. 라이브러리 기반** | 전역 라이브러리 스키마, 마이그레이션, Import 시 자동 등록, "객체에서 등록", 라이브러리 패널 | 기존 126+48 인스턴스가 라이브러리에 연결, 패널에서 검색·미리보기 |
| **2. 배치·자동 갱신** | 패널에서 배치, 모델 교체, 링크 해제, 정의 편집·게시, 인스턴스 자동 갱신, 이전 버전 복원 | 게시하면 모든 인스턴스 형상이 즉시 갱신, 충돌 안내 동작 |
| **3. 도면·내보내기** | `drawings` 관리(생성/복제/열기/스냅샷), **DXF 내보내기**, BOM CSV | 내보낸 DXF를 CAD에서 열면 블록이 INSERT로 인식, 재Import 왕복 테스트 통과 |
| **4. 검증·고급** | 포트·점유 영역 편집, 겹침·이격·외곽 검증, DPAPI 설정 암호화 | 위반 객체 목록과 지도 하이라이트 |

단계 0을 먼저 두는 이유: 이후 모든 기능(등록자 기록, 게시 권한, 감사)이 사용자 개념에 의존하기 때문이다.

## 11. 테스트 전략

- **단위 테스트**: 비밀번호 해시 검증, 권한 판정, 마지막 Admin 보호 규칙, 변환 행렬(삽입점·회전·축척 ↔ `local_geom`), DXF Export 구조(블록/INSERT 수).
- **통합 테스트(실제 PostgreSQL)**: 마이그레이션 멱등성, 게시 시 인스턴스 갱신 개수와 좌표, 낙관적 동시성 충돌.
- **왕복 테스트**: `SmartLayout.dxf` Import → Export → Import 비교.
- 현재는 DB 통합 테스트 환경이 없으므로, 테스트용 데이터베이스(예: `SD_DB_TEST`)를 준비해야 한다.

## 12. 미정 사항 / 리스크

| 항목 | 내용 |
|---|---|
| 게시 승인 절차 | `library.publish`를 별도 승인자로 둘지, 편집자가 바로 게시할지 |
| 비밀번호 정책 | 최소 길이·복잡도·만료 기간 |
| 세션 유휴 잠금 | 사용 여부와 시간 |
| 대용량 자동 갱신 | 인스턴스가 수만 개일 때 게시 시간. 배치 처리와 진행률 표시 필요 |
| 형상 해시 중복 감지 | 같은 장비의 삽입점·방향이 다른 블록을 같은 것으로 볼지 기준 |
| DB 직접 접속 우회 | 7.4 참조. 엄격한 통제가 필요하면 RLS 검토 |

## 13. 1단계 구현 내용 (2026-10-08)

- **스키마**: `block_categories`, `block_library`, `block_library_versions`(형상 해시 `shape_hash` 포함), `block_ports`(테이블만, UI 미구현), `spatial_block_instances.library_id`. `EnsureSchema`와 DXF Import 직후에 멱등 SQL로 생성·이관한다(`LibraryRepository.EnsureSchemaAsync`).
- **자동 등록(등록 ①)**: `block_definitions`의 블록 이름마다 라이브러리 항목(분류 "미분류", 버전 1)을 만들고, 같은 형상이 이미 있으면 만들지 않는다. 인스턴스는 형상 일치 → 블록 이름 순으로 `library_id`에 연결한다.
- **객체에서 등록(등록 ②)**: `Tools > Register selected block in library...`(`library.create`). 선택한 블록 인스턴스를 역변환해 로컬 형상으로 저장하고, 같은 정의를 쓰는 인스턴스를 모두 연결한다. 같은 형상이 이미 있으면 새 항목 대신 기존 항목에 연결한다. 편집 중 미저장 객체는 거부한다.
- **라이브러리 창**: `Tools > Block library...`(`library.view`). 분류 트리, 검색(코드·이름·제조사·모델), 상태 필터, 썸네일 목록, 미리보기·속성, 분류 추가, 정보 편집(`library.edit`), 삭제(`library.delete`, 사용 중이면 거부하고 `deprecated` 안내).
- **미구현**: 등록 ③(블록 단독 DXF), 배치·모델 교체·링크 해제, 정의 편집·게시·자동 갱신·버전 복원(2단계), 포트·점유 영역 편집(4단계).
- **검증**: 합성 데이터를 넣은 임시 PostgreSQL DB에서 이관 멱등성, 형상 중복 연결(회전된 인스턴스 포함), 등록, 사용 중 삭제 차단, 편집, 삭제 항목 미재생성을 확인했다. 실제 `SD_DB`에는 실행하지 않았다.
