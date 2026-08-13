#!/usr/bin/env python3
"""Build deterministic, offline map assets for the Ming 1629 scenario.

The builder deliberately keeps four different concerns separate:

* Natural Earth land polygons provide only the physical coastline/base map.
* Reviewed scenario records select research-baseline geometry from the Hartwell source;
  explicit evidence overlays provide the reviewed, limited map claims.
* Place and route JSON files provide presentation anchors and polylines.
* Simulation topology is not read, inferred, or written by this tool.

Only the Python standard library and Pillow are required.  Every input is local,
all coordinates are transformed with one Web Mercator projection, and generated
files contain no build time or other volatile metadata.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
import struct
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Iterator, Mapping, Sequence

from PIL import Image, ImageColor, ImageDraw, PngImagePlugin


GENERATOR_VERSION = 2
SCHEMA_VERSION = 1
EARTH_RADIUS_METRES = 6_378_137.0
MAX_MERCATOR_LATITUDE = 85.0511287798066
DEFAULT_CONFIG = "content/scenarios/ming_1629/map/map-build.json"


class BuildError(RuntimeError):
    """An actionable input, validation, or build error."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise BuildError(message)


def require_mapping(value: Any, label: str) -> Mapping[str, Any]:
    require(isinstance(value, Mapping), f"{label} 必须是 JSON 对象。")
    return value


def require_list(value: Any, label: str) -> list[Any]:
    require(isinstance(value, list), f"{label} 必须是 JSON 数组。")
    return value


def require_string(value: Any, label: str) -> str:
    require(isinstance(value, str) and value.strip() != "", f"{label} 必须是非空字符串。")
    return value.strip()


def require_id(value: Any, label: str) -> str:
    identifier = require_string(value, label)
    require(
        all(character.isalnum() or character in "-_" for character in identifier),
        f"{label}={identifier!r} 只能包含字母、数字、连字符和下划线。",
    )
    return identifier


def require_number(value: Any, label: str) -> float:
    require(
        isinstance(value, (int, float)) and not isinstance(value, bool),
        f"{label} 必须是数字。",
    )
    number = float(value)
    require(math.isfinite(number), f"{label} 必须是有限数字。")
    return number


def validate_lon_lat(longitude: Any, latitude: Any, label: str) -> tuple[float, float]:
    lon = require_number(longitude, f"{label}.longitude")
    lat = require_number(latitude, f"{label}.latitude")
    require(-180.0 <= lon <= 180.0, f"{label} 经度 {lon} 超出 [-180, 180]。")
    require(-90.0 <= lat <= 90.0, f"{label} 纬度 {lat} 超出 [-90, 90]。")
    return lon, lat


def load_json(path: Path, label: str) -> Mapping[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8-sig"))
    except FileNotFoundError as error:
        raise BuildError(f"找不到{label}：{path}") from error
    except json.JSONDecodeError as error:
        raise BuildError(
            f"{label}不是有效 JSON：{path}:{error.lineno}:{error.colno} {error.msg}"
        ) from error
    return require_mapping(value, label)


def ensure_schema(document: Mapping[str, Any], label: str) -> None:
    require(
        document.get("schema_version") == SCHEMA_VERSION,
        f"{label}.schema_version 必须为 {SCHEMA_VERSION}。",
    )


def resolve_repo_path(repo_root: Path, value: Any, label: str) -> Path:
    raw = Path(require_string(value, label))
    resolved = (raw if raw.is_absolute() else repo_root / raw).resolve()
    try:
        resolved.relative_to(repo_root)
    except ValueError as error:
        raise BuildError(f"{label} 必须位于项目目录内：{resolved}") from error
    return resolved


def relative_path(repo_root: Path, path: Path) -> str:
    return path.resolve().relative_to(repo_root).as_posix()


def godot_path(repo_root: Path, path: Path) -> str:
    return f"res://{relative_path(repo_root, path)}"


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def write_json(path: Path, value: Any) -> None:
    text = json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        sort_keys=True,
        allow_nan=False,
    )
    path.write_text(text + "\n", encoding="utf-8", newline="\n")


def save_png(image: Image.Image, path: Path) -> None:
    # An explicitly empty PngInfo prevents accidental timestamps or host metadata.
    image.save(
        path,
        format="PNG",
        compress_level=9,
        optimize=False,
        pnginfo=PngImagePlugin.PngInfo(),
    )


def parse_colour(value: Any, default: str, label: str, mode: str) -> tuple[int, ...]:
    raw = default if value is None else require_string(value, label)
    try:
        return ImageColor.getcolor(raw, mode)
    except ValueError as error:
        raise BuildError(f"{label} 不是有效颜色：{raw!r}") from error


def bbox_intersects(a: Sequence[float], b: Sequence[float]) -> bool:
    return not (a[2] < b[0] or a[0] > b[2] or a[3] < b[1] or a[1] > b[3])


def coordinate_bbox(points: Sequence[tuple[float, float]]) -> tuple[float, float, float, float]:
    return (
        min(point[0] for point in points),
        min(point[1] for point in points),
        max(point[0] for point in points),
        max(point[1] for point in points),
    )


def signed_ring_area(points: Sequence[tuple[float, float]]) -> float:
    return 0.5 * sum(
        first[0] * second[1] - second[0] * first[1]
        for first, second in zip(points, points[1:])
    )


def validate_ring(
    raw_ring: Any,
    label: str,
    *,
    require_closed: bool = True,
) -> list[tuple[float, float]]:
    values = require_list(raw_ring, label)
    require(len(values) >= 4, f"{label} 至少需要 4 个坐标（含闭合点）。")
    points: list[tuple[float, float]] = []
    for index, raw_point in enumerate(values):
        point = require_list(raw_point, f"{label}[{index}]")
        require(len(point) >= 2, f"{label}[{index}] 至少需要经度和纬度。")
        points.append(validate_lon_lat(point[0], point[1], f"{label}[{index}]"))
    if require_closed:
        require(
            math.isclose(points[0][0], points[-1][0], abs_tol=1e-9)
            and math.isclose(points[0][1], points[-1][1], abs_tol=1e-9),
            f"{label} 没有闭合。",
        )
    require(abs(signed_ring_area(points)) > 1e-15, f"{label} 面积为零。")
    return points


def validate_polyline(raw_line: Any, label: str) -> list[tuple[float, float]]:
    values = require_list(raw_line, label)
    require(len(values) >= 2, f"{label} 至少需要 2 个坐标。")
    points: list[tuple[float, float]] = []
    for index, raw_point in enumerate(values):
        point = require_list(raw_point, f"{label}[{index}]")
        require(len(point) >= 2, f"{label}[{index}] 至少需要经度和纬度。")
        points.append(validate_lon_lat(point[0], point[1], f"{label}[{index}]"))
    require(
        any(first != second for first, second in zip(points, points[1:])),
        f"{label} 的所有坐标都相同。",
    )
    return points


def geometry_polygons(geometry: Any, label: str) -> list[list[list[tuple[float, float]]]]:
    value = require_mapping(geometry, label)
    geometry_type = value.get("type")
    coordinates = value.get("coordinates")
    if geometry_type == "Polygon":
        raw_polygons = [require_list(coordinates, f"{label}.coordinates")]
    elif geometry_type == "MultiPolygon":
        raw_polygons = require_list(coordinates, f"{label}.coordinates")
    else:
        raise BuildError(f"{label}.type 必须是 Polygon 或 MultiPolygon，实际为 {geometry_type!r}。")

    polygons: list[list[list[tuple[float, float]]]] = []
    for polygon_index, raw_polygon in enumerate(raw_polygons):
        rings = require_list(raw_polygon, f"{label}.coordinates[{polygon_index}]")
        require(rings, f"{label}.coordinates[{polygon_index}] 不能为空。")
        polygons.append(
            [
                validate_ring(
                    raw_ring,
                    f"{label}.coordinates[{polygon_index}][{ring_index}]",
                )
                for ring_index, raw_ring in enumerate(rings)
            ]
        )
    return polygons


def geometry_bbox(polygons: Sequence[Sequence[Sequence[tuple[float, float]]]]) -> list[float]:
    points = [point for polygon in polygons for ring in polygon for point in ring]
    require(points, "几何不能没有坐标。")
    bbox = coordinate_bbox(points)
    return [round(number, 9) for number in bbox]


@dataclass(frozen=True)
class WebMercatorProjection:
    bounds_lon_lat: tuple[float, float, float, float]
    width: int
    height: int
    padding: int
    projected_bounds: tuple[float, float, float, float]
    scale: float
    offset_x: float
    offset_y: float
    content_rect: tuple[float, float, float, float]

    @staticmethod
    def mercator(longitude: float, latitude: float) -> tuple[float, float]:
        clamped_latitude = max(-MAX_MERCATOR_LATITUDE, min(MAX_MERCATOR_LATITUDE, latitude))
        x = EARTH_RADIUS_METRES * math.radians(longitude)
        y = EARTH_RADIUS_METRES * math.log(
            math.tan(math.pi / 4.0 + math.radians(clamped_latitude) / 2.0)
        )
        return x, y

    @classmethod
    def create(
        cls,
        bounds: Sequence[Any],
        width: int,
        height: int,
        padding: int,
    ) -> "WebMercatorProjection":
        require(len(bounds) == 4, "map-build.bounds 必须是 [west, south, east, north]。")
        west, south = validate_lon_lat(bounds[0], bounds[1], "map-build.bounds.southwest")
        east, north = validate_lon_lat(bounds[2], bounds[3], "map-build.bounds.northeast")
        require(west < east and south < north, "map-build.bounds 的西/南必须小于东/北。")
        require(
            -MAX_MERCATOR_LATITUDE < south < north < MAX_MERCATOR_LATITUDE,
            f"Web Mercator 纬度必须位于 ±{MAX_MERCATOR_LATITUDE:.6f} 度内。",
        )
        require(width > 0 and height > 0, "画布宽高必须大于零。")
        require(padding >= 0, "画布 padding 不能为负数。")
        require(width > 2 * padding and height > 2 * padding, "画布 padding 挤占了全部绘图区。")

        min_x, min_y = cls.mercator(west, south)
        max_x, max_y = cls.mercator(east, north)
        available_width = width - 2 * padding
        available_height = height - 2 * padding
        scale = min(
            available_width / (max_x - min_x),
            available_height / (max_y - min_y),
        )
        draw_width = (max_x - min_x) * scale
        draw_height = (max_y - min_y) * scale
        content_left = padding + (available_width - draw_width) / 2.0
        content_top = padding + (available_height - draw_height) / 2.0
        offset_x = content_left - min_x * scale
        offset_y = content_top + max_y * scale
        return cls(
            bounds_lon_lat=(west, south, east, north),
            width=width,
            height=height,
            padding=padding,
            projected_bounds=(min_x, min_y, max_x, max_y),
            scale=scale,
            offset_x=offset_x,
            offset_y=offset_y,
            content_rect=(content_left, content_top, draw_width, draw_height),
        )

    def project(self, longitude: float, latitude: float) -> tuple[float, float]:
        x, y = self.mercator(longitude, latitude)
        return x * self.scale + self.offset_x, -y * self.scale + self.offset_y

    def manifest(self) -> dict[str, Any]:
        return {
            "name": "web_mercator",
            "input_crs": "EPSG:4326",
            "bounds_lon_lat": [round(value, 9) for value in self.bounds_lon_lat],
            "projected_bounds_metres": [round(value, 6) for value in self.projected_bounds],
            "scale_pixels_per_metre": round(self.scale, 12),
            "offset_pixels": [round(self.offset_x, 6), round(self.offset_y, 6)],
            "affine_formula": {
                "map_x": "mercator_x * scale + offset_x",
                "map_y": "-mercator_y * scale + offset_y",
            },
        }


@dataclass(frozen=True)
class ShapeRecord:
    record_number: int
    bbox: tuple[float, float, float, float]
    rings: list[list[tuple[float, float]]]


def iter_polygon_shapes(path: Path) -> Iterator[ShapeRecord]:
    try:
        actual_size = path.stat().st_size
        stream = path.open("rb")
    except FileNotFoundError as error:
        raise BuildError(f"找不到 Natural Earth land SHP：{path}") from error

    with stream:
        header = stream.read(100)
        require(len(header) == 100, f"SHP 文件头不完整：{path}")
        require(struct.unpack_from(">i", header, 0)[0] == 9994, f"SHP magic number 错误：{path}")
        declared_size = struct.unpack_from(">i", header, 24)[0] * 2
        require(declared_size == actual_size, f"SHP 声明长度 {declared_size} 与实际 {actual_size} 不一致。")
        require(struct.unpack_from("<i", header, 28)[0] == 1000, "SHP version 必须为 1000。")
        require(struct.unpack_from("<i", header, 32)[0] == 5, "Natural Earth land SHP 必须是 Polygon(5)。")

        previous_record_number = 0
        while True:
            record_header = stream.read(8)
            if not record_header:
                break
            require(len(record_header) == 8, "SHP 记录头被截断。")
            record_number, content_words = struct.unpack(">ii", record_header)
            require(record_number > previous_record_number, "SHP record number 未严格递增。")
            previous_record_number = record_number
            require(content_words >= 2, f"SHP 记录 {record_number} 长度无效。")
            content = stream.read(content_words * 2)
            require(len(content) == content_words * 2, f"SHP 记录 {record_number} 被截断。")
            shape_type = struct.unpack_from("<i", content, 0)[0]
            if shape_type == 0:
                continue
            require(shape_type == 5, f"SHP 记录 {record_number} 类型 {shape_type} 不是 Polygon(5)。")
            require(len(content) >= 44, f"SHP 记录 {record_number} 缺少 Polygon 头。")
            bbox = struct.unpack_from("<4d", content, 4)
            require(all(math.isfinite(value) for value in bbox), f"SHP 记录 {record_number} bbox 非有限。")
            part_count, point_count = struct.unpack_from("<2i", content, 36)
            require(part_count > 0 and point_count >= 4, f"SHP 记录 {record_number} parts/points 无效。")
            expected_size = 44 + part_count * 4 + point_count * 16
            require(len(content) >= expected_size, f"SHP 记录 {record_number} 坐标被截断。")
            part_indexes = list(struct.unpack_from(f"<{part_count}i", content, 44))
            require(part_indexes[0] == 0, f"SHP 记录 {record_number} 首个 part 必须从 0 开始。")
            require(
                part_indexes == sorted(set(part_indexes)) and part_indexes[-1] < point_count,
                f"SHP 记录 {record_number} part 索引无效或重复。",
            )
            points_offset = 44 + part_count * 4
            all_points = [
                struct.unpack_from("<2d", content, points_offset + index * 16)
                for index in range(point_count)
            ]
            rings: list[list[tuple[float, float]]] = []
            for part_index, start in enumerate(part_indexes):
                end = part_indexes[part_index + 1] if part_index + 1 < part_count else point_count
                raw_ring = [[point[0], point[1]] for point in all_points[start:end]]
                rings.append(validate_ring(raw_ring, f"SHP record {record_number} part {part_index}"))
            yield ShapeRecord(record_number, tuple(float(value) for value in bbox), rings)


def iter_polyline_shapes(path: Path) -> Iterator[ShapeRecord]:
    """Read a Polygon-like Shapefile record whose parts are open polylines."""

    try:
        actual_size = path.stat().st_size
        stream = path.open("rb")
    except FileNotFoundError as error:
        raise BuildError(f"找不到 Natural Earth rivers SHP：{path}") from error

    with stream:
        header = stream.read(100)
        require(len(header) == 100, f"SHP 文件头不完整：{path}")
        require(struct.unpack_from(">i", header, 0)[0] == 9994, f"SHP magic number 错误：{path}")
        declared_size = struct.unpack_from(">i", header, 24)[0] * 2
        require(declared_size == actual_size, f"SHP 声明长度 {declared_size} 与实际 {actual_size} 不一致。")
        require(struct.unpack_from("<i", header, 28)[0] == 1000, "SHP version 必须为 1000。")
        require(struct.unpack_from("<i", header, 32)[0] == 3, "Natural Earth rivers SHP 必须是 PolyLine(3)。")

        previous_record_number = 0
        while True:
            record_header = stream.read(8)
            if not record_header:
                break
            require(len(record_header) == 8, "SHP 记录头被截断。")
            record_number, content_words = struct.unpack(">ii", record_header)
            require(record_number > previous_record_number, "SHP record number 未严格递增。")
            previous_record_number = record_number
            require(content_words >= 2, f"SHP 记录 {record_number} 长度无效。")
            content = stream.read(content_words * 2)
            require(len(content) == content_words * 2, f"SHP 记录 {record_number} 被截断。")
            shape_type = struct.unpack_from("<i", content, 0)[0]
            if shape_type == 0:
                continue
            require(shape_type == 3, f"SHP 记录 {record_number} 类型 {shape_type} 不是 PolyLine(3)。")
            require(len(content) >= 44, f"SHP 记录 {record_number} 缺少 PolyLine 头。")
            bbox = struct.unpack_from("<4d", content, 4)
            require(all(math.isfinite(value) for value in bbox), f"SHP 记录 {record_number} bbox 非有限。")
            part_count, point_count = struct.unpack_from("<2i", content, 36)
            require(part_count > 0 and point_count >= 2, f"SHP 记录 {record_number} parts/points 无效。")
            expected_size = 44 + part_count * 4 + point_count * 16
            require(len(content) >= expected_size, f"SHP 记录 {record_number} 坐标被截断。")
            part_indexes = list(struct.unpack_from(f"<{part_count}i", content, 44))
            require(part_indexes[0] == 0, f"SHP 记录 {record_number} 首个 part 必须从 0 开始。")
            require(
                part_indexes == sorted(set(part_indexes)) and part_indexes[-1] < point_count,
                f"SHP 记录 {record_number} part 索引无效或重复。",
            )
            points_offset = 44 + part_count * 4
            all_points = [
                struct.unpack_from("<2d", content, points_offset + index * 16)
                for index in range(point_count)
            ]
            lines: list[list[tuple[float, float]]] = []
            for part_index, start in enumerate(part_indexes):
                end = part_indexes[part_index + 1] if part_index + 1 < part_count else point_count
                raw_line = [[point[0], point[1]] for point in all_points[start:end]]
                lines.append(validate_polyline(raw_line, f"SHP record {record_number} part {part_index}"))
            yield ShapeRecord(record_number, tuple(float(value) for value in bbox), lines)


def projected_points(
    ring: Sequence[tuple[float, float]], projection: WebMercatorProjection
) -> list[tuple[float, float]]:
    return [projection.project(longitude, latitude) for longitude, latitude in ring]


def draw_dashed_line(
    draw: ImageDraw.ImageDraw,
    points: Sequence[tuple[float, float]],
    fill: tuple[int, ...],
    width: int,
    dash: float = 10.0,
    gap: float = 7.0,
) -> None:
    if len(points) < 2:
        return
    cycle = dash + gap
    phase = 0.0
    for start, end in zip(points, points[1:]):
        dx = end[0] - start[0]
        dy = end[1] - start[1]
        length = math.hypot(dx, dy)
        if length <= 1e-9:
            continue
        distance = 0.0
        while distance < length:
            cycle_position = phase % cycle
            step = min(length - distance, cycle - cycle_position)
            if cycle_position < dash:
                visible = min(step, dash - cycle_position)
                first_ratio = distance / length
                second_ratio = (distance + visible) / length
                draw.line(
                    [
                        (start[0] + dx * first_ratio, start[1] + dy * first_ratio),
                        (start[0] + dx * second_ratio, start[1] + dy * second_ratio),
                    ],
                    fill=fill,
                    width=width,
                )
            distance += step
            phase += step


def render_physical_base(
    shp_path: Path,
    lakes_path: Path | None,
    rivers_path: Path | None,
    projection: WebMercatorProjection,
    style: Mapping[str, Any],
) -> tuple[Image.Image, dict[str, int]]:
    sea = parse_colour(style.get("sea"), "#73929B", "style.sea", "RGB")
    land = parse_colour(style.get("land"), "#D8CDAA", "style.land", "RGB")
    coast = parse_colour(style.get("coast"), "#556A69", "style.coast", "RGB")
    image = Image.new("RGB", (projection.width, projection.height), sea)
    land_mask = Image.new("L", image.size, 0)
    mask_draw = ImageDraw.Draw(land_mask)
    visible_rings: list[tuple[float, list[tuple[float, float]]]] = []
    shape_count = 0
    point_count = 0
    for shape in iter_polygon_shapes(shp_path):
        if not bbox_intersects(shape.bbox, projection.bounds_lon_lat):
            continue
        shape_has_visible_ring = False
        for ring in shape.rings:
            if not bbox_intersects(coordinate_bbox(ring), projection.bounds_lon_lat):
                continue
            area = signed_ring_area(ring)
            visible_rings.append((area, projected_points(ring, projection)))
            point_count += len(ring)
            shape_has_visible_ring = True
        if shape_has_visible_ring:
            shape_count += 1

    # Shapefile outer rings are clockwise (negative signed area), holes are
    # counter-clockwise.  Largest-first drawing also preserves islands in holes.
    for area, points in sorted(visible_rings, key=lambda item: abs(item[0]), reverse=True):
        mask_draw.polygon(points, fill=255 if area < 0 else 0)
    image.paste(land, (0, 0, projection.width, projection.height), land_mask)
    coast_draw = ImageDraw.Draw(image)
    for _area, points in visible_rings:
        coast_draw.line(points, fill=coast, width=1, joint="curve")
    counts = {
        "visible_shape_records": shape_count,
        "visible_rings": len(visible_rings),
        "visible_points": point_count,
    }

    if lakes_path is not None:
        lake_rings: list[tuple[float, list[tuple[float, float]]]] = []
        lake_shape_count = 0
        lake_point_count = 0
        for shape in iter_polygon_shapes(lakes_path):
            if not bbox_intersects(shape.bbox, projection.bounds_lon_lat):
                continue
            shape_has_visible_ring = False
            for ring in shape.rings:
                if not bbox_intersects(coordinate_bbox(ring), projection.bounds_lon_lat):
                    continue
                lake_rings.append((signed_ring_area(ring), projected_points(ring, projection)))
                lake_point_count += len(ring)
                shape_has_visible_ring = True
            if shape_has_visible_ring:
                lake_shape_count += 1
        lake_mask = Image.new("L", image.size, 0)
        lake_mask_draw = ImageDraw.Draw(lake_mask)
        for area, points in sorted(lake_rings, key=lambda item: abs(item[0]), reverse=True):
            lake_mask_draw.polygon(points, fill=255 if area < 0 else 0)
        image.paste(sea, (0, 0, projection.width, projection.height), lake_mask)
        lake_outline = parse_colour(
            style.get("lake_outline"), "#607D84", "style.lake_outline", "RGB"
        )
        lake_draw = ImageDraw.Draw(image)
        for _area, points in lake_rings:
            lake_draw.line(points, fill=lake_outline, width=1, joint="curve")
        counts.update(
            {
                "visible_lake_shape_records": lake_shape_count,
                "visible_lake_rings": len(lake_rings),
                "visible_lake_points": lake_point_count,
            }
        )
    else:
        counts.update(
            {
                "visible_lake_shape_records": 0,
                "visible_lake_rings": 0,
                "visible_lake_points": 0,
            }
        )

    if rivers_path is not None:
        river_colour = parse_colour(style.get("river"), "#607D84", "style.river", "RGB")
        river_draw = ImageDraw.Draw(image)
        river_shape_count = 0
        river_line_count = 0
        river_point_count = 0
        for shape in iter_polyline_shapes(rivers_path):
            if not bbox_intersects(shape.bbox, projection.bounds_lon_lat):
                continue
            shape_has_visible_line = False
            for line in shape.rings:
                if not bbox_intersects(coordinate_bbox(line), projection.bounds_lon_lat):
                    continue
                points = projected_points(line, projection)
                river_draw.line(points, fill=river_colour, width=1, joint="curve")
                river_line_count += 1
                river_point_count += len(line)
                shape_has_visible_line = True
            if shape_has_visible_line:
                river_shape_count += 1
        counts.update(
            {
                "visible_river_shape_records": river_shape_count,
                "visible_river_lines": river_line_count,
                "visible_river_points": river_point_count,
            }
        )
    else:
        counts.update(
            {
                "visible_river_shape_records": 0,
                "visible_river_lines": 0,
                "visible_river_points": 0,
            }
        )

    # Geometry outside the configured geographic bounds may still project onto
    # the padded canvas.  Keep padding clean and make the configured content
    # rectangle the sole physical-map viewport.
    content_left, content_top, content_width, content_height = projection.content_rect
    clip_mask = Image.new("L", image.size, 0)
    ImageDraw.Draw(clip_mask).rectangle(
        (
            math.floor(content_left),
            math.floor(content_top),
            math.ceil(content_left + content_width),
            math.ceil(content_top + content_height),
        ),
        fill=255,
    )
    clipped = Image.new("RGB", image.size, sea)
    clipped.paste(image, (0, 0), clip_mask)
    return clipped, counts


def load_source_ledger(path: Path) -> dict[str, dict[str, str]]:
    try:
        with path.open("r", encoding="utf-8-sig", newline="") as stream:
            reader = csv.DictReader(stream)
            require(reader.fieldnames is not None and "source_id" in reader.fieldnames, "source-ledger.csv 缺少 source_id 列。")
            ledger: dict[str, dict[str, str]] = {}
            for line_number, row in enumerate(reader, start=2):
                source_id = require_id(row.get("source_id"), f"source-ledger.csv:{line_number}.source_id")
                require(source_id not in ledger, f"source-ledger.csv 存在重复 source_id：{source_id}")
                ledger[source_id] = {key: value or "" for key, value in row.items() if key is not None}
    except FileNotFoundError as error:
        raise BuildError(f"找不到 source ledger：{path}") from error
    require(ledger, "source-ledger.csv 不能为空。")
    return ledger


def validate_source_ids(
    value: Any,
    label: str,
    ledger: Mapping[str, Mapping[str, str]],
) -> list[str]:
    raw_ids = require_list(value, label)
    require(raw_ids, f"{label} 不能为空。")
    source_ids = [require_id(raw_id, f"{label}[{index}]") for index, raw_id in enumerate(raw_ids)]
    require(len(source_ids) == len(set(source_ids)), f"{label} 不能包含重复 ID。")
    unknown = sorted(set(source_ids) - set(ledger))
    require(not unknown, f"{label} 引用了 source-ledger.csv 中不存在的 ID：{unknown}")
    return source_ids


def load_evidence_ledger(
    path: Path,
    source_ledger: Mapping[str, Mapping[str, str]],
) -> tuple[dict[str, Mapping[str, Any]], set[str]]:
    document = load_json(path, "liaodong-1629-evidence.json")
    # The evidence ledger owns schema version 2; map input files remain on the
    # builder's schema version 1.  Keep this compatibility gate local to the
    # ledger loader so the empty-overlay change does not broaden other schemas.
    require(
        document.get("schema_version") in {1, 2},
        "liaodong-1629-evidence.json.schema_version 必须为 1 或 2。",
    )
    sources = require_list(document.get("sources"), "evidence.sources")
    source_ids: set[str] = set()
    for index, raw_source in enumerate(sources):
        source = require_mapping(raw_source, f"evidence.sources[{index}]")
        source_id = require_id(source.get("id"), f"evidence.sources[{index}].id")
        require(source_id not in source_ids, f"证据 source ID 重复：{source_id}")
        source_ids.add(source_id)
        require(source.get("access_checked"), f"证据 source {source_id} 缺少 access_checked。")
        require(source.get("access_result"), f"证据 source {source_id} 缺少 access_result。")
        require(
            source.get("url") and source.get("license"),
            f"证据 source {source_id} 必须记录 URL 和 license。",
        )
        require(source_id in source_ledger, f"证据 source {source_id} 未登记到 source-ledger.csv。")

    raw_claims = require_list(document.get("claims"), "evidence.claims")
    claims: dict[str, Mapping[str, Any]] = {}
    for index, raw_claim in enumerate(raw_claims):
        claim = require_mapping(raw_claim, f"evidence.claims[{index}]")
        claim_id = require_id(claim.get("id"), f"evidence.claims[{index}].id")
        require(claim_id not in claims, f"证据 claim ID 重复：{claim_id}")
        status = require_string(claim.get("status"), f"claim {claim_id}.status")
        require(status in {"FACT", "INFERENCE", "OPEN"}, f"claim {claim_id}.status 无效：{status}")
        claims[claim_id] = claim

    for claim_id, claim in claims.items():
        refs = [
            require_id(value, f"claim {claim_id}.source_ids[{index}]")
            for index, value in enumerate(
                require_list(claim.get("source_ids"), f"claim {claim_id}.source_ids")
            )
        ]
        require(refs, f"claim {claim_id}.source_ids 不能为空。")
        require(len(refs) == len(set(refs)), f"claim {claim_id}.source_ids 不能重复。")
        for ref in refs:
            if ref in source_ids:
                continue
            require(
                ref in claims and claims[ref].get("status") != "OPEN",
                f"claim {claim_id}.source_ids 引用了未知或 OPEN claim/source：{ref}",
            )

    admitted_claim_ids: set[str] = set()
    for index, raw_admission in enumerate(
        require_list(document.get("p0_map_admission"), "evidence.p0_map_admission")
    ):
        admission = require_mapping(raw_admission, f"evidence.p0_map_admission[{index}]")
        admission_id = require_id(admission.get("id"), f"evidence.p0_map_admission[{index}].id")
        admission_claims = require_list(
            admission.get("source_claim_ids"), f"admission {admission_id}.source_claim_ids"
        )
        for claim_index, raw_claim_id in enumerate(admission_claims):
            claim_id = require_id(
                raw_claim_id, f"admission {admission_id}.source_claim_ids[{claim_index}]"
            )
            require(claim_id in claims, f"admission {admission_id} 引用了未知 claim：{claim_id}")
            admitted_claim_ids.add(claim_id)

    require(claims, "evidence.claims 不能为空。")
    require(admitted_claim_ids, "evidence.p0_map_admission 不能为空。")
    return claims, admitted_claim_ids


def validate_claim_ids(
    raw_claim_ids: Any,
    label: str,
    claims: Mapping[str, Mapping[str, Any]],
    admitted_claim_ids: set[str],
) -> list[str]:
    claim_ids = [
        require_id(value, f"{label}[{index}]")
        for index, value in enumerate(require_list(raw_claim_ids, label))
    ]
    require(claim_ids, f"{label} 不能为空。")
    require(len(set(claim_ids)) == len(claim_ids), f"{label} 不能重复。")
    for claim_id in claim_ids:
        require(claim_id in claims, f"{label} 引用了未知 claim：{claim_id}")
        require(claim_id in admitted_claim_ids, f"{label} 引用的 claim 不在 P0 准入清单：{claim_id}")
    return claim_ids


def load_historical_regions(
    definition_path: Path,
    hartwell_path: Path,
    ledger: Mapping[str, Mapping[str, str]],
) -> tuple[Mapping[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
    definitions = load_json(definition_path, "historical-regions.json")
    ensure_schema(definitions, "historical-regions.json")
    geometry_depict_date = require_string(
        definitions.get("geometry_depict_date"),
        "historical-regions.json.geometry_depict_date",
    )
    document_claim_status = require_string(
        definitions.get("claim_status"), "historical-regions.json.claim_status"
    )
    historical_fit_status = require_string(
        definitions.get("historical_fit_status"),
        "historical-regions.json.historical_fit_status",
    )
    require(document_claim_status == "open", "当前草稿历史区必须明确 claim_status=open。")
    require(
        historical_fit_status == "research_baseline_only",
        "当前 Hartwell 几何必须明确 historical_fit_status=research_baseline_only。",
    )
    hartwell = load_json(hartwell_path, "Hartwell GeoJSON")
    require(hartwell.get("type") == "FeatureCollection", "Hartwell GeoJSON 必须是 FeatureCollection。")
    hartwell_features = require_list(hartwell.get("features"), "Hartwell GeoJSON.features")
    require(hartwell_features, "Hartwell GeoJSON.features 不能为空。")

    output_features: list[dict[str, Any]] = []
    manifest_regions: list[dict[str, Any]] = []
    region_ids: set[str] = set()
    used_feature_indexes: set[int] = set()
    for region_index, raw_region in enumerate(require_list(definitions.get("regions"), "historical-regions.json.regions")):
        region = require_mapping(raw_region, f"historical-regions.json.regions[{region_index}]")
        region_id = require_id(region.get("id"), f"regions[{region_index}].id")
        require(region_id not in region_ids, f"historical region ID 重复：{region_id}")
        region_ids.add(region_id)
        name_zh = require_string(region.get("name_zh"), f"region {region_id}.name_zh")
        boundary_kind = require_string(region.get("boundary_kind"), f"region {region_id}.boundary_kind")
        confidence = require_string(region.get("confidence"), f"region {region_id}.confidence")
        review_status = require_string(region.get("review_status"), f"region {region_id}.review_status")
        claim_status = require_string(region.get("claim_status"), f"region {region_id}.claim_status")
        region_fit_status = require_string(
            region.get("historical_fit_status"), f"region {region_id}.historical_fit_status"
        )
        require(confidence in {"high", "medium", "low", "unknown"}, f"region {region_id}.confidence 无效：{confidence}")
        require(review_status in {"accepted", "draft", "open", "rejected"}, f"region {region_id}.review_status 无效：{review_status}")
        require(review_status != "rejected", f"region {region_id} 已 rejected，不能进入构建输出。")
        require(claim_status == "open", f"region {region_id} 当前必须保持 claim_status=open。")
        require(
            region_fit_status == "research_baseline_only",
            f"region {region_id} 当前必须标记 historical_fit_status=research_baseline_only。",
        )
        require(
            region.get("effective_controller") is None,
            f"region {region_id} 只有行政草稿证据，不能填写 effective_controller。",
        )
        source_ids = validate_source_ids(region.get("source_ids"), f"region {region_id}.source_ids", ledger)
        geometry_source = require_mapping(region.get("geometry_source"), f"region {region_id}.geometry_source")
        dataset = require_string(geometry_source.get("dataset"), f"region {region_id}.geometry_source.dataset")
        require(dataset == "hartwell_1391", f"region {region_id} 使用未知 geometry dataset：{dataset}")
        property_name = require_string(geometry_source.get("property"), f"region {region_id}.geometry_source.property")
        expected_value = geometry_source.get("equals")
        require(expected_value is not None, f"region {region_id}.geometry_source.equals 不能为空。")

        matches: list[tuple[int, Mapping[str, Any]]] = []
        for feature_index, raw_feature in enumerate(hartwell_features):
            feature = require_mapping(raw_feature, f"Hartwell feature[{feature_index}]")
            properties = require_mapping(feature.get("properties"), f"Hartwell feature[{feature_index}].properties")
            if property_name in properties and properties[property_name] == expected_value:
                matches.append((feature_index, feature))
        require(
            len(matches) == 1,
            f"region {region_id} 的 source_feature {property_name}={expected_value!r} 应唯一命中，实际 {len(matches)} 条。",
        )
        feature_index, feature = matches[0]
        require(feature_index not in used_feature_indexes, f"Hartwell feature[{feature_index}] 被多个历史区域重复使用。")
        used_feature_indexes.add(feature_index)
        polygons = geometry_polygons(feature.get("geometry"), f"Hartwell feature[{feature_index}].geometry")
        source_properties = require_mapping(feature.get("properties"), f"Hartwell feature[{feature_index}].properties")
        source_feature = {
            "dataset": dataset,
            "feature_id": str(feature.get("id", feature_index)),
            "property": property_name,
            "value": expected_value,
        }
        if source_properties.get("CODE") is not None:
            source_feature["code"] = source_properties["CODE"]

        output_properties = {
            "id": region_id,
            "name_zh": name_zh,
            "boundary_kind": boundary_kind,
            "legal_polity": region.get("legal_polity"),
            "effective_controller": region.get("effective_controller"),
            "claim_status": claim_status,
            "historical_fit_status": region_fit_status,
            "geometry_depict_date": geometry_depict_date,
            "confidence": confidence,
            "review_status": review_status,
            "source_ids": source_ids,
            "source_feature": source_feature,
            "notes": region.get("notes", ""),
        }
        bbox = geometry_bbox(polygons)
        output_features.append(
            {
                "type": "Feature",
                "id": region_id,
                "bbox": bbox,
                "properties": output_properties,
                "geometry": feature.get("geometry"),
            }
        )
        manifest_regions.append(
            {
                "id": region_id,
                "name_zh": name_zh,
                "boundary_kind": boundary_kind,
                "confidence": confidence,
                "review_status": review_status,
                "claim_status": claim_status,
                "historical_fit_status": region_fit_status,
                "geometry_depict_date": geometry_depict_date,
                "bbox_lon_lat": bbox,
                "source_feature": source_feature,
                "source_ids": source_ids,
            }
        )

    require(output_features, "historical-regions.json 至少需要一个区域。")
    output_geojson = {
        "type": "FeatureCollection",
        "name": "ming_1629_historical_regions",
        "crs": {"type": "name", "properties": {"name": "EPSG:4326"}},
        "metadata": {
            "schema_version": SCHEMA_VERSION,
            "snapshot_date": definitions.get("snapshot_date"),
            "geometry_depict_date": geometry_depict_date,
            "claim_status": document_claim_status,
            "historical_fit_status": historical_fit_status,
            "warning": definitions.get("warning", ""),
            "geometry_role": "historical_presentation_only_not_simulation_topology",
        },
        "features": output_features,
    }
    return output_geojson, output_features, manifest_regions


def load_evidence_overlays(
    path: Path,
    source_ledger: Mapping[str, Mapping[str, str]],
    claims: Mapping[str, Mapping[str, Any]],
    admitted_claim_ids: set[str],
) -> tuple[dict[str, Any], list[dict[str, Any]], list[dict[str, Any]]]:
    document = load_json(path, "historical-overlays.json")
    ensure_schema(document, "historical-overlays.json")
    output_features: list[dict[str, Any]] = []
    manifest_overlays: list[dict[str, Any]] = []
    overlay_ids: set[str] = set()
    for index, raw_overlay in enumerate(
        require_list(document.get("overlays"), "historical-overlays.json.overlays")
    ):
        overlay = require_mapping(raw_overlay, f"historical-overlays.json.overlays[{index}]")
        overlay_id = require_id(overlay.get("id"), f"overlays[{index}].id")
        require(overlay_id not in overlay_ids, f"历史证据叠加层 ID 重复：{overlay_id}")
        overlay_ids.add(overlay_id)
        name_zh = require_string(overlay.get("name_zh"), f"overlay {overlay_id}.name_zh")
        representation = require_string(
            overlay.get("representation"), f"overlay {overlay_id}.representation"
        )
        require(
            representation == "limited_influence_area",
            f"overlay {overlay_id} 只允许 limited_influence_area，不能伪装成精确控制面。",
        )
        review_status = require_string(
            overlay.get("review_status"), f"overlay {overlay_id}.review_status"
        )
        require(review_status == "accepted", f"overlay {overlay_id} 必须是 accepted。")
        confidence = require_string(overlay.get("confidence"), f"overlay {overlay_id}.confidence")
        require(
            confidence in {"high", "medium", "low", "unknown"},
            f"overlay {overlay_id}.confidence 无效：{confidence}",
        )
        claim_ids = validate_claim_ids(
            overlay.get("claim_ids"), f"overlay {overlay_id}.claim_ids", claims, admitted_claim_ids
        )
        source_ids = validate_source_ids(
            overlay.get("source_ids"), f"overlay {overlay_id}.source_ids", source_ledger
        )
        require(
            all(claims[claim_id].get("status") != "OPEN" for claim_id in claim_ids),
            f"overlay {overlay_id} 不能引用 OPEN claim。",
        )
        polygons = geometry_polygons(overlay.get("geometry"), f"overlay {overlay_id}.geometry")
        bbox = geometry_bbox(polygons)
        properties = {
            "id": overlay_id,
            "name_zh": name_zh,
            "representation": representation,
            "claim_ids": claim_ids,
            "source_ids": source_ids,
            "confidence": confidence,
            "review_status": review_status,
            "notes": overlay.get("notes", ""),
        }
        output_features.append(
            {
                "type": "Feature",
                "id": overlay_id,
                "bbox": bbox,
                "properties": properties,
                "geometry": overlay["geometry"],
            }
        )
        manifest_overlays.append({"id": overlay_id, "bbox_lon_lat": bbox, **properties})

    output_geojson = {
        "type": "FeatureCollection",
        "name": "ming_1629_reviewed_evidence_overlays",
        "crs": {"type": "name", "properties": {"name": "EPSG:4326"}},
        "metadata": {
            "schema_version": SCHEMA_VERSION,
            "snapshot_date": document.get("snapshot_date"),
            "claim_status": "reviewed_p0_evidence",
            "geometry_role": "reviewed_historical_presentation_only_not_simulation_topology",
            "warning": document.get("warning", ""),
        },
        "features": output_features,
    }
    return output_geojson, output_features, manifest_overlays


def render_history_overlay(
    features: Sequence[Mapping[str, Any]],
    projection: WebMercatorProjection,
    style: Mapping[str, Any],
) -> Image.Image:
    fill_rgb = parse_colour(style.get("evidence_fill"), "#4F8C9A", "style.evidence_fill", "RGB")
    alpha_by_confidence = {"high": 72, "medium": 54, "low": 34, "unknown": 24}
    overlay = Image.new("RGBA", (projection.width, projection.height), (0, 0, 0, 0))
    for feature_index, feature in enumerate(features):
        properties = require_mapping(feature.get("properties"), f"output region[{feature_index}].properties")
        confidence = str(properties.get("confidence"))
        representation = str(properties.get("representation"))
        polygons = geometry_polygons(feature.get("geometry"), f"output region[{feature_index}].geometry")
        mask = Image.new("L", overlay.size, 0)
        mask_draw = ImageDraw.Draw(mask)
        for polygon in polygons:
            exterior = projected_points(polygon[0], projection)
            mask_draw.polygon(exterior, fill=255)
            for hole in polygon[1:]:
                projected_hole = projected_points(hole, projection)
                mask_draw.polygon(projected_hole, fill=0)
        fill_alpha = alpha_by_confidence.get(confidence, alpha_by_confidence["unknown"])
        fill_alpha = min(fill_alpha, 54)
        overlay.paste((*fill_rgb, fill_alpha), (0, 0, projection.width, projection.height), mask)
        # A limited influence area is deliberately diffuse: its outer edge is
        # not a claimed political boundary and therefore receives no outline.
        require(
            representation == "limited_influence_area",
            f"output evidence overlay[{feature_index}] representation 无效。",
        )
    return overlay


def load_places(
    path: Path,
    ledger: Mapping[str, Mapping[str, str]],
    projection: WebMercatorProjection,
    claims: Mapping[str, Mapping[str, Any]],
    admitted_claim_ids: set[str],
) -> list[dict[str, Any]]:
    document = load_json(path, "places.json")
    ensure_schema(document, "places.json")
    places: list[dict[str, Any]] = []
    place_ids: set[str] = set()
    for index, raw_place in enumerate(require_list(document.get("places"), "places.json.places")):
        place = require_mapping(raw_place, f"places[{index}]")
        place_id = require_id(place.get("id"), f"places[{index}].id")
        require(place_id not in place_ids, f"place ID 重复：{place_id}")
        place_ids.add(place_id)
        name_zh = require_string(place.get("name_zh"), f"place {place_id}.name_zh")
        kind = require_string(place.get("kind"), f"place {place_id}.kind")
        longitude, latitude = validate_lon_lat(
            place.get("longitude"), place.get("latitude"), f"place {place_id}"
        )
        require(
            projection.bounds_lon_lat[0] <= longitude <= projection.bounds_lon_lat[2]
            and projection.bounds_lon_lat[1] <= latitude <= projection.bounds_lon_lat[3],
            f"place {place_id} 位于地图 bounds 之外。",
        )
        source_ids = validate_source_ids(place.get("source_ids"), f"place {place_id}.source_ids", ledger)
        coordinate_epoch = require_string(
            place.get("coordinate_epoch"), f"place {place_id}.coordinate_epoch"
        )
        historical_site_status = require_string(
            place.get("historical_site_status"), f"place {place_id}.historical_site_status"
        )
        confidence = require_string(place.get("confidence"), f"place {place_id}.confidence")
        review_status = require_string(
            place.get("review_status"), f"place {place_id}.review_status"
        )
        require(
            coordinate_epoch == "modern_anchor",
            f"place {place_id} 当前只能标为 coordinate_epoch=modern_anchor。",
        )
        require(historical_site_status == "open", f"place {place_id} 必须保持 historical_site_status=open。")
        require(confidence == "unknown", f"place {place_id} 的坐标 confidence 必须保持 unknown。")
        require(review_status == "accepted", f"place {place_id} 必须是 accepted 地图点。")
        map_representation = require_string(
            place.get("map_representation"), f"place {place_id}.map_representation"
        )
        require(
            map_representation == "approximate_point",
            f"place {place_id} 只允许 approximate_point，不得画精确古城几何。",
        )
        evidence_status = require_string(
            place.get("evidence_status"), f"place {place_id}.evidence_status"
        )
        require(
            evidence_status in {"accepted_anchor", "accepted_evidence"},
            f"place {place_id} 的 evidence_status 无效：{evidence_status}",
        )
        claim_ids = validate_claim_ids(
            place.get("claim_ids"), f"place {place_id}.claim_ids", claims, admitted_claim_ids
        )
        evidence_confidence = require_string(
            place.get("evidence_confidence"), f"place {place_id}.evidence_confidence"
        )
        map_x, map_y = projection.project(longitude, latitude)
        places.append(
            {
                "id": place_id,
                "name_zh": name_zh,
                "kind": kind,
                "longitude": longitude,
                "latitude": latitude,
                "map_x": round(map_x, 6),
                "map_y": round(map_y, 6),
                "coordinate_epoch": coordinate_epoch,
                "historical_site_status": historical_site_status,
                "map_representation": map_representation,
                "evidence_status": evidence_status,
                "evidence_confidence": evidence_confidence,
                "confidence": confidence,
                "review_status": review_status,
                "claim_ids": claim_ids,
                "source_ids": source_ids,
                "notes": place.get("notes", ""),
            }
        )
    require(places, "places.json 至少需要一个地点。")
    return places


def load_routes(
    path: Path,
    ledger: Mapping[str, Mapping[str, str]],
    projection: WebMercatorProjection,
    places: Sequence[Mapping[str, Any]],
    claims: Mapping[str, Mapping[str, Any]],
    admitted_claim_ids: set[str],
) -> tuple[list[dict[str, Any]], list[str]]:
    document = load_json(path, "routes.json")
    ensure_schema(document, "routes.json")
    place_by_id = {str(place["id"]): place for place in places}
    routes: list[dict[str, Any]] = []
    suppressed_route_ids: list[str] = []
    route_ids: set[str] = set()
    for index, raw_route in enumerate(require_list(document.get("routes"), "routes.json.routes")):
        route = require_mapping(raw_route, f"routes[{index}]")
        route_id = require_id(route.get("id"), f"routes[{index}].id")
        require(route_id not in route_ids, f"route ID 重复：{route_id}")
        route_ids.add(route_id)
        from_id = require_id(route.get("from_place_id"), f"route {route_id}.from_place_id")
        to_id = require_id(route.get("to_place_id"), f"route {route_id}.to_place_id")
        require(from_id != to_id, f"route {route_id} 的起点和终点不能相同。")
        require(from_id in place_by_id, f"route {route_id} 起点引用未知 place ID：{from_id}")
        require(to_id in place_by_id, f"route {route_id} 终点引用未知 place ID：{to_id}")
        raw_path = require_list(route.get("path"), f"route {route_id}.path")
        require(len(raw_path) >= 2, f"route {route_id}.path 至少需要两个点。")
        coordinates: list[tuple[float, float]] = []
        points: list[dict[str, float]] = []
        for point_index, raw_point in enumerate(raw_path):
            coordinate = require_list(raw_point, f"route {route_id}.path[{point_index}]")
            require(len(coordinate) >= 2, f"route {route_id}.path[{point_index}] 缺少经纬度。")
            longitude, latitude = validate_lon_lat(
                coordinate[0], coordinate[1], f"route {route_id}.path[{point_index}]"
            )
            require(
                projection.bounds_lon_lat[0] <= longitude <= projection.bounds_lon_lat[2]
                and projection.bounds_lon_lat[1] <= latitude <= projection.bounds_lon_lat[3],
                f"route {route_id}.path[{point_index}] 位于地图 bounds 之外。",
            )
            coordinates.append((longitude, latitude))
            map_x, map_y = projection.project(longitude, latitude)
            points.append(
                {
                    "longitude": longitude,
                    "latitude": latitude,
                    "map_x": round(map_x, 6),
                    "map_y": round(map_y, 6),
                }
            )
        start = place_by_id[from_id]
        end = place_by_id[to_id]
        endpoint_tolerance = 1e-4
        require(
            math.isclose(coordinates[0][0], float(start["longitude"]), abs_tol=endpoint_tolerance)
            and math.isclose(coordinates[0][1], float(start["latitude"]), abs_tol=endpoint_tolerance),
            f"route {route_id}.path 首点没有落在起点 {from_id}。",
        )
        require(
            math.isclose(coordinates[-1][0], float(end["longitude"]), abs_tol=endpoint_tolerance)
            and math.isclose(coordinates[-1][1], float(end["latitude"]), abs_tol=endpoint_tolerance),
            f"route {route_id}.path 末点没有落在终点 {to_id}。",
        )
        source_ids = validate_source_ids(route.get("source_ids"), f"route {route_id}.source_ids", ledger)
        claim_status = require_string(route.get("claim_status"), f"route {route_id}.claim_status")
        confidence = require_string(route.get("confidence"), f"route {route_id}.confidence")
        review_status = require_string(
            route.get("review_status"), f"route {route_id}.review_status"
        )
        if claim_status in {"design_open", "open"} or review_status != "accepted":
            suppressed_route_ids.append(route_id)
            continue
        require(
            claim_status == "reviewed_inference",
            f"route {route_id} 当前必须保持 claim_status=reviewed_inference。",
        )
        require(
            confidence in {"medium", "low"},
            f"route {route_id} 的证据走廊 confidence 必须为 medium 或 low。",
        )
        require(review_status == "accepted", f"route {route_id} 必须是 accepted 证据走廊。")
        map_representation = require_string(
            route.get("map_representation"), f"route {route_id}.map_representation"
        )
        require(
            map_representation == "corridor",
            f"route {route_id} 只允许 corridor，不得伪装成精确道路。",
        )
        evidence_status = require_string(
            route.get("evidence_status"), f"route {route_id}.evidence_status"
        )
        require(evidence_status == "accepted", f"route {route_id} 必须是 accepted 证据走廊。")
        claim_ids = validate_claim_ids(
            route.get("claim_ids"), f"route {route_id}.claim_ids", claims, admitted_claim_ids
        )
        routes.append(
            {
                "id": route_id,
                "name_zh": require_string(route.get("name_zh"), f"route {route_id}.name_zh"),
                "kind": require_string(route.get("kind"), f"route {route_id}.kind"),
                "claim_status": claim_status,
                "map_representation": map_representation,
                "evidence_status": evidence_status,
                "from_place_id": from_id,
                "to_place_id": to_id,
                "points": points,
                "confidence": confidence,
                "review_status": review_status,
                "claim_ids": claim_ids,
                "source_ids": source_ids,
                "notes": route.get("notes", ""),
            }
        )
    require(routes, "routes.json 至少需要一条路线。")
    require(routes, "routes.json 至少需要一条已准入路线。")
    return routes, suppressed_route_ids


def render_debug_map(
    physical: Image.Image,
    history: Image.Image,
    places: Sequence[Mapping[str, Any]],
    routes: Sequence[Mapping[str, Any]],
    style: Mapping[str, Any],
    projection: WebMercatorProjection,
) -> Image.Image:
    debug = Image.alpha_composite(physical.convert("RGBA"), history)
    draw = ImageDraw.Draw(debug)
    land_route = parse_colour(style.get("land_route"), "#8A4F2C", "style.land_route", "RGBA")
    sea_route = parse_colour(style.get("sea_route"), "#235F78", "style.sea_route", "RGBA")
    marker_fill = parse_colour(style.get("place_fill"), "#F2D47A", "style.place_fill", "RGBA")
    marker_outline = parse_colour(style.get("place_outline"), "#332817", "style.place_outline", "RGBA")
    for route in routes:
        points = [
            (float(point["map_x"]), float(point["map_y"]))
            for point in require_list(route.get("points"), f"route {route.get('id')}.points")
        ]
        if route.get("kind") == "sea":
            draw_dashed_line(draw, points, sea_route, width=6, dash=16, gap=10)
        else:
            draw.line(points, fill=land_route, width=6, joint="curve")
    for place in places:
        x = float(place["map_x"])
        y = float(place["map_y"])
        radius = 9 if place.get("kind") == "capital" else 7
        draw.ellipse(
            (x - radius, y - radius, x + radius, y + radius),
            fill=marker_fill,
            outline=marker_outline,
            width=3,
        )
    left, top, width, height = projection.content_rect
    draw.rectangle((left, top, left + width, top + height), outline=(55, 66, 61, 180), width=2)
    # Debug images can escape their original folder during review.  Keep an ASCII
    # warning inside the pixels so nobody can mistake this technical render for a
    # reviewed 1629 political map, even when the adjacent manifest is missing.
    warning = "REVIEWED P0 EVIDENCE | OPEN BOUNDARIES | NO SIM TOPOLOGY"
    warning_box = draw.textbbox((0, 0), warning)
    warning_width = warning_box[2] - warning_box[0]
    warning_height = warning_box[3] - warning_box[1]
    warning_x = max(12, debug.width - warning_width - 22)
    warning_y = max(12, debug.height - warning_height - 22)
    draw.rectangle(
        (
            warning_x - 8,
            warning_y - 6,
            warning_x + warning_width + 8,
            warning_y + warning_height + 6,
        ),
        fill=(247, 239, 211, 220),
        outline=(104, 67, 42, 230),
        width=2,
    )
    draw.text((warning_x, warning_y), warning, fill=(88, 39, 28, 255))
    return debug.convert("RGB")


def build(config_path: Path) -> dict[str, Any]:
    repo_root = Path(__file__).resolve().parents[2]
    config_path = config_path.resolve()
    try:
        config_path.relative_to(repo_root)
    except ValueError as error:
        raise BuildError(f"配置必须位于项目目录内：{config_path}") from error
    config = load_json(config_path, "map-build.json")
    ensure_schema(config, "map-build.json")
    require(config.get("crs") == "EPSG:4326", "map-build.crs 必须是 EPSG:4326。")
    require(config.get("projection") == "web_mercator", "map-build.projection 必须是 web_mercator。")
    canvas = require_mapping(config.get("canvas"), "map-build.canvas")
    width = int(require_number(canvas.get("width"), "map-build.canvas.width"))
    height = int(require_number(canvas.get("height"), "map-build.canvas.height"))
    padding = int(require_number(canvas.get("padding"), "map-build.canvas.padding"))
    require(float(canvas.get("width")) == width, "canvas.width 必须是整数。")
    require(float(canvas.get("height")) == height, "canvas.height 必须是整数。")
    require(float(canvas.get("padding")) == padding, "canvas.padding 必须是整数。")
    projection = WebMercatorProjection.create(
        require_list(config.get("bounds"), "map-build.bounds"), width, height, padding
    )
    sources = require_mapping(config.get("sources"), "map-build.sources")
    required_source_keys = (
        "physical_land_shp",
        "hartwell_1391_geojson",
        "historical_regions",
        "historical_overlays",
        "evidence_ledger",
        "places",
        "routes",
        "source_ledger",
    )
    source_paths = {
        key: resolve_repo_path(repo_root, sources.get(key), f"map-build.sources.{key}")
        for key in required_source_keys
    }
    for optional_key in ("physical_lakes_shp", "physical_rivers_shp"):
        if sources.get(optional_key) is not None:
            source_paths[optional_key] = resolve_repo_path(
                repo_root,
                sources.get(optional_key),
                f"map-build.sources.{optional_key}",
            )
    for key, path in source_paths.items():
        require(path.is_file(), f"map-build.sources.{key} 文件不存在：{path}")
    output_dir = resolve_repo_path(repo_root, config.get("output_dir"), "map-build.output_dir")
    require(output_dir != repo_root, "output_dir 不能是项目根目录。")
    output_dir.parent.mkdir(parents=True, exist_ok=True)
    style = require_mapping(config.get("style", {}), "map-build.style")

    ledger = load_source_ledger(source_paths["source_ledger"])
    evidence_claims, admitted_claim_ids = load_evidence_ledger(
        source_paths["evidence_ledger"], ledger
    )
    baseline_geojson, baseline_features, baseline_regions = load_historical_regions(
        source_paths["historical_regions"], source_paths["hartwell_1391_geojson"], ledger
    )
    evidence_geojson, evidence_features, evidence_overlays = load_evidence_overlays(
        source_paths["historical_overlays"], ledger, evidence_claims, admitted_claim_ids
    )
    places = load_places(
        source_paths["places"], ledger, projection, evidence_claims, admitted_claim_ids
    )
    routes, suppressed_route_ids = load_routes(
        source_paths["routes"], ledger, projection, places, evidence_claims, admitted_claim_ids
    )
    physical, physical_counts = render_physical_base(
        source_paths["physical_land_shp"],
        source_paths.get("physical_lakes_shp"),
        source_paths.get("physical_rivers_shp"),
        projection,
        style,
    )
    # Only reviewed P0 evidence overlays are rendered.  The Hartwell geometry
    # remains an auditable research baseline in the manifest, never a visible
    # 1629 control layer.
    history = render_history_overlay(evidence_features, projection, style)
    debug = render_debug_map(physical, history, places, routes, style, projection)

    source_hashes = {
        relative_path(repo_root, config_path): sha256_file(config_path),
        **{
            relative_path(repo_root, path): sha256_file(path)
            for path in source_paths.values()
        },
    }
    output_names = {
        "physical_base": "physical-base.png",
        "history_overlay": "history-overlay.png",
        "debug_map": "debug-map.png",
        "manifest": "map-manifest.json",
        "regions_geojson": "regions.geojson",
        "build_report": "build-report.json",
    }

    with tempfile.TemporaryDirectory(prefix=".ming-map-build-", dir=output_dir.parent) as temporary:
        temporary_dir = Path(temporary)
        physical_path = temporary_dir / output_names["physical_base"]
        history_path = temporary_dir / output_names["history_overlay"]
        debug_path = temporary_dir / output_names["debug_map"]
        regions_path = temporary_dir / output_names["regions_geojson"]
        manifest_path = temporary_dir / output_names["manifest"]
        report_path = temporary_dir / output_names["build_report"]
        save_png(physical, physical_path)
        save_png(history, history_path)
        save_png(debug, debug_path)
        write_json(regions_path, evidence_geojson)

        target_paths = {key: output_dir / filename for key, filename in output_names.items()}
        preliminary_output_hashes = {
            output_names["physical_base"]: sha256_file(physical_path),
            output_names["history_overlay"]: sha256_file(history_path),
            output_names["debug_map"]: sha256_file(debug_path),
            output_names["regions_geojson"]: sha256_file(regions_path),
        }
        manifest = {
            "schema_version": SCHEMA_VERSION,
            "generator": "tools/maps/build_east_asia_map.py",
            "generator_version": GENERATOR_VERSION,
            "scenario_id": require_id(config.get("scenario_id"), "map-build.scenario_id"),
            "snapshot_date": require_string(config.get("snapshot_date"), "map-build.snapshot_date"),
            "historical_content": evidence_geojson["metadata"],
            "research_baseline": {
                "geometry_depict_date": baseline_geojson["metadata"]["geometry_depict_date"],
                "historical_fit_status": baseline_geojson["metadata"]["historical_fit_status"],
                "visible": False,
                "region_count": len(baseline_features),
            },
            "canvas": {
                "width": width,
                "height": height,
                "padding": padding,
                "content_rect": [round(value, 6) for value in projection.content_rect],
            },
            "projection": projection.manifest(),
            "assets": {
                "physical_base": godot_path(repo_root, target_paths["physical_base"]),
                "history_overlay": godot_path(repo_root, target_paths["history_overlay"]),
                "debug_map": godot_path(repo_root, target_paths["debug_map"]),
                "regions_geojson": godot_path(repo_root, target_paths["regions_geojson"]),
            },
            "asset_sha256": preliminary_output_hashes,
            "layers": [
                {
                    "id": "physical_base",
                    "kind": "raster",
                    "asset": "physical_base",
                    "role": "physical_geography_only",
                    "default_visible": True,
                },
                {
                    "id": "reviewed_evidence_overlays",
                    "kind": "transparent_raster",
                    "asset": "history_overlay",
                    "role": "reviewed_evidence_presentation_only_not_simulation_topology",
                    "default_visible": True,
                },
            ],
            "overlays": [
                {"id": "reviewed_evidence_overlays", "source": "regions", "default_visible": True},
                {"id": "routes", "source": "routes", "default_visible": True},
                {"id": "places", "source": "places", "default_visible": True},
            ],
            "regions": evidence_overlays,
            "historical_baseline_regions": baseline_regions,
            "places": places,
            "routes": routes,
            "suppressed_routes": suppressed_route_ids,
            "evidence": {
                "ledger": godot_path(repo_root, source_paths["evidence_ledger"]),
                "admitted_claim_ids": sorted(admitted_claim_ids),
                "claim_count": len(evidence_claims),
            },
            "separation_contract": {
                "physical_base_contains_political_boundaries": False,
                "historical_geometry_defines_simulation_topology": False,
                "route_polylines_define_simulation_costs": False,
                "place_screen_coordinates_are_authoritative_world_state": False,
            },
        }
        write_json(manifest_path, manifest)
        output_hashes = {
            **preliminary_output_hashes,
            output_names["manifest"]: sha256_file(manifest_path),
        }
        report = {
            "schema_version": SCHEMA_VERSION,
            "generator": "tools/maps/build_east_asia_map.py",
            "generator_version": GENERATOR_VERSION,
            "scenario_id": manifest["scenario_id"],
            "status": "technical_validation_passed_reviewed_evidence",
            "technical_validation": "passed",
            "historical_content_status": "reviewed_p0_evidence_with_open_items",
            "source_sha256": source_hashes,
            "output_sha256": output_hashes,
            "counts": {
                **physical_counts,
                "historical_regions": len(evidence_features),
                "historical_baseline_regions": len(baseline_features),
                "evidence_claims": len(evidence_claims),
                "admitted_claims": len(admitted_claim_ids),
                "suppressed_routes": len(suppressed_route_ids),
                "places": len(places),
                "routes": len(routes),
                "source_ledger_entries": len(ledger),
            },
            "validation": {
                "source_features_unique": True,
                "source_ids_known": True,
                "evidence_claims_have_sources": True,
                "open_claims_not_rendered": True,
                "hartwell_baseline_not_rendered": True,
                "open_routes_not_rendered": True,
                "geometry_structurally_valid": True,
                "place_ids_unique": True,
                "route_ids_unique": True,
                "route_endpoints_known_and_aligned": True,
                "volatile_metadata_included": False,
                "optional_physical_layers": {
                    "lakes": (
                        "rendered" if "physical_lakes_shp" in source_paths else "not_configured"
                    ),
                    "rivers": (
                        "rendered" if "physical_rivers_shp" in source_paths else "not_configured"
                    ),
                },
            },
            "notes": [
                "build-report.json 不记录自身哈希，以避免递归哈希。",
                "历史区域几何仅供表现；本构建器不读取、推导或写入 Simulation 拓扑。",
            ],
        }
        write_json(report_path, report)

        output_dir.mkdir(parents=True, exist_ok=True)
        for key, filename in output_names.items():
            os.replace(temporary_dir / filename, target_paths[key])

    return {
        "output_dir": relative_path(repo_root, output_dir),
        "output_sha256": {
            filename: sha256_file(output_dir / filename) for filename in output_names.values()
        },
        "counts": report["counts"],
    }


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--config",
        type=Path,
        default=None,
        help=f"项目内配置路径；默认 {DEFAULT_CONFIG}",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    repo_root = Path(__file__).resolve().parents[2]
    config_path = args.config or (repo_root / DEFAULT_CONFIG)
    if not config_path.is_absolute():
        config_path = repo_root / config_path
    try:
        result = build(config_path)
    except (BuildError, OSError, struct.error) as error:
        print(f"地图构建失败：{error}", file=sys.stderr)
        return 1
    print(f"地图构建完成：{result['output_dir']}")
    print(
        "构建数量："
        f"历史区域 {result['counts']['historical_regions']}，"
        f"地点 {result['counts']['places']}，路线 {result['counts']['routes']}"
    )
    for filename, digest in sorted(result["output_sha256"].items()):
        print(f"SHA256 {filename} {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
