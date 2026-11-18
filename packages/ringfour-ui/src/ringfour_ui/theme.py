"""Ringfour's optional Gradio theme and safe local launch defaults."""

from __future__ import annotations

import base64
import os
import re
from functools import lru_cache
from pathlib import Path
from typing import Any

ACCENT = "#e47a00"
ACCENT_HOVER = "#ff9b2f"
READY = "#36b5a0"
SURFACE = "#111111"
INSET = "#191919"
PAGE = "#080808"

_FONT_DIR = Path(__file__).parent / "assets" / "fonts"
_FONT_FILES = (
    ("SpaceGrotesk-Regular", 400),
    ("SpaceGrotesk-Medium", 500),
    ("SpaceGrotesk-Bold", 700),
)

CSS = """
.gradio-container, body, gradio-app { background: #080808 !important; }
.gradio-container { max-width: 100% !important; }
.gradio-container label > span:first-child,
span[data-testid="block-info"] {
  font-family: "Space Grotesk", "Inter", sans-serif !important;
  font-size: 11px !important;
  font-weight: 700 !important;
  letter-spacing: 1.2px !important;
  text-transform: uppercase !important;
}
.gradio-container table, .gradio-container td, .gradio-container th,
.gradio-container textarea, .gradio-container input[type=number] {
  font-variant-numeric: tabular-nums;
}
.gradio-container button, .gradio-container .gr-button {
  border-radius: 6px !important;
}
.gradio-container input, .gradio-container textarea,
.gradio-container select { border-radius: 4px !important; }
.gradio-container button.primary {
  box-shadow: 0 2px 10px rgba(228,122,0,.18) !important;
}
.gradio-container button.stop, .gradio-container .ringfour-stop button {
  background: #f39788 !important;
  color: #080808 !important;
}
::-webkit-scrollbar { width: 9px; height: 9px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: #444; border-radius: 4px; }
"""


@lru_cache(maxsize=1)
def _css_with_fonts() -> str:
    """Embed the licensed local display font so mission UIs need no CDN."""
    faces = []
    for name, weight in _FONT_FILES:
        encoded = base64.b64encode((_FONT_DIR / f"{name}.woff2").read_bytes()).decode("ascii")
        faces.append(
            "@font-face { font-family: 'Space Grotesk'; "
            f"src: url(data:font/woff2;base64,{encoded}) format('woff2'); "
            f"font-style: normal; font-weight: {weight}; font-display: swap; }}"
        )
    return "\n".join(faces) + "\n" + CSS


def theme() -> Any:
    """Return a dark Gradio theme using Ringfour's interface colors."""
    try:
        import gradio as gr
    except ImportError as exc:  # pragma: no cover - installation dependent
        raise ImportError("ringfour_ui.theme() requires gradio==6.32.0") from exc

    return gr.themes.Base(
        primary_hue=gr.themes.Color(
            c50="#fff5e8", c100="#ffe6c7", c200="#ffd199", c300="#ffb15a",
            c400="#f39737", c500=ACCENT, c600="#c96a00", c700="#a95700",
            c800="#874700", c900="#653600", c950="#432400",
        ),
        secondary_hue=gr.themes.Color(
            c50="#e6f8f5", c100="#c9eee8", c200="#a4e1d7", c300="#7bd2c4",
            c400="#55c3b1", c500=READY, c600="#2d9785", c700="#247968",
            c800="#1b5c4f", c900="#123f36", c950="#09211d",
        ),
        neutral_hue=gr.themes.colors.zinc,
        font=[gr.themes.Font("Inter"), gr.themes.Font("Arial"), gr.themes.Font("sans-serif")],
        font_mono=[
            gr.themes.Font("ui-monospace"),
            gr.themes.Font("SFMono-Regular"),
            gr.themes.Font("monospace"),
        ],
    ).set(
        body_background_fill=PAGE,
        body_background_fill_dark=PAGE,
        background_fill_primary=SURFACE,
        background_fill_primary_dark=SURFACE,
        background_fill_secondary=INSET,
        background_fill_secondary_dark=INSET,
        block_background_fill=SURFACE,
        block_background_fill_dark=SURFACE,
        block_border_color="#2b2b2b",
        block_border_color_dark="#2b2b2b",
        body_text_color="#f5f5f5",
        body_text_color_dark="#f5f5f5",
        body_text_color_subdued="#b8b8b8",
        body_text_color_subdued_dark="#b8b8b8",
        border_color_primary="#2b2b2b",
        border_color_primary_dark="#2b2b2b",
        color_accent=ACCENT,
        color_accent_soft="#e47a0020",
        color_accent_soft_dark="#e47a0020",
        button_primary_background_fill=ACCENT,
        button_primary_background_fill_dark=ACCENT,
        button_primary_background_fill_hover=ACCENT_HOVER,
        button_primary_background_fill_hover_dark=ACCENT_HOVER,
        button_primary_text_color="#080808",
        button_primary_text_color_dark="#080808",
        input_background_fill=INSET,
        input_background_fill_dark=INSET,
        input_border_color="#2b2b2b",
        input_border_color_dark="#2b2b2b",
        link_text_color=READY,
        link_text_color_dark=READY,
    )


def launch(app: Any, **kwargs: Any) -> Any:
    """Launch with Ringfour styling and private, non-sharing defaults.

    Explicit Gradio launch options remain available to mission authors.
    """
    kwargs.setdefault("theme", theme())
    kwargs.setdefault("css", _css_with_fonts())
    kwargs.setdefault("server_name", "127.0.0.1")
    assigned_port = os.environ.get("RINGFOUR_UI_PORT")
    if "server_port" not in kwargs and assigned_port is not None:
        if not assigned_port.isascii() or not assigned_port.isdecimal():
            raise ValueError("RINGFOUR_UI_PORT must be an integer from 1 to 65535")
        port = int(assigned_port)
        if not 1 <= port <= 65535:
            raise ValueError("RINGFOUR_UI_PORT must be an integer from 1 to 65535")
        kwargs.setdefault("server_port", port)

    root_path = os.environ.get("RINGFOUR_UI_ROOT_PATH")
    if "root_path" not in kwargs and root_path is not None:
        if (not root_path.startswith("/") or root_path.startswith("//") or "//" in root_path
                or ".." in root_path.split("/")
                or not re.fullmatch(r"/[A-Za-z0-9/_-]*", root_path)):
            raise ValueError("RINGFOUR_UI_ROOT_PATH must be a safe absolute URL path")
        kwargs.setdefault("root_path", root_path)
    kwargs.setdefault("share", False)
    kwargs.setdefault("run_history", False)
    kwargs.setdefault("footer_links", [])
    return app.launch(**kwargs)
