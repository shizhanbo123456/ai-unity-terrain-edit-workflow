"""Validate and visualize RoadGenerationTest MapData without Unity dependencies."""

from __future__ import annotations

import argparse
from collections import deque
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw


def read_map(path: Path) -> np.ndarray:
    with path.open("r", encoding="utf-8-sig") as stream:
        header = stream.readline().strip()
        if not header.startswith("#key="):
            raise ValueError(f"Invalid MapData header: {path}")
        rows = [[float(value) for value in line.strip().split(",")] for line in stream if line.strip()]
    data = np.asarray(rows, dtype=np.float32)
    if data.ndim != 2 or not data.size:
        raise ValueError(f"MapData is empty or non-rectangular: {path}")
    return data


def component_count(mask: np.ndarray) -> int:
    visited = np.zeros(mask.shape, dtype=bool)
    count = 0
    height, width = mask.shape
    for y, x in zip(*np.nonzero(mask)):
        if visited[y, x]:
            continue
        count += 1
        visited[y, x] = True
        queue = deque([(y, x)])
        while queue:
            cy, cx = queue.popleft()
            for ny, nx in ((cy - 1, cx), (cy + 1, cx), (cy, cx - 1), (cy, cx + 1)):
                if 0 <= ny < height and 0 <= nx < width and mask[ny, nx] and not visited[ny, nx]:
                    visited[ny, nx] = True
                    queue.append((ny, nx))
    return count


def curved_component_count(mask: np.ndarray, minimum_points: int = 80, minimum_ratio: float = 0.004) -> int:
    """Count long 8-connected centerlines whose minor/major PCA variance proves visible curvature."""
    visited = np.zeros(mask.shape, dtype=bool)
    height, width = mask.shape
    curved = 0
    for y, x in zip(*np.nonzero(mask)):
        if visited[y, x]:
            continue
        points = []
        visited[y, x] = True
        queue = deque([(y, x)])
        while queue:
            cy, cx = queue.popleft()
            points.append((cx, cy))
            for ny in range(cy - 1, cy + 2):
                for nx in range(cx - 1, cx + 2):
                    if (0 <= ny < height and 0 <= nx < width and mask[ny, nx]
                            and not visited[ny, nx]):
                        visited[ny, nx] = True
                        queue.append((ny, nx))
        if len(points) < minimum_points:
            continue
        covariance = np.cov(np.asarray(points, dtype=np.float32).T)
        eigenvalues = np.linalg.eigvalsh(covariance)
        if eigenvalues[-1] > 0 and eigenvalues[0] / eigenvalues[-1] >= minimum_ratio:
            curved += 1
    return curved


def colorize(values: np.ndarray, color: tuple[int, int, int]) -> np.ndarray:
    maximum = float(values.max())
    normalized = values / maximum if maximum > 0 else values
    rgb = np.zeros((*values.shape, 3), dtype=np.uint8)
    for channel, component in enumerate(color):
        rgb[..., channel] = np.clip(normalized * component, 0, 255).astype(np.uint8)
    return rgb


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("map_data", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()

    layer = np.rint(read_map(args.map_data / "layerMap.txt")).astype(np.int32)
    road = read_map(args.map_data / "road.txt")
    occupancy = read_map(args.map_data / "occupancy.txt")
    distance = read_map(args.map_data / "distance.txt")
    off_road = read_map(args.map_data / "offRoad.txt")
    if len({item.shape for item in (layer, road, occupancy, distance, off_road)}) != 1:
        raise AssertionError("MapData dimensions differ")

    road_mask = road > 0.5
    semantic = layer > 0
    transparent = ~semantic
    total_road = int(road_mask.sum())
    assertions = {
        "road_is_non_empty": total_road > 0,
        "road_stays_in_legal_layers": not np.any(road_mask & transparent),
        "wide_layer_has_road": bool(np.any(road_mask & (layer == 1))),
        "narrow_layer_has_road": bool(np.any(road_mask & (layer == 2))),
        "occupancy_is_non_empty": bool(np.any(occupancy > 0.5)),
        "multiple_curved_anchor_paths_exist": curved_component_count(occupancy > 0.5) >= 2,
        "distance_is_non_empty": float(distance.max()) > 0,
        "off_road_is_non_empty": float(off_road.max()) > 0,
    }
    for name, passed in assertions.items():
        print(f"{'PASS' if passed else 'FAIL'} {name}")

    print(f"resolution={road.shape[1]}x{road.shape[0]}")
    print(f"road_pixels={total_road} ({total_road / road.size:.2%})")
    print(f"road_components_4_connected={component_count(road_mask)}")
    print(f"distance_max={float(distance.max()):.3f}")
    print(f"off_road_max={float(off_road.max()):.3f}")
    print(f"boundary_spill_pixels={int(np.count_nonzero(road_mask & transparent))}")
    for layer_index in range(1, 3):
        area = layer == layer_index
        pixels = int(np.count_nonzero(road_mask & area))
        coverage = pixels / max(1, int(area.sum()))
        print(f"layer_{layer_index}_road_pixels={pixels} coverage={coverage:.2%}")

    if args.output:
        palette = np.asarray([
            [18, 18, 22], [60, 145, 62], [211, 137, 40], [55, 111, 206], [169, 70, 190]
        ], dtype=np.uint8)
        layer_rgb = palette[np.clip(layer, 0, len(palette) - 1)]
        overlay = layer_rgb.copy()
        overlay[road_mask] = [255, 245, 90]
        panels = [
            (overlay, "LayerMap + Road"),
            (colorize(road, (255, 245, 90)), "Road Mask"),
            (colorize(occupancy, (255, 90, 70)), "Generated Anchor Centerlines"),
            (colorize(off_road, (70, 190, 255)), "Off-road Distance"),
        ]
        scale = 2
        margin = 28
        panel_w = road.shape[1] * scale
        panel_h = road.shape[0] * scale
        canvas = Image.new("RGB", (panel_w * 2, (panel_h + margin) * 2), (24, 24, 28))
        draw = ImageDraw.Draw(canvas)
        for index, (pixels, title) in enumerate(panels):
            x = (index % 2) * panel_w
            y = (index // 2) * (panel_h + margin)
            image = Image.fromarray(pixels).resize((panel_w, panel_h), Image.Resampling.NEAREST)
            canvas.paste(image, (x, y + margin))
            draw.text((x + 8, y + 7), title, fill=(235, 235, 240))
        args.output.parent.mkdir(parents=True, exist_ok=True)
        canvas.save(args.output)
        print(f"preview={args.output}")

    return 0 if all(assertions.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
