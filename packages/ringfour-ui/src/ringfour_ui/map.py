"""Render a metric map as an image: grid, robots, path, measurements.

This is the one piece of mission UI Gradio cannot give you. It has an
Image, a Dataframe, a LinePlot and a Chatbot; it does not have "an
occupancy grid with robot poses and a field of measured values drawn over
it in world coordinates", and it never will, because that is a robotics
thing rather than a UI thing.

Everything else a mission UI needs is plain Gradio. This module returns a
numpy array, so it drops straight into ``gr.Image`` and the browser gives
you pan and zoom for free.

    view = MapView()
    frame = view.render(grid=robot.map, poses=robot.pose, points=samples)

THE MEASUREMENT OVERLAY is the reason this exists in the shape it does.
``points`` takes plain ``Point``s, which render neutral, and
``ScalarPoint``s, which render from a colormap — a field of pH, moisture,
signal strength or clearance drawn where a costmap would be. Mixing both
in one call is normal: waypoints under a measured field.

Two anti-flicker rules are why ``MapView`` is a class rather than four
loose functions, and both were learned the hard way:

* the colormap domain only moves when a value falls outside it, so the
  legend does not rescale on every frame and a colour keeps meaning the
  same thing from one second to the next;
* with no grid, the fitted view only refits when something leaves it,
  because a view that rescales every frame makes a moving robot look
  stationary while the world slides around it.
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Any, Iterable, Sequence

import numpy as np

from ringfour.models.spatial import Point, Pose

# ---- the measurement overlay -------------------------------------------------


@dataclass(frozen=True)
class ScalarPoint:
    """A point carrying a measured value.

    ``label`` names the FIELD, not the sample: every point in one overlay
    should carry the same label, because it becomes the legend's title.
    """

    point: Point
    value: float
    label: str = ""


# ---- colours -----------------------------------------------------------------

# Viridis at ten stops. Perceptually uniform, and legible to the ~8% of
# men with red-green colour blindness — which a red-to-green ramp, and a
# costmap, are not.
_VIRIDIS = [
    (0x44, 0x01, 0x54), (0x48, 0x28, 0x78), (0x3E, 0x4A, 0x89),
    (0x31, 0x68, 0x8E), (0x26, 0x83, 0x8E), (0x1F, 0x9E, 0x89),
    (0x35, 0xB7, 0x79), (0x6E, 0xCE, 0x58), (0xB5, 0xDE, 0x2B),
    (0xFD, 0xE7, 0x25),
]

# The grid, rendered for a dark surface: walls brightest, then free, then
# unknown. The ROS convention (free WHITE) was written for a light page;
# a white free-space rectangle here is a lightbox that costs every
# overlay on top of it its contrast.
UNKNOWN = (0x12, 0x12, 0x16)
FREE = (0x26, 0x26, 0x2C)
OCCUPIED = (0x74, 0x74, 0x80)
BACKDROP = (0x0A, 0x0A, 0x0C)

ACCENT = (0x1A, 0x9E, 0x8E)
# Fixed per-namespace colours. Fixed, because an operator learns "the
# picker is the green one" in about a minute, and relearning it on every
# reconnect is worse than any palette choice.
NS_PALETTE = [(0x34, 0xD3, 0x99), (0x5A, 0xA9, 0xE6), (0xF4, 0xA3, 0x40),
              (0xE8, 0x6A, 0x92), (0xC2, 0xA5, 0xF2), (0xF2, 0xE1, 0x5A)]

FREE_MAX = 25        # ROS occupancy: <= this counts as free
OCCUPIED_MIN = 50    # >= this counts as a wall


def viridis(t: float) -> tuple[int, int, int]:
    """A colour for t in [0, 1]. Clamped, never wrapped."""
    t = 0.0 if t != t else min(1.0, max(0.0, t))     # t != t catches NaN
    position = t * (len(_VIRIDIS) - 1)
    low = int(position)
    high = min(low + 1, len(_VIRIDIS) - 1)
    frac = position - low
    return tuple(  # type: ignore[return-value]
        round(_VIRIDIS[low][i] + (_VIRIDIS[high][i] - _VIRIDIS[low][i]) * frac)
        for i in range(3))


@dataclass
class Domain:
    """The value range the colour ramp currently spans."""

    low: float = 0.0
    high: float = 1.0
    initialised: bool = False

    OUTSIDE = 0.05      # how far past the domain a value must fall to rescale
    EXPAND = 0.10       # how much slack to leave when it does

    def normalise(self, value: float) -> float:
        span = self.high - self.low
        return 0.5 if span <= 0 else (value - self.low) / span

    def observe(self, values: Sequence[float]) -> bool:
        """Fold in a new point set. True when the domain actually moved."""
        values = [v for v in values if v == v]       # drop NaN
        if not values:
            return False
        low, high = min(values), max(values)
        if not self.initialised:
            self._set(low, high)
            self.initialised = True
            return True
        span = self.high - self.low
        margin = abs(span) * self.OUTSIDE if span else abs(self.high) * self.OUTSIDE
        if low >= self.low - margin and high <= self.high + margin:
            return False
        self._set(min(low, self.low), max(high, self.high))
        return True

    def _set(self, low: float, high: float) -> None:
        if high <= low:
            # One distinct value is a legitimate state — a single sample,
            # or a field that has not varied yet. Give it a span rather
            # than dividing by zero forever after.
            pad = abs(low) * self.EXPAND or 0.5
            self.low, self.high = low - pad, low + pad
            return
        pad = (high - low) * self.EXPAND
        self.low, self.high = low - pad, high + pad


# ---- geometry ----------------------------------------------------------------


@dataclass
class _Geometry:
    """Where the world is, in cells."""

    origin_x: float
    origin_y: float
    resolution: float
    width: int          # cells
    height: int         # cells


def _to_px(x: float, y: float, geo: _Geometry, scale: int) -> tuple[float, float]:
    """World metres to image pixels.

    The one piece of arithmetic every overlay depends on, which is why it
    is a named function and not four inlined copies that drift. The y
    flip is because ROS puts cell (0, 0) at the bottom-left and images
    start top-left; getting it wrong yields a map that looks plausible
    and is mirrored, which is the worst kind of wrong.
    """
    u = (x - geo.origin_x) / geo.resolution * scale
    v = (geo.height - (y - geo.origin_y) / geo.resolution) * scale
    return u, v


def _poses_of(poses: Any) -> dict[str, Pose]:
    if poses is None:
        return {}
    if isinstance(poses, Pose):
        return {"robot": poses}
    return dict(poses)


def _xy(item: Any) -> tuple[float, float, float | None, str]:
    """(x, y, value, label) for anything ``points`` accepts."""
    if isinstance(item, ScalarPoint):
        return item.point.x, item.point.y, float(item.value), item.label
    if isinstance(item, dict):
        point = item["point"]
        value = item.get("value")
        return (point.x, point.y,
                None if value is None else float(value),
                str(item.get("label", "")))
    return item.x, item.y, None, ""


class MapView:
    """A map renderer that remembers its scaling between frames.

    Hold one per map panel. Two panels showing different fields want
    different colour domains, and sharing one would make both illegible.
    """

    FIT_PX = 900
    FIT_MARGIN = 0.10

    def __init__(self) -> None:
        self.domain = Domain()
        self._fit: _Geometry | None = None

    # -- public ---------------------------------------------------------------

    def render(self, grid: Any = None, *,
               poses: "Pose | dict[str, Pose] | None" = None,
               points: Iterable | None = None,
               path: Sequence | None = None,
               size: int = 900,
               labels: bool = True) -> np.ndarray:
        """One RGB frame, ready for ``gr.Image``.

        Every layer is optional. With no grid the view is fitted to the
        data; with no data at all you get an empty backdrop rather than
        an exception, because a panel that has not been updated yet is a
        normal state and should look like one.
        """
        from PIL import Image, ImageDraw

        points = list(points or ())
        path = list(path or ())
        pose_map = _poses_of(poses)

        geo = self._geometry(grid, pose_map, points, path)
        scale = max(1, round(size / max(1, geo.width)))
        picture = self._base(grid, geo, scale)
        canvas = ImageDraw.Draw(picture, "RGBA")

        self._draw_path(canvas, path, geo, scale)
        self._draw_points(canvas, points, geo, scale)
        self._draw_poses(canvas, pose_map, geo, scale, labels)
        if labels:
            self._draw_legend(canvas, points, picture.width, picture.height)
        return np.asarray(picture)

    # -- geometry -------------------------------------------------------------

    def _geometry(self, grid, poses, points, path) -> _Geometry:
        if grid is not None:
            return _Geometry(grid.origin.x, grid.origin.y, grid.resolution,
                             int(grid.width), int(grid.height))
        return self._fitted(poses, points, path)

    def _fitted(self, poses, points, path) -> _Geometry:
        """A synthetic geometry covering the data, plus 10%.

        Refit only when something falls outside the current view — same
        anti-flicker reason as the colour domain.
        """
        xs: list[float] = []
        ys: list[float] = []
        for pose in poses.values():
            xs.append(pose.x)
            ys.append(pose.y)
        for item in points:
            x, y, _v, _l = _xy(item)
            xs.append(x)
            ys.append(y)
        for point in path:
            xs.append(point.x)
            ys.append(point.y)

        if not xs:
            return self._fit or self._view(-2.0, -2.0, 2.0, 2.0)
        low_x, high_x, low_y, high_y = min(xs), max(xs), min(ys), max(ys)
        fit = self._fit
        if fit is not None:
            inside = (low_x >= fit.origin_x and low_y >= fit.origin_y
                      and high_x <= fit.origin_x + fit.width * fit.resolution
                      and high_y <= fit.origin_y + fit.height * fit.resolution)
            if inside:
                return fit
        span_x = max(high_x - low_x, 0.5)
        span_y = max(high_y - low_y, 0.5)
        pad_x, pad_y = span_x * self.FIT_MARGIN, span_y * self.FIT_MARGIN
        self._fit = self._view(low_x - pad_x, low_y - pad_y,
                               high_x + pad_x, high_y + pad_y)
        return self._fit

    def _view(self, low_x, low_y, high_x, high_y) -> _Geometry:
        span_x = max(high_x - low_x, 1e-3)
        span_y = max(high_y - low_y, 1e-3)
        resolution = span_x / self.FIT_PX
        return _Geometry(low_x, low_y, resolution, self.FIT_PX,
                         max(1, int(round(span_y / resolution))))

    # -- layers ---------------------------------------------------------------

    def _base(self, grid, geo: _Geometry, scale: int):
        from PIL import Image

        if grid is None:
            return Image.new("RGB", (geo.width * scale, geo.height * scale),
                             BACKDROP)
        cells = np.asarray(grid.data)
        picture = np.zeros((geo.height, geo.width, 3), np.uint8)
        picture[:] = UNKNOWN
        picture[(cells >= 0) & (cells <= FREE_MAX)] = FREE
        picture[cells >= OCCUPIED_MIN] = OCCUPIED
        # See _to_px: ROS origin is bottom-left, images are top-left.
        picture = np.flipud(picture)
        # NEAREST, not bilinear: at 5 cm a cell is a fact, and smoothing
        # it invents free space at the edge of every wall.
        return Image.fromarray(picture).resize(
            (geo.width * scale, geo.height * scale), Image.NEAREST)

    def _draw_path(self, canvas, path, geo, scale) -> None:
        if len(path) < 2:
            return
        canvas.line([_to_px(p.x, p.y, geo, scale) for p in path],
                    fill=ACCENT + (230,), width=max(2, scale // 2),
                    joint="curve")

    def _draw_points(self, canvas, points, geo, scale) -> None:
        if not points:
            return
        scalars = [v for _x, _y, v, _l in map(_xy, points) if v is not None]
        if scalars:
            self.domain.observe(scalars)
        radius = max(3.0, 3.0 * scale)
        for item in points:
            x, y, value, _label = _xy(item)
            u, v = _to_px(x, y, geo, scale)
            box = (u - radius, v - radius, u + radius, v + radius)
            if value is None:
                canvas.ellipse(box, fill=(255, 255, 255, 80),
                               outline=(255, 255, 255, 110))
            else:
                canvas.ellipse(box,
                               fill=viridis(self.domain.normalise(value)) + (255,),
                               outline=(0, 0, 0, 140))

    def _draw_poses(self, canvas, poses, geo, scale, labels) -> None:
        """A triangle per namespace, labelled.

        The label is what makes a multi-robot map readable; without it
        two triangles are a puzzle.
        """
        size = max(6.0, 5.5 * scale)
        for index, (ns, pose) in enumerate(sorted(poses.items())):
            u, v = _to_px(pose.x, pose.y, geo, scale)
            colour = NS_PALETTE[index % len(NS_PALETTE)]
            # World yaw is counter-clockwise; image y grows downward, so
            # the same rotation on screen is clockwise. Hence -yaw.
            angle = -pose.yaw
            nose, back, side = size, -size * 0.6, size * 0.62
            corners = [(nose, 0.0), (back, -side), (back, side)]
            rotated = [
                (u + cx * math.cos(angle) - cy * math.sin(angle),
                 v + cx * math.sin(angle) + cy * math.cos(angle))
                for cx, cy in corners
            ]
            canvas.polygon(rotated, fill=colour + (255,),
                           outline=(0x1B, 0x1B, 0x1B, 255))
            if labels and (len(poses) > 1 or ns != "robot"):
                canvas.text((u + size + 4, v - size),
                            str(ns), fill=colour + (255,),
                            font=_font(max(11, int(4 * scale))),
                            stroke_width=2, stroke_fill=(0, 0, 0, 180))

    def _draw_legend(self, canvas, points, width, height) -> None:
        """Ramp, min, max, and the field name."""
        labelled = [(v, l) for _x, _y, v, l in map(_xy, points) if v is not None]
        if not labelled:
            return
        field = next((l for _v, l in labelled if l), "")

        bar_w = max(120, width // 7)
        bar_h = max(10, height // 55)
        font = _font(max(11, height // 55))
        x0 = width - bar_w - 18
        y0 = height - bar_h - 40

        canvas.rounded_rectangle(
            (x0 - 12, y0 - 26, x0 + bar_w + 12, y0 + bar_h + 24), radius=6,
            fill=(0x0A, 0x0A, 0x0C, 215), outline=(255, 255, 255, 30))
        steps = 48
        for i in range(steps):
            left = x0 + bar_w * i / steps
            canvas.rectangle((left, y0, left + bar_w / steps + 1, y0 + bar_h),
                             fill=viridis(i / (steps - 1)) + (255,))
        white = (255, 255, 255, 235)
        if field:
            canvas.text((x0, y0 - 22), field, fill=white, font=font)
        canvas.text((x0, y0 + bar_h + 4), f"{self.domain.low:.3g}",
                    fill=white, font=font)
        canvas.text((x0 + bar_w, y0 + bar_h + 4), f"{self.domain.high:.3g}",
                    fill=white, font=font, anchor="ra")


_FONTS: dict[int, Any] = {}


def _font(size: int):
    """PIL's default face at a usable size, cached.

    No bundled font file: shipping one means a licence to track, and the
    default is legible enough for four numbers and a namespace.
    """
    if size not in _FONTS:
        from PIL import ImageFont
        try:
            _FONTS[size] = ImageFont.load_default(size=size)
        except TypeError:      # Pillow < 10.1 has no size argument
            _FONTS[size] = ImageFont.load_default()
    return _FONTS[size]


_DEFAULT = MapView()


def map_image(grid: Any = None, *,
              poses: "Pose | dict[str, Pose] | None" = None,
              points: Iterable | None = None,
              path: Sequence | None = None,
              size: int = 900,
              labels: bool = True) -> np.ndarray:
    """One-shot render against a shared default view.

    Convenient for a single-map app. Hold your own ``MapView`` when you
    have two maps, or the second one will fight the first for the colour
    domain.
    """
    return _DEFAULT.render(grid, poses=poses, points=points, path=path,
                           size=size, labels=labels)
