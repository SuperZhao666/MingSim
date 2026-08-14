#!/usr/bin/env python3
"""Verify and deterministically rebuild the proven Ming UI PNG slices.

This intentionally small tool is ledger-driven.  It never writes into the
repository: ``build`` requires an output directory outside the checkout, and
``repeatability`` uses temporary directories.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import struct
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any, Iterable

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError as exc:  # pragma: no cover - environment-specific guard
    raise SystemExit("Pillow is required: python -m pip install Pillow") from exc


REPO_ROOT = Path(__file__).resolve().parents[2]
ASSET_ROOT = REPO_ROOT / "assets" / "ui" / "generated" / "ming_ui_v2"
DEFAULT_LEDGER = ASSET_ROOT / "asset-ledger.json"
REQUIRED_FIELDS = {
    "path",
    "role",
    "sha256",
    "pixel_sha256",
    "bytes",
    "width",
    "height",
    "mode",
    "generation_edit_tool",
    "date",
    "prompt_or_generation_id",
    "source_path",
    "slice",
    "operation",
    "license_decision",
}
ALLOWED_ROLES = {"source", "final", "preview", "derived"}
ALLOWED_OPERATIONS = {
    "exact_crop",
    "exact_crop_alpha_cleanup",
    "precise_object_edit",
    "deterministic_map_derive",
    "procedural_source",
    "procedural_final",
}

# Pixel-equality was checked against the current files before recording these
# rectangles.  Keeping the evidence here lets ``snapshot-ledger`` regenerate a
# complete current-hash ledger without a hand-maintained 73-entry rewrite.
PROVEN_EXACT_CROPS: dict[str, tuple[str, int, int, int, int]] = {
    "badges/badge-design.png": ("source/status-paper-tags-transparent.png", 296, 0, 295, 887),
    "badges/badge-fact.png": ("source/status-paper-tags-transparent.png", 0, 0, 296, 887),
    "badges/badge-open.png": ("source/status-paper-tags-transparent.png", 591, 0, 296, 887),
    "badges/badge-selected.png": ("source/status-paper-tags-transparent.png", 1478, 0, 296, 887),
    "badges/badge-urgent.png": ("source/status-paper-tags-transparent.png", 887, 0, 296, 887),
    "badges/badge-warning.png": ("source/status-paper-tags-transparent.png", 1183, 0, 295, 887),
    "icons/icon-decree.png": ("source/functional-paper-icons-source.png", 1663, 0, 416, 756),
    "icons/icon-memorial.png": ("source/functional-paper-icons-source.png", 0, 0, 416, 756),
    "icons/icon-message.png": ("source/functional-paper-icons-source.png", 1247, 0, 416, 756),
    "icons/icon-military.png": ("source/functional-paper-icons-source.png", 416, 0, 416, 756),
    "icons/icon-treasury.png": ("source/functional-paper-icons-source.png", 832, 0, 415, 756),
    "memorials/memorial-normal.png": ("source/memorial-paper-states-source.png", 0, 0, 646, 254),
    "memorials/memorial-hover.png": ("source/memorial-paper-states-source.png", 646, 0, 646, 254),
    "memorials/memorial-pressed.png": ("source/memorial-paper-states-source.png", 1292, 0, 646, 254),
    "memorials/memorial-selected.png": ("source/memorial-paper-states-source.png", 1938, 0, 646, 258),
    "memorials/memorial-disabled.png": ("source/memorial-paper-states-source.png", 2584, 0, 646, 258),
    "parts/checkbox-off.png": ("source/small-paper-parts-source.png", 836, 418, 418, 418),
    "parts/checkbox-on.png": ("source/small-paper-parts-source.png", 0, 836, 418, 418),
    "parts/divider.png": ("source/small-paper-parts-source.png", 0, 0, 418, 418),
    "parts/focus-frame.png": ("source/small-paper-parts-source.png", 0, 418, 418, 418),
    "parts/map-selection-ring.png": ("source/small-paper-parts-source.png", 836, 836, 418, 418),
    "parts/scrollbar-thumb.png": ("source/small-paper-parts-source.png", 836, 0, 418, 418),
    "parts/scrollbar-track.png": ("source/small-paper-parts-source.png", 418, 0, 418, 418),
    "parts/toggle-off.png": ("source/small-paper-parts-source.png", 418, 836, 418, 418),
    "parts/tooltip-frame.png": ("source/small-paper-parts-source.png", 418, 418, 418, 418),
    "speed/speed-1x-selected.png": ("source/speed-bamboo-states-transparent.png", 444, 0, 443, 887),
    "speed/speed-2x-selected.png": ("source/speed-bamboo-states-transparent.png", 887, 0, 443, 887),
    "speed/speed-4x-selected.png": ("source/speed-bamboo-states-transparent.png", 1330, 0, 444, 887),
    "speed/speed-pause-selected.png": ("source/speed-bamboo-states-transparent.png", 0, 0, 444, 887),
    "buttons/primary-normal.png": ("source/primary-paper-states-source.png", 0, 0, 408, 156),
    "buttons/primary-hover.png": ("source/primary-paper-states-source.png", 408, 0, 408, 156),
    "buttons/primary-pressed.png": ("source/primary-paper-states-source.png", 816, 0, 408, 156),
    "buttons/primary-selected.png": ("source/primary-paper-states-source.png", 1224, 0, 408, 156),
    "buttons/primary-disabled.png": ("source/primary-paper-states-source.png", 1632, 0, 408, 156),
    "buttons/seal-normal.png": ("source/seal-paper-states-source.png", 0, 0, 522, 239),
    "buttons/seal-hover.png": ("source/seal-paper-states-source.png", 522, 0, 522, 239),
    "buttons/seal-pressed.png": ("source/seal-paper-states-source.png", 1044, 0, 522, 239),
    "buttons/seal-disabled.png": ("source/seal-paper-states-source.png", 1566, 0, 522, 239),
    "tabs/tab-normal.png": ("source/tab-paper-states-source.png", 0, 0, 441, 185),
    "tabs/tab-hover.png": ("source/tab-paper-states-source.png", 441, 0, 441, 185),
    "tabs/tab-pressed.png": ("source/tab-paper-states-source.png", 882, 0, 441, 185),
    "tabs/tab-selected.png": ("source/tab-paper-states-source.png", 1323, 0, 441, 185),
    "tabs/tab-disabled.png": ("source/tab-paper-states-source.png", 1764, 0, 441, 185),
}

IMAGEGEN_DESK_MAP = "backgrounds/ming-imperial-study-desk-map.png"
PROCEDURAL_SOURCE_SIZES: dict[str, tuple[int, int]] = {
    "source/functional-paper-icons-source.png": (2079, 756),
    "source/memorial-paper-states-source.png": (3230, 258),
    "source/primary-paper-states-source.png": (2040, 156),
    "source/seal-paper-states-source.png": (2088, 239),
    "source/small-paper-parts-source.png": (1254, 1254),
    "source/speed-bamboo-states-transparent.png": (1774, 887),
    "source/status-paper-tags-transparent.png": (1774, 887),
    "source/tab-paper-states-source.png": (2205, 185),
}
PROCEDURAL_FINALS = {"cards/ming-booklet-paper-ninepatch.png": (1254, 1254)}
PROCEDURAL_GENERATION_ID = "ming-paper-ink-procedural-v2"
PROVENANCE_EVIDENCE = "assets/ui/generated/ming_ui_v2/ASSET_PROVENANCE.md"
FORMAL_MAP_ROOT = REPO_ROOT / "assets" / "maps" / "generated"
DERIVED_MAPS: dict[str, tuple[str, str]] = {
    "maps/ming_1629-physical.png": (
        "assets/maps/generated/ming_1629/physical-base.png",
        "assets/maps/generated/ming_1629/map-manifest.json",
    ),
    "maps/ming_1629_liaoxi-physical.png": (
        "assets/maps/generated/ming_1629_liaoxi/physical-base.png",
        "assets/maps/generated/ming_1629_liaoxi/map-manifest.json",
    ),
}


class LedgerError(RuntimeError):
    """Raised when ledger evidence does not match the current asset tree."""


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def image_pixel_sha256(image: Image.Image) -> str:
    """Hash decoded pixels plus mode and dimensions, independent of PNG chunks."""

    digest = hashlib.sha256()
    digest.update(image.mode.encode("ascii"))
    digest.update(struct.pack(">II", image.width, image.height))
    digest.update(image.tobytes())
    return digest.hexdigest()


def path_is_within(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def checked_output_root(raw_path: str | Path) -> Path:
    output = Path(raw_path).expanduser().resolve()
    if path_is_within(output, REPO_ROOT.resolve()):
        raise LedgerError(
            f"refusing repository output path: {output}; choose a temp/external directory"
        )
    return output


def load_ledger(path: Path) -> dict[str, Any]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise LedgerError(f"ledger not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise LedgerError(f"invalid ledger JSON: {exc}") from exc
    if payload.get("schema_version") != 1:
        raise LedgerError("unsupported or missing schema_version; expected 1")
    if not isinstance(payload.get("assets"), list):
        raise LedgerError("ledger assets must be an array")
    return payload


def delivery_png_paths() -> list[Path]:
    """Return PNGs that Git will actually deliver, excluding local old batches."""

    result = subprocess.run(
        ["git", "ls-files", "--", ASSET_ROOT.relative_to(REPO_ROOT).as_posix()],
        cwd=REPO_ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    paths: list[Path] = []
    for raw in result.stdout.splitlines():
        candidate = REPO_ROOT / Path(raw)
        if candidate.suffix.lower() == ".png" and candidate.is_file():
            paths.append(candidate)
    return sorted(paths)


def snapshot_ledger_payload() -> dict[str, Any]:
    """Create a full ledger from current files plus the proven operation map."""

    assets: list[dict[str, Any]] = []
    for path in delivery_png_paths():
        if not path.is_file():
            continue
        rel = path.relative_to(ASSET_ROOT).as_posix()
        with Image.open(path) as image:
            width, height, mode = image.width, image.height, image.mode
            pixel_hash = image_pixel_sha256(image)

        extra: dict[str, Any] = {}
        if rel in PROCEDURAL_SOURCE_SIZES:
            role = "source"
            operation = "procedural_source"
            source_path = "tools/ui/ming_ui_assets.py"
            slice_metadata = None
            tool = "Pillow deterministic geometry renderer"
            date = "2026-08-14"
            generation = PROCEDURAL_GENERATION_ID
            license_decision = (
                "PROJECT_ORIGINAL_MIT - generated entirely by repository code; "
                f"redistribution authorized by repository LICENSE and documented in {PROVENANCE_EVIDENCE}"
            )
            extra = {"evidence_path": PROVENANCE_EVIDENCE, "source_url": "tools/ui/ming_ui_assets.py"}
        elif rel in PROVEN_EXACT_CROPS:
            role = "derived"
            operation = "exact_crop_alpha_cleanup" if mode == "RGBA" else "exact_crop"
            source_path, x, y, crop_width, crop_height = PROVEN_EXACT_CROPS[rel]
            slice_metadata = {
                "x": x,
                "y": y,
                "width": crop_width,
                "height": crop_height,
                "method": "pixel_exact_crop",
            }
            tool = "Pillow exact crop + deterministic one-pixel alpha matte cleanup"
            date = "2026-08-14"
            generation = PROCEDURAL_GENERATION_ID
            license_decision = (
                "DERIVED_FROM_PROJECT_ORIGINAL_MIT_SOURCE - source is rebuilt by repository "
                f"code and redistribution is authorized by LICENSE; see {PROVENANCE_EVIDENCE}"
            )
            extra = {"evidence_path": PROVENANCE_EVIDENCE}
        elif rel in DERIVED_MAPS:
            role = "derived"
            operation = "deterministic_map_derive"
            source_path, source_manifest = DERIVED_MAPS[rel]
            slice_metadata = {
                "x": 0,
                "y": 0,
                "width": width,
                "height": height,
                "method": "coordinate_preserving_parchment_muted_v1",
            }
            tool = "tools/ui/ming_ui_assets.py derive-map (Pillow)"
            date = "2026-08-14"
            generation = "parchment-muted-v1"
            license_decision = (
                "DERIVED_FROM_FORMAL_MAP - Natural Earth public-domain physical data; "
                "UI-only presentation derivative, not historical or simulation topology"
            )
            extra = {
                "source_manifest": source_manifest,
                "evidence_path": PROVENANCE_EVIDENCE,
                "source_url": "https://www.naturalearthdata.com/about/terms-of-use/",
            }
        elif rel == IMAGEGEN_DESK_MAP:
            role = "final"
            operation = "precise_object_edit"
            source_path = None
            slice_metadata = None
            tool = "built-in ImageGen"
            date = "2026-08-14"
            generation = "exec-46924c8c-6d4a-46cf-94ed-b17ed57ffa25"
            license_decision = (
                "PROJECT_GENERATED - built-in ImageGen output approved for this project; "
                "no third-party source is asserted"
            )
            extra = {
                "edit_method": "precise-object-edit",
                "evidence_path": PROVENANCE_EVIDENCE,
                "source_url": "https://openai.com/policies/terms-of-use/",
                "provenance_note": (
                    "Removed pseudo-glyph details and mechanical/metal props from the "
                    "previous composition; overwritten source version is not retained here."
                ),
            }
        elif rel in PROCEDURAL_FINALS:
            role = "final"
            operation = "procedural_final"
            source_path = "tools/ui/ming_ui_assets.py"
            slice_metadata = None
            tool = "Pillow deterministic geometry renderer"
            date = "2026-08-14"
            generation = PROCEDURAL_GENERATION_ID
            license_decision = (
                "PROJECT_ORIGINAL_MIT - generated entirely by repository code; "
                f"redistribution authorized by repository LICENSE and documented in {PROVENANCE_EVIDENCE}"
            )
            extra = {"evidence_path": PROVENANCE_EVIDENCE, "source_url": "tools/ui/ming_ui_assets.py"}
        else:
            raise LedgerError(f"unclassified delivered PNG has no closed provenance: {rel}")

        entry = {
            "path": rel,
            "role": role,
            "sha256": file_sha256(path),
            "pixel_sha256": pixel_hash,
            "bytes": path.stat().st_size,
            "width": width,
            "height": height,
            "mode": mode,
            "generation_edit_tool": tool,
            "date": date,
            "prompt_or_generation_id": generation,
            "source_path": source_path,
            "slice": slice_metadata,
            "operation": operation,
            "license_decision": license_decision,
        }
        entry.update(extra)
        assets.append(entry)

    counts = dict(sorted(Counter(asset["role"] for asset in assets).items()))
    return {
        "schema_version": 1,
        "asset_root": "assets/ui/generated/ming_ui_v2",
        "ledger_created": "2026-08-14",
        "hash_contract": {
            "sha256": "SHA-256 of current PNG bytes, including encoder metadata",
            "pixel_sha256": (
                "SHA-256 of decoded mode + big-endian width/height + raw pixel bytes"
            ),
        },
        "provenance_policy": "Every delivered PNG must have closed, reviewable redistribution evidence; OPEN is forbidden.",
        "role_counts": counts,
        "coverage": {
            "inventory_png_count": len(assets),
            "pixel_exact_crop_count": sum(
                asset["operation"] == "exact_crop" for asset in assets
            ),
            "alpha_cleanup_crop_count": sum(
                asset["operation"] == "exact_crop_alpha_cleanup" for asset in assets
            ),
            "precise_object_edit_count": sum(
                asset["operation"] == "precise_object_edit" for asset in assets
            ),
            "procedural_source_count": sum(
                asset["operation"] == "procedural_source" for asset in assets
            ),
            "procedural_final_count": sum(
                asset["operation"] == "procedural_final" for asset in assets
            ),
            "preview_count": sum(asset["role"] == "preview" for asset in assets),
            "deterministic_map_derive_count": sum(
                asset["operation"] == "deterministic_map_derive" for asset in assets
            ),
        },
        "assets": assets,
    }


def write_ledger_snapshot(path: Path) -> int:
    resolved = path.resolve()
    if resolved != DEFAULT_LEDGER.resolve():
        raise LedgerError(
            f"snapshot-ledger may only write the canonical ledger: {DEFAULT_LEDGER}"
        )
    payload = snapshot_ledger_payload()
    # 显式 newline="\n"：Windows 文本模式默认把 \n 写成 \r\n，会让账本字节
    # 随平台漂移；与 .gitattributes 的 text eol=lf 一起保证重建后工作树字节稳定。
    path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    return len(payload["assets"])


def entry_map(payload: dict[str, Any]) -> dict[str, dict[str, Any]]:
    result: dict[str, dict[str, Any]] = {}
    for entry in payload["assets"]:
        if not isinstance(entry, dict):
            raise LedgerError("each asset entry must be an object")
        missing = REQUIRED_FIELDS - set(entry)
        if missing:
            raise LedgerError(
                f"{entry.get('path', '<unknown>')}: missing fields {sorted(missing)}"
            )
        rel = entry["path"]
        if not isinstance(rel, str) or not rel.endswith(".png") or "\\" in rel:
            raise LedgerError(f"invalid canonical PNG path: {rel!r}")
        if rel in result:
            raise LedgerError(f"duplicate ledger path: {rel}")
        if entry["role"] not in ALLOWED_ROLES:
            raise LedgerError(f"{rel}: invalid role {entry['role']!r}")
        if entry["operation"] not in ALLOWED_OPERATIONS:
            raise LedgerError(f"{rel}: invalid operation {entry['operation']!r}")
        for field in ("generation_edit_tool", "date", "prompt_or_generation_id", "license_decision"):
            value = entry.get(field)
            if not isinstance(value, str) or not value.strip() or "OPEN" in value.upper():
                raise LedgerError(f"{rel}: {field} must contain closed provenance, not OPEN")
        evidence_path = entry.get("evidence_path")
        if not isinstance(evidence_path, str) or not (REPO_ROOT / evidence_path).is_file():
            raise LedgerError(f"{rel}: missing repository provenance evidence")
        result[rel] = entry
    return result


def exact_crop(entry: dict[str, Any], source_root: Path = ASSET_ROOT) -> Image.Image:
    source_rel = entry["source_path"]
    rect = entry["slice"]
    if not isinstance(source_rel, str) or not isinstance(rect, dict):
        raise LedgerError(f"{entry['path']}: exact_crop requires source_path and slice")
    expected_keys = {"x", "y", "width", "height", "method"}
    if set(rect) != expected_keys or rect["method"] != "pixel_exact_crop":
        raise LedgerError(f"{entry['path']}: invalid exact_crop slice metadata")
    source = source_root / Path(source_rel)
    if not source.is_file():
        raise LedgerError(f"{entry['path']}: missing source {source_rel}")
    x, y, width, height = (
        int(rect["x"]),
        int(rect["y"]),
        int(rect["width"]),
        int(rect["height"]),
    )
    with Image.open(source) as image:
        if x < 0 or y < 0 or width <= 0 or height <= 0:
            raise LedgerError(f"{entry['path']}: invalid crop rectangle")
        if x + width > image.width or y + height > image.height:
            raise LedgerError(f"{entry['path']}: crop rectangle exceeds source")
        return image.crop((x, y, x + width, y + height)).copy()


def green_fringe_alpha_cleanup(image: Image.Image) -> Image.Image:
    """Deterministically contract one contaminated matte pixel and despill green.

    The old chroma batch contains red/yellow/green one-pixel outlines, including
    fully opaque spill that colour-only filters cannot identify.  Eroding just
    the alpha boundary removes that outer contamination while preserving the
    paper/ink interior.  The follow-up despill only affects translucent green.
    """

    rgba = image.convert("RGBA")
    red_band, green_band, blue_band, alpha_band = rgba.split()
    contracted_alpha = alpha_band.filter(ImageFilter.MinFilter(3))
    rgba.putalpha(contracted_alpha)
    contracted_alpha.close()
    alpha_band.close()
    red_band.close()
    green_band.close()
    blue_band.close()
    cleaned: list[tuple[int, int, int, int]] = []
    pixels = (
        rgba.get_flattened_data()
        if hasattr(rgba, "get_flattened_data")
        else rgba.getdata()
    )
    for red, green, blue, alpha in pixels:
        dominance = green - max(red, blue)
        if alpha < 32:
            red, green, blue, alpha = 0, 0, 0, 0
        elif 0 < alpha < 245 and dominance > 16:
            reduction = min(255, (dominance - 16) * 4)
            alpha = alpha * (255 - reduction) // 255
        cleaned.append((red, green, blue, alpha))
    rgba.putdata(cleaned)
    return rgba


def _state_from_path(rel: str) -> str:
    stem = Path(rel).stem
    for state in ("disabled", "selected", "pressed", "hover", "normal"):
        if state in stem:
            return state
    return "normal"


def _state_palette(state: str) -> tuple[tuple[int, int, int, int], tuple[int, int, int, int]]:
    fills = {
        "normal": (232, 216, 178, 255),
        "hover": (247, 233, 195, 255),
        "pressed": (202, 181, 143, 255),
        "selected": (239, 218, 171, 255),
        "disabled": (205, 199, 184, 255),
    }
    accents = {
        "normal": (78, 61, 43, 255),
        "hover": (98, 65, 37, 255),
        "pressed": (62, 48, 35, 255),
        "selected": (151, 45, 31, 255),
        "disabled": (104, 92, 80, 255),
    }
    return fills[state], accents[state]


def _draw_paper_fibres(
    draw: ImageDraw.ImageDraw,
    box: tuple[int, int, int, int],
    colour: tuple[int, int, int, int],
    count: int = 18,
) -> None:
    left, top, right, bottom = box
    width = max(1, right - left)
    height = max(1, bottom - top)
    for index in range(count):
        y = top + 3 + ((index * 37 + 11) % max(4, height - 6))
        x = left + 4 + ((index * 53 + 7) % max(5, width // 3))
        length = max(5, width // 5 + (index * 17) % max(6, width // 4))
        draw.line((x, y, min(right - 3, x + length), y), fill=colour, width=max(1, min(width, height) // 180))


def _draw_paper_control(
    image: Image.Image,
    state: str,
    inset_ratio: float,
    radius_ratio: float,
    cinnabar_marker: bool = True,
) -> tuple[ImageDraw.ImageDraw, tuple[int, int, int, int]]:
    draw = ImageDraw.Draw(image)
    width, height = image.size
    inset = max(6, int(min(width, height) * inset_ratio))
    box = (inset, inset, width - inset - 1, height - inset - 1)
    fill, accent = _state_palette(state)
    radius = max(4, int(min(width, height) * radius_ratio))
    border_width = max(2, int(min(width, height) * 0.026))
    shadow = (box[0] + border_width, box[1] + border_width, box[2] + border_width, box[3] + border_width)
    draw.rounded_rectangle(shadow, radius=radius, fill=(37, 28, 20, 72))
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=accent, width=border_width)
    inner = (box[0] + border_width * 2, box[1] + border_width * 2, box[2] - border_width * 2, box[3] - border_width * 2)
    draw.rounded_rectangle(inner, radius=max(2, radius - border_width), outline=(117, 89, 56, 110), width=max(1, border_width // 2))
    _draw_paper_fibres(draw, inner, (108, 84, 57, 38))
    if cinnabar_marker:
        marker_width = max(4, (box[2] - box[0]) // 35)
        marker = (box[0] + border_width * 3, box[1] + border_width * 3, box[0] + border_width * 3 + marker_width, box[3] - border_width * 3)
        marker_colour = (150, 39, 28, 215 if state != "disabled" else 120)
        draw.rounded_rectangle(marker, radius=max(2, marker_width // 2), fill=marker_colour)
    return draw, box


def render_procedural_component(rel: str, width: int, height: int) -> Image.Image:
    """Render one project-original paper/ink/wood/bamboo UI component."""

    image = Image.new("RGBA", (width, height), (0, 0, 0, 0))
    state = _state_from_path(rel)
    name = Path(rel).name

    if rel.startswith("buttons/primary"):
        draw, box = _draw_paper_control(image, state, 0.065, 0.12)
        y = (box[1] + box[3]) // 2
        draw.line((box[0] + width // 5, y, box[2] - width // 8, y), fill=(62, 47, 33, 190), width=max(2, height // 30))
    elif rel.startswith("buttons/seal"):
        draw, box = _draw_paper_control(image, state, 0.075, 0.1, False)
        # 朱批按钮=纸签+右侧朱砂方印；左侧留纸面给"朱批候核"浅色文字，
        # 印面只占右侧约四分之一，避免深红底吃掉浅字。
        seal_size = max(20, min(width, height) // 4)
        seal_x = box[2] - seal_size - max(10, width // 40)
        seal_y = (box[1] + box[3] - seal_size) // 2
        seal_fill = (139, 45, 34, 150 if state == "disabled" else 242)
        draw.rounded_rectangle(
            (seal_x, seal_y, seal_x + seal_size, seal_y + seal_size),
            radius=max(3, seal_size // 10), fill=seal_fill,
            outline=(92, 30, 23, 255), width=max(2, seal_size // 16))
        ink = (245, 226, 193, 120 if state == "disabled" else 255)
        draw.line((seal_x + seal_size // 6, seal_y + seal_size // 2,
                   seal_x + seal_size * 5 // 6, seal_y + seal_size // 2), fill=ink, width=max(2, seal_size // 12))
        draw.line((seal_x + seal_size // 2, seal_y + seal_size // 6,
                   seal_x + seal_size // 2, seal_y + seal_size * 5 // 6), fill=ink, width=max(2, seal_size // 12))
    elif rel.startswith("tabs/"):
        draw, box = _draw_paper_control(image, state, 0.065, 0.14)
        notch = max(8, height // 6)
        draw.polygon(((box[2] - notch, box[1]), (box[2], box[1]), (box[2], box[1] + notch)), fill=(173, 53, 37, 190 if state != "disabled" else 90))
        draw.line((box[0] + width // 5, (box[1] + box[3]) // 2, box[2] - width // 6, (box[1] + box[3]) // 2), fill=(70, 54, 38, 210), width=max(2, height // 35))
    elif rel.startswith("badges/"):
        draw = ImageDraw.Draw(image)
        inset_x = max(12, width // 7)
        inset_y = max(18, height // 12)
        box = (inset_x, inset_y, width - inset_x, height - inset_y)
        fill, accent = _state_palette("selected" if "selected" in name or "urgent" in name else "normal")
        if "open" in name:
            fill, accent = (225, 215, 188, 255), (74, 95, 90, 255)
        elif "design" in name:
            fill, accent = (231, 216, 178, 255), (126, 79, 36, 255)
        elif "warning" in name:
            fill, accent = (229, 202, 160, 255), (142, 55, 34, 255)
        draw.rounded_rectangle(box, radius=max(8, width // 9), fill=fill, outline=accent, width=max(4, width // 30))
        draw.ellipse((width // 3, inset_y // 3, width * 2 // 3, inset_y * 5 // 3), fill=(92, 64, 39, 255))
        cx, cy = width // 2, height // 2
        ring = max(18, width // 5)
        draw.ellipse((cx - ring, cy - ring, cx + ring, cy + ring), outline=accent, width=max(5, width // 25))
        if "open" not in name:
            draw.line((cx - ring // 2, cy, cx + ring // 2, cy), fill=accent, width=max(5, width // 25))
    elif rel.startswith("icons/"):
        draw = ImageDraw.Draw(image)
        margin = max(16, width // 12)
        box = (margin, margin, width - margin, height - margin)
        draw.rounded_rectangle(box, radius=max(12, width // 10), fill=(233, 219, 184, 250), outline=(74, 58, 42, 255), width=max(5, width // 45))
        cx, cy = width // 2, height // 2
        ink = (54, 44, 33, 255)
        red = (153, 44, 31, 230)
        stroke = max(6, width // 28)
        if "military" in name:
            draw.line((cx - width // 5, cy + height // 6, cx + width // 5, cy - height // 6), fill=ink, width=stroke)
            draw.line((cx - width // 5, cy - height // 6, cx + width // 5, cy + height // 6), fill=ink, width=stroke)
        elif "treasury" in name:
            draw.ellipse((cx - width // 5, cy - height // 7, cx + width // 5, cy + height // 7), outline=ink, width=stroke)
            draw.line((cx - width // 7, cy, cx + width // 7, cy), fill=red, width=stroke)
        elif "message" in name:
            draw.rectangle((cx - width // 4, cy - height // 8, cx + width // 4, cy + height // 8), outline=ink, width=stroke)
            draw.line((cx - width // 4, cy - height // 8, cx, cy + height // 18, cx + width // 4, cy - height // 8), fill=red, width=stroke)
        elif "decree" in name:
            draw.rectangle((cx - width // 5, cy - height // 4, cx + width // 5, cy + height // 4), outline=ink, width=stroke)
            for offset in (-1, 0, 1):
                y = cy + offset * height // 12
                draw.line((cx - width // 8, y, cx + width // 8, y), fill=red if offset == 1 else ink, width=max(3, stroke // 2))
        else:
            draw.rectangle((cx - width // 5, cy - height // 5, cx + width // 5, cy + height // 5), outline=ink, width=stroke)
            draw.line((cx - width // 8, cy, cx + width // 8, cy), fill=red, width=stroke)
    elif rel.startswith("speed/"):
        draw = ImageDraw.Draw(image)
        margin_x = max(18, width // 8)
        margin_y = max(30, height // 12)
        box = (margin_x, margin_y, width - margin_x, height - margin_y)
        draw.rounded_rectangle(box, radius=max(10, width // 10), fill=(181, 142, 77, 255), outline=(74, 54, 31, 255), width=max(5, width // 35))
        for y in range(box[1] + height // 8, box[3], max(18, height // 7)):
            draw.line((box[0] + 8, y, box[2] - 8, y), fill=(113, 82, 43, 130), width=max(2, width // 80))
        bars = 2 if "pause" in name else 1 if "1x" in name else 2 if "2x" in name else 4
        span = max(12, width // (bars * 3 + 2))
        total = bars * span + (bars - 1) * span
        start = (width - total) // 2
        for index in range(bars):
            x = start + index * span * 2
            draw.rounded_rectangle((x, height // 3, x + span, height * 2 // 3), radius=max(3, span // 3), fill=(54, 43, 29, 235))
    elif rel.startswith("memorials/"):
        draw = ImageDraw.Draw(image)
        fill, accent = _state_palette(state)
        margin = max(8, min(width, height) // 26)
        box = (margin, margin, width - margin - 1, height - margin - 1)
        radius = max(10, min(width, height) // 16)
        draw.rounded_rectangle(
            (box[0] + 3, box[1] + 3, box[2] + 3, box[3] + 3),
            radius=radius, fill=(37, 28, 20, 70))
        draw.rounded_rectangle(box, radius=radius, fill=fill, outline=accent,
                               width=max(2, min(width, height) // 110))
        inner = (box[0] + max(6, width // 48), box[1] + max(6, height // 30),
                 box[2] - max(6, width // 48), box[3] - max(6, height // 30))
        draw.rounded_rectangle(inner, radius=max(6, radius - 3),
                               outline=(117, 89, 56, 110), width=max(1, min(width, height) // 230))
        # 奏疏正文区：左侧约 60% 画四道竖排墨栏，Alpha 压低避免压住标题文字
        ink_alpha = 72 if state != "disabled" else 42
        columns = 4
        left = inner[0] + (inner[2] - inner[0]) // 14
        right = inner[0] + (inner[2] - inner[0]) * 3 // 5
        column_width = max(2, width // 230)
        for index in range(columns):
            x = left + (right - left) * index // max(1, columns - 1)
            draw.line((x, inner[1] + height // 10, x, inner[3] - height // 10),
                      fill=(62, 49, 35, ink_alpha), width=column_width)
        # 朱批方印位于左下，避开右上角状态徽章与标题区
        seal_size = max(38, min(width, height) // 5)
        seal_x = inner[0] + width // 20
        seal_y = inner[3] - seal_size - height // 12
        seal_alpha = 215 if state != "disabled" else 105
        draw.rounded_rectangle(
            (seal_x, seal_y, seal_x + seal_size, seal_y + seal_size),
            radius=max(4, seal_size // 16), fill=(150, 39, 28, seal_alpha),
            outline=(92, 30, 23, 255), width=max(2, seal_size // 22))
        # 印文：两道留白十字，模拟朱砂印面刻痕
        draw.line((seal_x + seal_size // 8, seal_y + seal_size // 2,
                   seal_x + seal_size * 7 // 8, seal_y + seal_size // 2),
                  fill=(255, 236, 214, seal_alpha * 2 // 3), width=max(2, seal_size // 24))
        draw.line((seal_x + seal_size // 2, seal_y + seal_size // 8,
                   seal_x + seal_size // 2, seal_y + seal_size * 7 // 8),
                  fill=(255, 236, 214, seal_alpha * 2 // 3), width=max(2, seal_size // 24))
    elif rel.startswith("parts/"):
        draw = ImageDraw.Draw(image)
        margin = max(24, width // 9)
        ink = (61, 49, 35, 245)
        red = (151, 43, 31, 230)
        paper = (231, 216, 180, 230)
        stroke = max(6, width // 32)
        if "divider" in name:
            draw.line((margin, height // 2, width - margin, height // 2), fill=ink, width=stroke)
            draw.ellipse((width // 2 - stroke, height // 2 - stroke, width // 2 + stroke, height // 2 + stroke), fill=red)
        elif "checkbox" in name:
            draw.rounded_rectangle((margin, margin, width - margin, height - margin), radius=width // 12, fill=paper, outline=ink, width=stroke)
            if "on" in name:
                draw.line((width // 4, height // 2, width * 5 // 12, height * 2 // 3, width * 3 // 4, height // 3), fill=red, width=stroke * 2, joint="curve")
        elif "selection" in name or "focus" in name:
            draw.ellipse((margin, margin, width - margin, height - margin), outline=red if "selection" in name else ink, width=stroke * 2)
        elif "scrollbar" in name:
            if "track" in name:
                draw.rounded_rectangle((width * 2 // 5, margin, width * 3 // 5, height - margin), radius=width // 12, fill=(101, 75, 43, 140))
            else:
                draw.rounded_rectangle((width // 3, margin, width * 2 // 3, height - margin), radius=width // 10, fill=(131, 92, 49, 245), outline=ink, width=stroke)
        elif "toggle" in name:
            draw.rounded_rectangle((margin, height // 3, width - margin, height * 2 // 3), radius=height // 6, fill=paper, outline=ink, width=stroke)
            draw.ellipse((margin + stroke, height // 3 + stroke, height * 2 // 3 - stroke, height * 2 // 3 - stroke), fill=(93, 74, 52, 255))
        else:
            draw.rounded_rectangle((margin, margin, width - margin, height - margin), radius=width // 12, fill=paper, outline=ink, width=stroke)
            draw.polygon(((width - margin * 2, margin), (width - margin, margin), (width - margin, margin * 2)), fill=red)
    else:
        _draw_paper_control(image, state, 0.05, 0.08)
    return image


def render_procedural_source(rel: str) -> Image.Image:
    size = PROCEDURAL_SOURCE_SIZES.get(rel)
    if size is None:
        raise LedgerError(f"unknown procedural source: {rel}")
    sheet = Image.new("RGBA", size, (0, 0, 0, 0))
    for child_rel, (source_rel, x, y, width, height) in PROVEN_EXACT_CROPS.items():
        if source_rel != rel:
            continue
        component = render_procedural_component(child_rel, width, height)
        try:
            sheet.alpha_composite(component, (x, y))
        finally:
            component.close()
    return sheet


def render_procedural_final(rel: str) -> Image.Image:
    size = PROCEDURAL_FINALS.get(rel)
    if size is None:
        raise LedgerError(f"unknown procedural final: {rel}")
    image = Image.new("RGBA", size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    width, height = size
    margin = 44
    box = (margin, margin, width - margin, height - margin)
    draw.rounded_rectangle(box, radius=36, fill=(238, 223, 184, 255), outline=(83, 61, 42, 255), width=12)
    draw.rounded_rectangle((margin + 26, margin + 26, width - margin - 26, height - margin - 26),
                           radius=24, outline=(162, 69, 49, 160), width=4)
    _draw_paper_fibres(draw, (margin + 30, margin + 30, width - margin - 30, height - margin - 30),
                       (112, 84, 52, 30), 40)
    # 右上折角必须完全落在 StyleBoxTexture 的固定区内：右 margin=330、上 margin=210。
    # 若折角进入中央拉伸区，面板缩放时折角会被拉变形。
    fold_left = width - 330
    fold_top = 44
    draw.polygon(((fold_left, fold_top), (width - margin, fold_top), (width - margin, 210)),
                 fill=(210, 225, 201, 220))
    draw.line((fold_left, fold_top, width - margin, 210), fill=(78, 105, 96, 180), width=6)
    return image


def save_deterministic_png(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    # Fixed encoder options and no inherited ancillary PNG metadata.
    image.save(destination, format="PNG", optimize=False, compress_level=9)


def load_formal_map_inputs(
    physical_base: Path, manifest_path: Path
) -> tuple[Image.Image, tuple[int, int, int, int], dict[str, Any]]:
    physical_base = physical_base.resolve()
    manifest_path = manifest_path.resolve()
    formal_root = FORMAL_MAP_ROOT.resolve()
    if not path_is_within(physical_base, formal_root):
        raise LedgerError(f"physical base is not under {FORMAL_MAP_ROOT}: {physical_base}")
    if not path_is_within(manifest_path, formal_root):
        raise LedgerError(f"manifest is not under {FORMAL_MAP_ROOT}: {manifest_path}")
    if physical_base.name != "physical-base.png":
        raise LedgerError("formal input must be named physical-base.png")
    if manifest_path.parent != physical_base.parent:
        raise LedgerError("manifest and physical-base.png must be sibling formal assets")
    if not physical_base.is_file() or not manifest_path.is_file():
        raise LedgerError("formal physical base or manifest is missing")

    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        canvas = manifest["canvas"]
        content_rect = canvas["content_rect"]
        declared_hash = manifest["asset_sha256"]["physical-base.png"]
    except (json.JSONDecodeError, KeyError, TypeError) as exc:
        raise LedgerError(f"invalid formal map manifest contract: {exc}") from exc
    if file_sha256(physical_base) != declared_hash:
        raise LedgerError("formal physical-base.png SHA-256 does not match its manifest")
    if canvas.get("width") != 2400 or canvas.get("height") != 1600:
        raise LedgerError("map derive currently requires the formal 2400x1600 canvas")
    if not isinstance(content_rect, list) or len(content_rect) != 4:
        raise LedgerError("manifest canvas.content_rect must be [x, y, width, height]")
    try:
        x, y, width, height = (float(value) for value in content_rect)
    except (TypeError, ValueError) as exc:
        raise LedgerError("manifest content_rect values must be numeric") from exc
    left = max(0, math.floor(x))
    top = max(0, math.floor(y))
    right = min(2400, math.ceil(x + width))
    bottom = min(1600, math.ceil(y + height))
    if left >= right or top >= bottom:
        raise LedgerError("manifest content_rect is empty or outside the canvas")

    with Image.open(physical_base) as source:
        if source.size != (2400, 1600):
            raise LedgerError(
                f"formal physical-base dimensions are {source.size}, expected (2400, 1600)"
            )
        image = source.convert("RGB").copy()
    metadata = {
        "source_sha256": declared_hash,
        "manifest": manifest_path.relative_to(REPO_ROOT).as_posix(),
        "content_rect_float": content_rect,
        "content_rect_pixel_bounds": [left, top, right, bottom],
        "style": "parchment-muted-v1",
        "coordinate_contract": "2400x1600 canvas and map coordinates are unchanged",
    }
    return image, (left, top, right, bottom), metadata


def render_ui_map_art(
    source: Image.Image, content_bounds: tuple[int, int, int, int]
) -> Image.Image:
    """Apply a fixed paper wash while preserving formal canvas coordinates."""

    paper = Image.new("RGB", source.size, (236, 219, 176))
    content_art = Image.blend(source, paper, 0.14)
    margin_art = Image.blend(source, paper, 0.42)
    mask = Image.new("L", source.size, 0)
    mask.paste(255, box=content_bounds)
    result = Image.composite(content_art, margin_art, mask)
    paper.close()
    content_art.close()
    margin_art.close()
    mask.close()
    return result


def encode_deterministic_png(image: Image.Image) -> bytes:
    output = io.BytesIO()
    image.save(output, format="PNG", optimize=False, compress_level=9)
    return output.getvalue()


def derive_map_art(
    physical_base: Path,
    manifest_path: Path,
    output: Path | None,
    dry_run: bool,
) -> tuple[str, dict[str, Any]]:
    source, content_bounds, metadata = load_formal_map_inputs(
        physical_base, manifest_path
    )
    try:
        derived = render_ui_map_art(source, content_bounds)
    finally:
        source.close()
    try:
        encoded = encode_deterministic_png(derived)
    finally:
        derived.close()
    output_hash = hashlib.sha256(encoded).hexdigest()
    metadata["output_sha256"] = output_hash
    metadata["output_dimensions"] = [2400, 1600]

    if dry_run:
        if output is not None:
            raise LedgerError("--dry-run does not accept --output")
        return output_hash, metadata
    if output is None:
        raise LedgerError("derive-map requires --output unless --dry-run is used")
    destination = output.expanduser().resolve()
    if destination.suffix.lower() != ".png":
        raise LedgerError("derive-map output must have a .png extension")
    if path_is_within(destination, FORMAL_MAP_ROOT.resolve()):
        raise LedgerError("derive-map refuses every path under assets/maps/generated")
    if destination.exists():
        raise LedgerError(f"derive-map refuses to overwrite existing output: {destination}")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("xb") as stream:
        stream.write(encoded)
    metadata["output"] = str(destination)
    return output_hash, metadata


def verify(payload: dict[str, Any]) -> tuple[int, int]:
    entries = entry_map(payload)
    disk_paths = {path.relative_to(ASSET_ROOT).as_posix() for path in delivery_png_paths()}
    ledger_paths = set(entries)
    if disk_paths != ledger_paths:
        missing = sorted(disk_paths - ledger_paths)
        stale = sorted(ledger_paths - disk_paths)
        raise LedgerError(f"inventory mismatch; unlisted={missing}, missing_on_disk={stale}")

    exact_count = 0
    for rel, entry in sorted(entries.items()):
        path = ASSET_ROOT / Path(rel)
        if file_sha256(path) != entry["sha256"]:
            raise LedgerError(f"{rel}: SHA-256 mismatch")
        if path.stat().st_size != entry["bytes"]:
            raise LedgerError(f"{rel}: byte-size mismatch")
        with Image.open(path) as image:
            if [image.width, image.height, image.mode] != [
                entry["width"],
                entry["height"],
                entry["mode"],
            ]:
                raise LedgerError(f"{rel}: image metadata mismatch")
            if image_pixel_sha256(image) != entry["pixel_sha256"]:
                raise LedgerError(f"{rel}: decoded pixel hash mismatch")

        if entry["operation"] in {"exact_crop", "exact_crop_alpha_cleanup"}:
            exact_count += 1
            rebuilt = exact_crop(entry)
            try:
                if entry["operation"] == "exact_crop_alpha_cleanup":
                    cleaned = green_fringe_alpha_cleanup(rebuilt)
                    rebuilt.close()
                    rebuilt = cleaned
                if image_pixel_sha256(rebuilt) != entry["pixel_sha256"]:
                    raise LedgerError(f"{rel}: deterministic source crop/alpha cleanup mismatch")
            finally:
                rebuilt.close()
        elif entry["operation"] == "procedural_source":
            rebuilt = render_procedural_source(rel)
            try:
                if image_pixel_sha256(rebuilt) != entry["pixel_sha256"]:
                    raise LedgerError(f"{rel}: procedural source pixel mismatch")
            finally:
                rebuilt.close()
        elif entry["operation"] == "procedural_final":
            rebuilt = render_procedural_final(rel)
            try:
                if image_pixel_sha256(rebuilt) != entry["pixel_sha256"]:
                    raise LedgerError(f"{rel}: procedural final pixel mismatch")
            finally:
                rebuilt.close()
        elif entry["operation"] == "deterministic_map_derive":
            expected = DERIVED_MAPS.get(rel)
            if expected is None:
                raise LedgerError(f"{rel}: unknown deterministic map derivative")
            output_hash, _metadata = derive_map_art(
                REPO_ROOT / expected[0], REPO_ROOT / expected[1], None, True
            )
            if output_hash != entry["sha256"]:
                raise LedgerError(f"{rel}: deterministic map derive hash mismatch")

    declared_counts = payload.get("role_counts")
    actual_counts = dict(sorted(Counter(e["role"] for e in entries.values()).items()))
    if declared_counts != actual_counts:
        raise LedgerError(
            f"role_counts mismatch; declared={declared_counts}, actual={actual_counts}"
        )
    return len(entries), exact_count


def build(
    payload: dict[str, Any], output_root: Path, clean_green_fringe: bool = False
) -> dict[str, str]:
    output_root.mkdir(parents=True, exist_ok=True)
    hashes: dict[str, str] = {}
    entries = entry_map(payload)

    # Phase 1 publishes source identities and source sheets first.  Later crops
    # therefore consume the just-rebuilt source tree, never the checkout copy.
    for rel, entry in sorted(entries.items()):
        operation = entry["operation"]
        destination = output_root / Path(rel)
        if operation == "precise_object_edit":
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes((ASSET_ROOT / rel).read_bytes())
        elif operation == "procedural_source":
            image = render_procedural_source(rel)
            try:
                save_deterministic_png(image, destination)
            finally:
                image.close()
        else:
            continue
        hashes[rel] = file_sha256(destination)

    # Phase 2 creates project-original standalone finals.
    for rel, entry in sorted(entries.items()):
        if entry["operation"] != "procedural_final":
            continue
        destination = output_root / Path(rel)
        image = render_procedural_final(rel)
        try:
            save_deterministic_png(image, destination)
        finally:
            image.close()
        hashes[rel] = file_sha256(destination)

    # Phase 3 derives all source-backed controls from the rebuilt source tree.
    for rel, entry in sorted(entries.items()):
        operation = entry["operation"]
        if operation in {"exact_crop", "exact_crop_alpha_cleanup"}:
            image = exact_crop(entry, output_root)
            if operation == "exact_crop_alpha_cleanup":
                cleaned = green_fringe_alpha_cleanup(image)
                image.close()
                image = cleaned
        else:
            continue
        try:
            if clean_green_fringe and operation == "exact_crop":
                cleaned = green_fringe_alpha_cleanup(image)
                image.close()
                image = cleaned
            destination = output_root / Path(rel)
            save_deterministic_png(image, destination)
            hashes[rel] = file_sha256(destination)
        finally:
            image.close()

    # Phase 4 rebuilds coordinate-preserving Natural Earth presentation maps.
    for rel, entry in sorted(entries.items()):
        if entry["operation"] != "deterministic_map_derive":
            continue
        source_rel, manifest_rel = DERIVED_MAPS[rel]
        source, bounds, _metadata = load_formal_map_inputs(
            REPO_ROOT / source_rel, REPO_ROOT / manifest_rel
        )
        try:
            image = render_ui_map_art(source, bounds)
        finally:
            source.close()
        try:
            destination = output_root / Path(rel)
            save_deterministic_png(image, destination)
            hashes[rel] = file_sha256(destination)
        finally:
            image.close()

    if set(hashes) != set(entries):
        missing = sorted(set(entries) - set(hashes))
        raise LedgerError(f"full rebuild omitted delivered assets: {missing}")
    if not clean_green_fringe:
        mismatched = sorted(rel for rel, digest in hashes.items() if digest != entries[rel]["sha256"])
        if mismatched:
            raise LedgerError(f"full rebuild does not match canonical ledger bytes: {mismatched}")
    return hashes


def repeatability(payload: dict[str, Any], clean_green_fringe: bool = False) -> int:
    # 本环境（Windows 文件沙箱）下 tempfile.mkdtemp 创建的目录会被置为只读：
    # 它先以 O_CREAT 探测文件名再删除后 mkdir，沙箱把该路径仍当作受保护文件，
    # 导致目录内无法再创建子项。改用 pathlib 显式建目录即可正常读写与清理。
    root = Path(tempfile.gettempdir()) / "mingsim-ui-repeatability"
    root.mkdir(parents=True, exist_ok=True)
    first_dir = root / "a"
    second_dir = root / "b"
    for directory in (first_dir, second_dir):
        if directory.exists():
            shutil.rmtree(directory)
        directory.mkdir(parents=True)
    try:
        first = build(payload, first_dir, clean_green_fringe)
        second = build(payload, second_dir, clean_green_fringe)
    finally:
        for directory in (first_dir, second_dir):
            shutil.rmtree(directory, ignore_errors=True)
        try:
            root.rmdir()
        except OSError:
            pass
    if first != second:
        differing = sorted(key for key in set(first) | set(second) if first.get(key) != second.get(key))
        raise LedgerError(f"repeatability mismatch: {differing}")
    return len(first)


def regenerate_component_bundle(output_root: Path) -> int:
    """Create the 52 provenance-replacement assets outside the repository."""

    output_root.mkdir(parents=True, exist_ok=True)
    generated: set[str] = set()

    for rel in sorted(PROCEDURAL_SOURCE_SIZES):
        image = render_procedural_source(rel)
        try:
            save_deterministic_png(image, output_root / rel)
        finally:
            image.close()
        generated.add(rel)

    for rel, (source_rel, x, y, width, height) in sorted(PROVEN_EXACT_CROPS.items()):
        entry = {
            "path": rel,
            "source_path": source_rel,
            "slice": {"x": x, "y": y, "width": width, "height": height, "method": "pixel_exact_crop"},
        }
        image = exact_crop(entry, output_root)
        cleaned = green_fringe_alpha_cleanup(image)
        image.close()
        try:
            save_deterministic_png(cleaned, output_root / rel)
        finally:
            cleaned.close()
        generated.add(rel)

    for rel in sorted(PROCEDURAL_FINALS):
        image = render_procedural_final(rel)
        try:
            save_deterministic_png(image, output_root / rel)
        finally:
            image.close()
        generated.add(rel)

    if len(generated) != 52:
        raise LedgerError(f"component bundle expected 52 assets, created {len(generated)}")
    return len(generated)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--ledger", type=Path, default=DEFAULT_LEDGER, help="asset ledger JSON"
    )
    subparsers = parser.add_subparsers(dest="command", required=True)
    subparsers.add_parser(
        "snapshot-ledger",
        help="mechanically refresh the canonical ledger from current PNG bytes",
    )
    subparsers.add_parser("verify", help="verify inventory, hashes, metadata and exact crops")

    build_parser = subparsers.add_parser(
        "build", help="rebuild proven slices outside the repository"
    )
    build_parser.add_argument("--output", required=True)
    build_parser.add_argument(
        "--clean-green-fringe",
        action="store_true",
        help="experimental output-only alpha cleanup; does not match ledger pixels",
    )

    repeat_parser = subparsers.add_parser(
        "repeatability", help="build twice in temp directories and compare file hashes"
    )
    repeat_parser.add_argument("--clean-green-fringe", action="store_true")

    component_parser = subparsers.add_parser(
        "regenerate-components",
        help="create the 52 closed-provenance replacement assets outside the repository",
    )
    component_parser.add_argument("--output", required=True)

    map_parser = subparsers.add_parser(
        "derive-map",
        help="derive coordinate-preserving UI-only art from a formal 2400x1600 map",
    )
    map_parser.add_argument("--physical-base", required=True, type=Path)
    map_parser.add_argument("--manifest", required=True, type=Path)
    map_parser.add_argument("--output", type=Path)
    map_parser.add_argument(
        "--dry-run",
        action="store_true",
        help="validate and hash the in-memory result without creating a PNG",
    )
    return parser


def main(argv: Iterable[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        if args.command == "snapshot-ledger":
            count = write_ledger_snapshot(args.ledger.resolve())
            print(f"OK: refreshed canonical ledger for {count} current PNGs")
        elif args.command == "derive-map":
            output_hash, metadata = derive_map_art(
                args.physical_base,
                args.manifest,
                args.output,
                args.dry_run,
            )
            print(
                f"OK: derived map art SHA-256 {output_hash}; "
                f"metadata={json.dumps(metadata, ensure_ascii=False, sort_keys=True)}"
            )
        elif args.command == "verify":
            payload = load_ledger(args.ledger.resolve())
            total, exact = verify(payload)
            counts = payload["role_counts"]
            print(
                f"OK: {total} PNGs verified; {exact} deterministic crop-derived outputs; "
                f"roles={json.dumps(counts, ensure_ascii=False, sort_keys=True)}"
            )
        elif args.command == "build":
            payload = load_ledger(args.ledger.resolve())
            output = checked_output_root(args.output)
            hashes = build(payload, output, args.clean_green_fringe)
            print(f"OK: rebuilt {len(hashes)} proven slices at {output}")
        elif args.command == "repeatability":
            payload = load_ledger(args.ledger.resolve())
            count = repeatability(payload, args.clean_green_fringe)
            suffix = " with experimental green cleanup" if args.clean_green_fringe else ""
            print(f"OK: {count} outputs are byte-repeatable across two temp builds{suffix}")
        elif args.command == "regenerate-components":
            output = checked_output_root(args.output)
            count = regenerate_component_bundle(output)
            print(f"OK: generated {count} closed-provenance component assets at {output}")
        return 0
    except LedgerError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
