"""Optional UI helpers for mission-authored Gradio applications.

The package adds Ringfour's visual theme and a renderer for occupancy maps.
Mission authors build the rest of their interface with ordinary Gradio.
"""

from .map import (
    ACCENT,
    NS_PALETTE,
    Domain,
    MapView,
    ScalarPoint,
    map_image,
    viridis,
)
from .theme import CSS, launch, theme

__all__ = [
    "map_image",
    "MapView",
    "ScalarPoint",
    "Domain",
    "viridis",
    "theme",
    "launch",
    "CSS",
    "ACCENT",
    "NS_PALETTE",
]
