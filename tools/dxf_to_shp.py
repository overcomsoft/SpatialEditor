"""Export DXF entities to layer-specific ESRI Shapefiles.

SHP files are split by layer and geometry family because the format cannot
mix geometry types. Block INSERTs are expanded with ezdxf virtual_entities().
The block instance manifest preserves one CSV row per INSERT and its attrs.
"""

from __future__ import annotations

import argparse
import csv
import json
import logging
import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import ezdxf
import fiona
from ezdxf.entities import DXFGraphic, Insert
from ezdxf.path import make_path
from shapely.geometry import LineString, Point, Polygon, mapping
from shapely.ops import polygonize, unary_union

LOGGER = logging.getLogger("dxf_to_shp")


@dataclass
class Feature:
    geometry: Any
    layer: str
    entity_type: str
    handle: str
    block_instance_id: str = ""
    block_name: str = ""
    attributes: dict[str, Any] = field(default_factory=dict)


def safe_name(value: str) -> str:
    value = re.sub(r"[^A-Za-z0-9_-]+", "_", value).strip("_") or "NO_LAYER"
    return value[:45]


def flatten_path(entity: DXFGraphic, distance: float) -> list[tuple[float, float]]:
    return [(float(vertex.x), float(vertex.y)) for vertex in make_path(entity).flattening(distance=distance)]


def entity_geometry(entity: DXFGraphic, flattening: float) -> Any | None:
    dxftype = entity.dxftype()
    if dxftype == "POINT":
        location = entity.dxf.location
        return Point(float(location.x), float(location.y))
    if dxftype == "LINE":
        return LineString(((entity.dxf.start.x, entity.dxf.start.y), (entity.dxf.end.x, entity.dxf.end.y)))
    if dxftype in {"LWPOLYLINE", "POLYLINE", "SPLINE", "ARC", "CIRCLE", "ELLIPSE"}:
        coordinates = flatten_path(entity, flattening)
        if len(coordinates) < 2:
            return None
        closed = bool(getattr(entity, "closed", False) or getattr(entity.dxf, "is_closed", False))
        if closed and coordinates[0] != coordinates[-1]:
            coordinates.append(coordinates[0])
        if closed and len(coordinates) >= 4:
            polygon = Polygon(coordinates)
            if polygon.is_valid and not polygon.is_empty:
                return polygon
        return LineString(coordinates)
    if dxftype in {"TEXT", "MTEXT"}:
        location = entity.dxf.insert
        return Point(float(location.x), float(location.y))
    return None


def insert_attributes(insert: Insert) -> dict[str, Any]:
    return {f"attr_{attribute.dxf.tag}": attribute.dxf.text for attribute in (insert.attribs or [])}


def json_attributes(entity: DXFGraphic) -> str:
    return json.dumps(dict(entity.dxfattribs()), ensure_ascii=False, default=str)


def add_feature(features: list[Feature], entity: DXFGraphic, layer: str, block_id: str, block_name: str, flattening: float, extra: dict[str, Any]) -> None:
    geometry = entity_geometry(entity, flattening)
    if geometry is None or geometry.is_empty:
        return
    values = {
        "entity_type": entity.dxftype(),
        "entity_handle": entity.dxf.handle or "",
        "block_inst_id": block_id,
        "block_name": block_name,
        "dxf_attrs": json_attributes(entity),
        **extra,
    }
    if entity.dxftype() == "TEXT":
        values["text_value"] = entity.dxf.text
    elif entity.dxftype() == "MTEXT":
        values["text_value"] = entity.text
    features.append(Feature(geometry, layer, entity.dxftype(), entity.dxf.handle or "", block_id, block_name, values))


def collect_entities(doc: ezdxf.document.Drawing, flattening: float) -> tuple[list[Feature], list[dict[str, Any]]]:
    features: list[Feature] = []
    instances: list[dict[str, Any]] = []
    instance_number = 0

    def visit(entity: DXFGraphic, inherited_layer: str, block_id: str, block_name: str, stack: tuple[str, ...], extra: dict[str, Any]) -> None:
        nonlocal instance_number
        if isinstance(entity, Insert):
            feature_start = len(features)
            instance_number += 1
            current_id = f"{entity.dxf.handle or 'INSERT'}-{instance_number}"
            current_name = entity.dxf.name
            if current_name in stack:
                LOGGER.warning("Circular block reference skipped: %s", current_name)
                return
            current_layer = entity.dxf.layer or inherited_layer or "0"
            current_attrs = insert_attributes(entity)
            instances.append({
                "block_instance_id": current_id,
                "block_name": current_name,
                "layer": current_layer,
                "handle": entity.dxf.handle or "",
                "attributes": json.dumps(current_attrs, ensure_ascii=False),
                "dxf_attrs": json_attributes(entity),
            })
            for child in entity.virtual_entities():
                visit(child, child.dxf.layer or current_layer, current_id, current_name, (*stack, current_name), current_attrs)
            for polygon in polygonize_block(features[feature_start:]):
                features.append(Feature(
                    geometry=polygon,
                    layer=current_layer,
                    entity_type="BLOCK_OUTER",
                    handle=entity.dxf.handle or "",
                    block_instance_id=current_id,
                    block_name=current_name,
                    attributes={
                        "entity_type": "BLOCK_OUTER",
                        "entity_handle": entity.dxf.handle or "",
                        "block_inst_id": current_id,
                        "block_name": current_name,
                        "block_attributes": json.dumps(current_attrs, ensure_ascii=False),
                    },
                ))
            return
        add_feature(features, entity, entity.dxf.layer or inherited_layer or "0", block_id, block_name, flattening, extra)

    for entity in doc.modelspace():
        visit(entity, entity.dxf.layer or "0", "", "", (), {})
    return features, instances


def polygonize_block(features: list[Feature]) -> list[Polygon]:
    """Create every closed area from one block's complete linework."""
    linework: list[LineString] = []
    for feature in features:
        if feature.entity_type == "BLOCK_OUTER":
            continue
        if isinstance(feature.geometry, LineString):
            linework.append(feature.geometry)
        elif isinstance(feature.geometry, Polygon):
            linework.append(LineString(feature.geometry.exterior.coords))
            linework.extend(LineString(ring.coords) for ring in feature.geometry.interiors)
    if not linework:
        return []
    return [polygon for polygon in polygonize(unary_union(linework)) if polygon.is_valid and not polygon.is_empty]


def dbf_field_map(features: list[Feature]) -> dict[str, str]:
    used: set[str] = set()
    result: dict[str, str] = {}
    for key in sorted({key for feature in features for key in feature.attributes}):
        candidate = re.sub(r"[^A-Za-z0-9_]", "_", key).lower()[:10] or "field"
        base = candidate
        number = 1
        while candidate in used:
            suffix = str(number)
            candidate = f"{base[:10 - len(suffix)]}{suffix}"
            number += 1
        used.add(candidate)
        result[key] = candidate
    return result


def geometry_kind(geometry: Any) -> str:
    if geometry.geom_type == "Point":
        return "point"
    if geometry.geom_type in {"Polygon", "MultiPolygon"}:
        return "polygon"
    return "line"


def export_features(features: list[Feature], output: Path, encoding: str, crs: str | None) -> list[Path]:
    grouped: dict[tuple[str, str], list[Feature]] = defaultdict(list)
    for feature in features:
        kind = "block_outer" if feature.entity_type == "BLOCK_OUTER" else geometry_kind(feature.geometry)
        grouped[(feature.layer, kind)].append(feature)
    created: list[Path] = []
    for (layer, kind), items in sorted(grouped.items()):
        field_map = dbf_field_map(items)
        geometry_name = {"point": "Point", "line": "LineString", "polygon": "Polygon", "block_outer": "Polygon"}[kind]
        schema = {"geometry": geometry_name, "properties": {name: "str:254" for name in field_map.values()}}
        path = output / f"{safe_name(layer)}_{kind}.shp"
        with fiona.open(path, "w", driver="ESRI Shapefile", schema=schema, encoding=encoding, crs=crs) as sink:
            for feature in items:
                properties = {field_map[key]: str(value)[:254] for key, value in feature.attributes.items() if key in field_map}
                sink.write({"geometry": mapping(feature.geometry), "properties": properties})
        created.append(path)
        LOGGER.info("Wrote %d features to %s", len(items), path)
    return created


def write_instances(instances: list[dict[str, Any]], output: Path) -> Path:
    path = output / "block_instances.csv"
    fields = ("block_instance_id", "block_name", "layer", "handle", "attributes", "dxf_attrs")
    with path.open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields)
        writer.writeheader()
        writer.writerows(instances)
    return path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dxf", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--flattening", type=float, default=0.01)
    parser.add_argument("--encoding", default="utf-8")
    parser.add_argument("--crs", default=None, help="For example EPSG:5186")
    parser.add_argument("--log-level", default="INFO", choices=("DEBUG", "INFO", "WARNING"))
    args = parser.parse_args()
    logging.basicConfig(level=getattr(logging, args.log_level), format="%(levelname)s %(message)s")
    if not args.dxf.exists():
        raise FileNotFoundError(args.dxf)
    args.output.mkdir(parents=True, exist_ok=True)
    features, instances = collect_entities(ezdxf.readfile(args.dxf), args.flattening)
    paths = export_features(features, args.output, args.encoding, args.crs)
    manifest = write_instances(instances, args.output)
    LOGGER.info("DXF features=%d, block instances=%d, shapefiles=%d", len(features), len(instances), len(paths))
    LOGGER.info("Block manifest=%s", manifest)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())