"""Separate read-only LAN page: never exposes the daemon control API."""
import html
import json
from datetime import UTC, datetime
from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import FileResponse, HTMLResponse

from .enrollment import credential_error, load_identity
from .pairing import format_code, pending_path

STATIC = Path(__file__).parent / "static"


def create_status_app(settings, control_app=None):
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

    @app.get("/fonts/{font}")
    def font_file(font: str):
        if font not in {"SpaceGrotesk-Medium.woff2", "inter-latin-400-normal.woff2"}:
            return HTMLResponse("Not found", status_code=404)
        return FileResponse(STATIC / font, media_type="font/woff2")

    @app.get("/", response_class=HTMLResponse)
    def page():
        identity = load_identity(settings.db_path)
        if identity:
            broken = credential_error(settings.credential_path)
            manager = getattr(control_app.state, "connection_manager", None) if control_app else None
            state = ("Credential missing" if broken else "Enrolled · connected" if manager and manager.connected
                     else "Enrolled · reconnecting")
            detail = f"Robot ID: {identity['robot_id']}"
            extra = "Check the daemon credential." if broken else "This device is paired."
            style = "error" if broken else "ready"
        else:
            state, detail, extra, style = "Waiting for platform", "", "Connecting to the Ringfour platform…", "neutral"
            try:
                pending = json.loads(pending_path(settings).read_text())
                expiry = datetime.fromisoformat(pending["expires_at"])
                if pending["platform_url"] == settings.platform_url and expiry > datetime.now(UTC):
                    state = "Ready to pair"
                    detail = format_code(pending["code"])
                    extra = f"Enter this code in the Ringfour Console to add your robot. Expires {pending['expires_at']}."
                    style = "brand"
            except (OSError, KeyError, ValueError, TypeError):
                pass
        markup = f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta http-equiv="refresh" content="5"><title>Ringfour device</title>
<style>@font-face{{font-family:Space;src:url('/fonts/SpaceGrotesk-Medium.woff2')}}@font-face{{font-family:Inter;src:url('/fonts/inter-latin-400-normal.woff2')}}*{{box-sizing:border-box}}body{{margin:0;background:#111315;color:#edeff0;font:16px Inter,sans-serif}}main{{max-width:640px;margin:10vh auto;padding:0 24px}}h1{{font:32px Space,sans-serif;margin:0 0 24px}}section{{border:1px solid #363b3e;background:#1b1e20;padding:28px}}h2{{font:22px Space,sans-serif;margin:0 0 18px}}.brand{{color:#e47a00}}.ready{{color:#36b5a0}}.error{{color:#e35d5d}}.neutral{{color:#b6bcbf}}.detail{{font:22px Space,sans-serif;overflow-wrap:anywhere}}p{{line-height:1.5;color:#c5cbce}}small{{color:#9da5a8}}</style></head>
<body><main><h1>Ringfour</h1><section><h2 class="{style}">{state}</h2><div class="detail">{html.escape(detail)}</div><p>{html.escape(extra)}</p><small>Refreshes automatically every 5 seconds.</small></section></main></body></html>'''
        return HTMLResponse(markup, headers={"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; font-src 'self'; base-uri 'none'; frame-ancestors 'none'"})

    return app
