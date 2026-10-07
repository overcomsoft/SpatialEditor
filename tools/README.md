# DXF to SHP

## 설치

```powershell
python -m pip install -r tools/requirements.txt
```

## 실행

```powershell
python tools/dxf_to_shp.py data/SmartLayout.dxf data/SmartLayout_shp --crs EPSG:5186
```

레이어와 geometry 종류별로 SHP를 생성합니다.

```text
data/SmartLayout_shp/
  equipment_line.shp
  equipment_polygon.shp
  equipment_block_outer.shp
  block_instances.csv
```

`block_instances.csv`는 DXF `INSERT` 하나당 한 행이며 블록명, 레이어,
원본 handle, 블록 Attribute JSON, INSERT의 전체 DXF 속성 JSON을 보존합니다.
SHP feature에는 `block_inst_id`, `block_name`, `entity_type`, `entity_handle`,
`dxf_attrs`가 포함되어 개별 선·면·점 객체를 해당 블록 인스턴스로 추적할 수 있습니다.

각 블록 INSERT의 내부 Line/Polyline을 모두 모아 Polygonizer로 폐합 영역을
계산한 결과는 `*_block_outer.shp`로 별도 출력합니다. 한 블록에 외곽 영역이
여러 개이면 여러 Polygon feature로 저장되며, 원본 선·폴리라인 SHP는 그대로
유지됩니다.

Shapefile은 한 파일에서 geometry 종류를 혼합할 수 없고 DBF 필드명은 10자
제한이 있으므로 레이어와 geometry 종류별로 파일을 분리합니다. 원래의
긴 Attribute 이름과 전체 값은 `block_instances.csv`에서 확인할 수 있습니다.

WPF Importer는 블록 인스턴스마다 내부 Line/Polyline/곡선 선분 전체를
Polygonizer에 넣어 폐합 영역을 만들고, 그 결과를 블록의 `outer_geom`으로
저장합니다. 기존에 저장된 외곽선은 자동 변환되지 않으므로 새 계산 결과가
필요하면 기존 DXF 데이터를 삭제한 뒤 다시 Import해야 합니다.

곡선은 `--flattening` 허용오차로 선분화합니다. 기본값은 0.01이며 도면
단위에 맞춰 조정하십시오.