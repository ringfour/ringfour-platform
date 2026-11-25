"""Read-only LAN status page, using local App records and output."""
import html
import json
from datetime import UTC, datetime
from pathlib import Path

from fastapi import FastAPI
from fastapi.responses import FileResponse, HTMLResponse

from .db import connect
from .enrollment import credential_error, load_identity
from .pairing import format_code, pending_path

STATIC = Path(__file__).parent / "static"
DISPLAY_LIMIT = 8192


def create_status_app(settings, control_app=None):
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
            events, latest = _app_activity(settings.db_path)
            rows = "".join(
                f'<li><span><strong>{html.escape(title)}</strong>'
                f'{" · " + html.escape(filename) if filename else ""}'
                f'</span><small>{html.escape(updated_at)} · {html.escape(state_name)}</small></li>'
                for title, filename, state_name, updated_at in events
            ) or '<li class="muted">No App activity yet.</li>'
            output_html = '<p class="muted">No execution output yet.</p>'
            if latest:
                snapshot, error = _app_snapshot(settings.db_path, latest[0])
                if error:
                    output_html = f'<p class="muted">{html.escape(error)}</p>'
                else:
                    filename = f" · {html.escape(latest[1])}" if latest[1] else ""
                    label = "App active" if latest[2] and snapshot["state"] == "running" else snapshot["state"].capitalize()
                    output_html = f'<p class="execution">{html.escape(label)}{filename}</p>'
                    for stream in ("stdout", "stderr"):
                        content, shortened, truncated = snapshot["output"][stream]
                        note = " · showing latest output" if shortened else ""
                        note += " · Output limit reached" if truncated else ""
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


def _app_activity(db_path):
    with connect(Path(db_path).resolve().as_uri() + "?mode=ro", uri=True) as conn:
        rows = conn.execute("SELECT run_id,state,observed_at FROM app_runs ORDER BY rowid DESC LIMIT 12").fetchall()
    return [("App " + state, None, state, observed or "Awaiting observation") for _,state,observed in rows], (None if not rows else (rows[0][0], "App", False))


def _app_snapshot(db_path, run_id):
    with connect(Path(db_path).resolve().as_uri() + "?mode=ro", uri=True) as conn:
        row = conn.execute("SELECT state,stdout,stderr,stdout_truncated,stderr_truncated FROM app_runs WHERE run_id=?", (run_id,)).fetchone()
    if not row:
        return None, "No observed App run."
    output = {name: (bytes(row[index])[-DISPLAY_LIMIT:].decode("utf-8", errors="replace"), len(row[index]) > DISPLAY_LIMIT, bool(row[index+2])) for name,index in (("stdout",1),("stderr",2))}
    return {"state": row[0], "output": output}, None
