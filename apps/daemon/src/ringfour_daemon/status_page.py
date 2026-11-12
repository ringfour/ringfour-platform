"""Read-only LAN status page, using local operation records and Runner output."""
import asyncio
import base64
import html
import json
import sqlite3
from datetime import UTC, datetime
from pathlib import Path

import httpx
from fastapi import FastAPI
from fastapi.responses import FileResponse, HTMLResponse

from .enrollment import credential_error, load_identity
from .pairing import format_code, pending_path

STATIC = Path(__file__).parent / "static"
OUTPUT_LIMIT = 65536
DISPLAY_LIMIT = 8192


def _activity(db_path):
    """Read only known operation fields; never render raw payloads or results."""
    try:
        with sqlite3.connect(Path(db_path).resolve().as_uri() + "?mode=ro", uri=True) as conn:
            rows = conn.execute(
                "SELECT operation_id, kind, state, updated_at, payload FROM device_operations "
                "WHERE kind IN ('mission.prepare','script.execution.start','script.execution.stop') "
                "ORDER BY rowid DESC LIMIT 12"
            ).fetchall()
            latest_row = conn.execute(
                "SELECT operation_id, payload FROM device_operations "
                "WHERE kind='script.execution.start' ORDER BY rowid DESC LIMIT 1"
            ).fetchone()
    except sqlite3.Error:
        return [], None
    events, latest = [], None
    if latest_row:
        try:
            latest_payload = json.loads(latest_row[1])
            filename = latest_payload.get("entrypoint") if isinstance(latest_payload, dict) else None
            if not isinstance(filename, str) or len(filename) > 240:
                filename = None
            is_mission = isinstance(latest_payload, dict) and "mission_revision_id" in latest_payload
            latest = (latest_row[0], filename, is_mission)
        except (ValueError, TypeError):
            pass
    for operation_id, kind, state, updated_at, raw in rows:
        try:
            payload = json.loads(raw)
            if not isinstance(payload, dict):
                continue
        except (ValueError, TypeError):
            continue
        filename = payload.get("entrypoint")
        if not isinstance(filename, str) or len(filename) > 240:
            filename = None
        if kind == "mission.prepare":
            title = {"accepted": "Mission received", "running": "Preparing mission",
                     "succeeded": "Mission prepared", "failed": "Mission preparation failed",
                     "cancelled": "Mission preparation cancelled"}.get(state, "Mission preparation")
        elif kind == "script.execution.start":
            is_mission = "mission_revision_id" in payload
            title = {"accepted": "Mission start received", "running": "Starting mission",
                     "succeeded": "Mission start accepted", "failed": "Mission start failed",
                     "cancelled": "Mission start cancelled"}.get(state, "Mission start") if is_mission else {
                         "accepted": "Execution received", "running": "Starting execution",
                         "succeeded": "Execution started", "failed": "Execution start failed",
                         "cancelled": "Execution start cancelled"}.get(state, "Execution")
        else:
            title = {"accepted": "Stop received", "running": "Stopping execution",
                     "succeeded": "Stop requested", "failed": "Stop request failed",
                     "cancelled": "Stop request cancelled"}.get(state, "Stop request")
        events.append((title, filename, state, updated_at))
    return events, latest


async def _runner_snapshot(settings, execution_id, client_factory):
    if not settings.runner_auth_token:
        return None, "Runner authentication is not configured."
    try:
        async with asyncio.timeout(3), client_factory(
            base_url=settings.runner_url,
            headers={"authorization": f"Bearer {settings.runner_auth_token}"}, timeout=2,
        ) as client:
            response = await client.get(f"/v1/executions/{execution_id}")
            if response.status_code == 404:
                return None, "Execution is not available in Runner yet."
            response.raise_for_status()
            state = response.json()["state"]
            if state not in {"queued", "preparing", "running", "succeeded", "failed", "cancelled"}:
                raise ValueError("Invalid execution state")
            output = {}
            for stream in ("stdout", "stderr"):
                offset, tail, truncated = 0, bytearray(), False
                while offset < OUTPUT_LIMIT:
                    response = await client.get(f"/v1/executions/{execution_id}/output", params={
                        "stream": stream, "offset": offset, "limit": 16384,
                    })
                    response.raise_for_status()
                    chunk = response.json()
                    data = base64.b64decode(chunk["data_base64"], validate=True)
                    if len(data) > 16384 or chunk["offset"] != offset or chunk["next_offset"] != offset + len(data):
                        raise ValueError("Invalid Runner output")
                    offset += len(data)
                    tail.extend(data)
                    tail = tail[-DISPLAY_LIMIT:]
                    truncated = bool(chunk["truncated"])
                    if len(data) < 16384:
                        break
                output[stream] = (bytes(tail).decode("utf-8", errors="replace"), offset > DISPLAY_LIMIT, truncated)
            return {"state": state, "output": output}, None
    except (httpx.HTTPError, OSError, TimeoutError, ValueError, KeyError, TypeError):
        return None, "Runner is unavailable or returned an invalid response."


def create_status_app(settings, control_app=None, client_factory=httpx.AsyncClient):
    app = FastAPI(docs_url=None, redoc_url=None, openapi_url=None)

    @app.get("/fonts/{font}")
    def font_file(font: str):
        if font not in {"SpaceGrotesk-Medium.woff2", "inter-latin-400-normal.woff2"}:
            return HTMLResponse("Not found", status_code=404)
        return FileResponse(STATIC / font, media_type="font/woff2")

    @app.get("/", response_class=HTMLResponse)
    async def page():
        identity = load_identity(settings.db_path)
        activity_html = ""
        if identity:
            broken = credential_error(settings.credential_path)
            manager = getattr(control_app.state, "connection_manager", None) if control_app else None
            state = ("Credential missing" if broken else "Enrolled · connected" if manager and manager.connected
                     else "Enrolled · reconnecting")
            detail = f"Robot ID: {identity['robot_id']}"
            extra = "Check the daemon credential." if broken else "This device is paired."
            style = "error" if broken else "ready"
            events, latest = _activity(settings.db_path)
            rows = "".join(
                f'<li><span><strong>{html.escape(title)}</strong>'
                f'{" · " + html.escape(filename) if filename else ""}'
                f'</span><small>{html.escape(updated_at)} · {html.escape(state_name)}</small></li>'
                for title, filename, state_name, updated_at in events
            ) or '<li class="muted">No mission or execution activity yet.</li>'
            output_html = '<p class="muted">No execution output yet.</p>'
            if latest:
                snapshot, error = await _runner_snapshot(settings, latest[0], client_factory)
                if error:
                    output_html = f'<p class="muted">{html.escape(error)}</p>'
                else:
                    filename = f" · {html.escape(latest[1])}" if latest[1] else ""
                    label = "Mission active" if latest[2] and snapshot["state"] == "running" else snapshot["state"].capitalize()
                    output_html = f'<p class="execution">{html.escape(label)}{filename}</p>'
                    for stream in ("stdout", "stderr"):
                        content, shortened, truncated = snapshot["output"][stream]
                        note = " · showing latest output" if shortened else ""
                        note += " · Runner output limit reached" if truncated else ""
                        output_html += f'<h3>{stream}{note}</h3><pre>{html.escape(content) if content else "No output yet."}</pre>'
            activity_html = f'''<section><h2>Recent activity</h2><ul>{rows}</ul>
<p class="muted">Prepared means ready on the device. Check deployment status in Ringfour for activation.</p></section>
<section><h2>Latest execution</h2>{output_html}</section>
<p class="notice">Anyone who can reach this page can read the displayed output.</p>'''
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
<style>@font-face{{font-family:Space;src:url('/fonts/SpaceGrotesk-Medium.woff2')}}@font-face{{font-family:Inter;src:url('/fonts/inter-latin-400-normal.woff2')}}*{{box-sizing:border-box}}body{{margin:0;background:#111315;color:#edeff0;font:16px Inter,sans-serif}}main{{max-width:760px;margin:8vh auto;padding:0 24px}}h1{{font:32px Space,sans-serif;margin:0 0 24px}}section{{border:1px solid #363b3e;background:#1b1e20;padding:24px;margin-bottom:12px}}h2{{font:22px Space,sans-serif;margin:0 0 18px}}h3{{font-size:13px;text-transform:uppercase;color:#9da5a8;margin:20px 0 8px}}.brand{{color:#e47a00}}.ready{{color:#36b5a0}}.error{{color:#e35d5d}}.neutral,.muted{{color:#9da5a8}}.detail{{font:20px Space,sans-serif;overflow-wrap:anywhere}}p{{line-height:1.5;color:#c5cbce}}small{{color:#9da5a8}}ul{{list-style:none;padding:0;margin:0}}li{{display:flex;flex-wrap:wrap;justify-content:space-between;gap:6px 16px;padding:12px 0;border-bottom:1px solid #363b3e;overflow-wrap:anywhere}}li:last-child{{border:0}}.execution{{color:#36b5a0}}pre{{background:#111315;border:1px solid #363b3e;padding:14px;white-space:pre-wrap;overflow-wrap:anywhere;max-height:320px;overflow:auto;font:13px/1.5 monospace}}.notice{{font-size:13px;color:#9da5a8}}</style></head>
<body><main><h1>Ringfour</h1><section><h2 class="{style}">{state}</h2><div class="detail">{html.escape(detail)}</div><p>{html.escape(extra)}</p><small>Refreshes automatically every 5 seconds.</small></section>{activity_html}</main></body></html>'''
        return HTMLResponse(markup, headers={"Cache-Control": "no-store", "X-Content-Type-Options": "nosniff", "Content-Security-Policy": "default-src 'none'; style-src 'unsafe-inline'; font-src 'self'; base-uri 'none'; frame-ancestors 'none'"})

    return app
