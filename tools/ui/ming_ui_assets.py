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
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any, Iterable

try:
    from PIL import Image, ImageFilter
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
    "identity",
    "external",
    "exact_crop",
    "exact_crop_alpha_cleanup",
    "precise_object_edit",
    "deterministic_map_derive",
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
    "memorials/memorial-disabled.png": ("source/memorial-paper-states-source.png", 1060, 406, 646, 258),
    "memorials/memorial-hover.png": ("source/memorial-paper-states-source.png", 706, 90, 646, 254),
    "memorials/memorial-normal.png": ("source/memorial-paper-states-source.png", 28, 90, 646, 254),
    "memorials/memorial-pressed.png": ("source/memorial-paper-states-source.png", 1392, 90, 646, 254),
    "memorials/memorial-selected.png": ("source/memorial-paper-states-source.png", 330, 406, 646, 258),
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
}

IMAGEGEN_DESK_MAP = "backgrounds/ming-imperial-study-desk-map.png"
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
        if rel.startswith("source/"):
            role = "source"
            operation = "identity"
            source_path = None
            slice_metadata = None
            tool = "OPEN"
            date = "OPEN"
            generation = "OPEN"
            license_decision = (
                "OPEN - source provenance and license evidence are not recorded in-repo"
            )
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
            tool = (
                "Pillow exact crop + deterministic one-pixel alpha matte cleanup"
                if operation == "exact_crop_alpha_cleanup"
                else "OPEN (current editor); pixel-identical rebuild available via tools/ui/ming_ui_assets.py using Pillow"
            )
            date = "OPEN"
            generation = "OPEN"
            license_decision = (
                "INHERITS_OPEN_FROM_SOURCE - no independent license decision recorded"
            )
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
            extra = {"source_manifest": source_manifest}
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
                "provenance_note": (
                    "Removed pseudo-glyph details and mechanical/metal props from the "
                    "previous composition; overwritten source version is not retained here."
                ),
            }
        else:
            role = "final"
            operation = "external"
            source_path = None
            slice_metadata = None
            tool = "OPEN"
            date = "OPEN"
            generation = "OPEN"
            license_decision = (
                "OPEN - generation/edit provenance and license decision are not fully "
                "recorded in-repo"
            )

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
        "provenance_policy": (
            "OPEN means evidence is absent; it is not a license grant or proof of origin."
        ),
        "role_counts": counts,
        "coverage": {
            "inventory_png_count": len(assets),
            "pixel_exact_crop_count": sum(
                asset["operation"] == "exact_crop" for asset in assets
            ),
            "alpha_cleanup_crop_count": sum(
                asset["operation"] == "exact_crop_alpha_cleanup" for asset in assets
            ),
            "external_non_rebuildable_final_count": sum(
                asset["operation"] == "external" for asset in assets
            ),
            "precise_object_edit_count": sum(
                asset["operation"] == "precise_object_edit" for asset in assets
            ),
            "source_identity_count": sum(
                asset["operation"] == "identity" for asset in assets
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
    path.write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
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
        result[rel] = entry
    return result


def exact_crop(entry: dict[str, Any]) -> Image.Image:
    source_rel = entry["source_path"]
    rect = entry["slice"]
    if not isinstance(source_rel, str) or not isinstance(rect, dict):
        raise LedgerError(f"{entry['path']}: exact_crop requires source_path and slice")
    expected_keys = {"x", "y", "width", "height", "method"}
    if set(rect) != expected_keys or rect["method"] != "pixel_exact_crop":
        raise LedgerError(f"{entry['path']}: invalid exact_crop slice metadata")
    source = ASSET_ROOT / Path(source_rel)
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
    for rel, entry in sorted(entry_map(payload).items()):
        if entry["operation"] not in {"exact_crop", "exact_crop_alpha_cleanup", "deterministic_map_derive"}:
            continue
        if entry["operation"] in {"exact_crop", "exact_crop_alpha_cleanup"}:
            image = exact_crop(entry)
            if entry["operation"] == "exact_crop_alpha_cleanup":
                cleaned = green_fringe_alpha_cleanup(image)
                image.close()
                image = cleaned
        else:
            source_rel, manifest_rel = DERIVED_MAPS[rel]
            source, bounds, _metadata = load_formal_map_inputs(
                REPO_ROOT / source_rel, REPO_ROOT / manifest_rel
            )
            try:
                image = render_ui_map_art(source, bounds)
            finally:
                source.close()
        try:
            if clean_green_fringe and entry["operation"] == "exact_crop":
                cleaned = green_fringe_alpha_cleanup(image)
                image.close()
                image = cleaned
            destination = output_root / Path(rel)
            save_deterministic_png(image, destination)
            hashes[rel] = file_sha256(destination)
            if not clean_green_fringe and entry["operation"] == "exact_crop":
                with Image.open(destination) as rebuilt:
                    if image_pixel_sha256(rebuilt) != entry["pixel_sha256"]:
                        raise LedgerError(f"{rel}: deterministic output pixel mismatch")
        finally:
            image.close()
    return hashes


def repeatability(payload: dict[str, Any], clean_green_fringe: bool = False) -> int:
    with tempfile.TemporaryDirectory(prefix="mingsim-ui-a-") as first_dir:
        with tempfile.TemporaryDirectory(prefix="mingsim-ui-b-") as second_dir:
            first = build(payload, Path(first_dir), clean_green_fringe)
            second = build(payload, Path(second_dir), clean_green_fringe)
    if first != second:
        differing = sorted(key for key in set(first) | set(second) if first.get(key) != second.get(key))
        raise LedgerError(f"repeatability mismatch: {differing}")
    return len(first)


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
        return 0
    except LedgerError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
