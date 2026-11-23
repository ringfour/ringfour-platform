"""Execution-scoped status for the optional private mission UI."""

import asyncio
from urllib.parse import unquote

import httpx
from fastapi import APIRouter, Request
from fastapi.responses import StreamingResponse
from starlette.background import BackgroundTask

from .executions import TERMINAL, authorize, error, get_row, valid_id

router = APIRouter()
UI_STARTUP_GRACE_SECONDS = 30
UI_PROBE_TIMEOUT_SECONDS = 0.4
UI_PROXY_BODY_LIMIT = 1024 * 1024
UI_PROXY_HEADER_LIMIT = 64
UI_PROXY_HEADER_BYTES = 16 * 1024
UI_PROXY_METHODS = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"]
UI_PROXY_HOP_HEADERS = {
    "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
    "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade", "expect",
}


async def probe(port: int) -> bool:
    """Return whether the Runner's fixed local UI port serves HTTP."""
    writer = None
    try:
        reader, writer = await asyncio.wait_for(
            asyncio.open_connection("127.0.0.1", port), UI_PROBE_TIMEOUT_SECONDS
        )
        writer.write(
            b"GET / HTTP/1.0\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n"
        )
        await asyncio.wait_for(writer.drain(), UI_PROBE_TIMEOUT_SECONDS)
        line = await asyncio.wait_for(reader.readline(), UI_PROBE_TIMEOUT_SECONDS)
        parts = line.split(b" ", 2)
        return (
            len(parts) >= 2
            and parts[0] in (b"HTTP/1.0", b"HTTP/1.1")
            and parts[1].isdigit()
            and 200 <= int(parts[1]) < 400
        )
    except (OSError, TimeoutError):
        return False
    finally:
        if writer is not None:
            writer.close()
            try:
                await writer.wait_closed()
            except OSError:
                pass


@router.get("/v1/executions/{execution_id}/ui")
async def status(request: Request, execution_id: str):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    if not valid_id(execution_id):
        return error(400, "invalid_input", "Invalid execution ID.")

    row = get_row(request.app.state.settings, execution_id)
    if row is None:
        return error(404, "not_found", "Execution not found.")

    if row["state"] in TERMINAL:
        state = "ended"
    elif row["state"] != "running":
        state = "starting"
    elif await probe(request.app.state.settings.ui_port):
        state = "ready"
    else:
        started_at = request.app.state.ui_started_at.get(execution_id)
        elapsed = asyncio.get_running_loop().time() - started_at if started_at else 0
        state = "unavailable" if elapsed >= UI_STARTUP_GRACE_SECONDS else "starting"

    return {"execution_id": execution_id, "state": state}


@router.get("/v1/ui")
async def active_statuses(request: Request):
    """Return bounded status snapshots for executions that can have a UI."""
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    from .executions import connection

    with connection(request.app.state.settings) as conn:
        rows = conn.execute(
            "SELECT execution_id FROM executions WHERE state NOT IN ('succeeded','failed','cancelled') "
            "ORDER BY created_at DESC LIMIT 64"
        ).fetchall()
    statuses = [await status(request, row[0]) for row in rows]
    return {"executions": statuses}


def safe_proxy_path(raw_path: bytes, execution_id: str) -> str | None:
    prefix = f"/v1/executions/{execution_id}/ui-proxy".encode("ascii")
    if not raw_path.startswith(prefix):
        return None
    suffix = raw_path[len(prefix):] or b"/"
    if not suffix.startswith(b"/") or suffix.startswith(b"//"):
        return None
    try:
        path = suffix.decode("ascii")
    except UnicodeDecodeError:
        return None
    decoded = path
    for _ in range(5):
        next_value = unquote(decoded)
        if next_value == decoded:
            break
        decoded = next_value
    if ("\\" in decoded or "#" in decoded or
            any(ord(char) < 32 or ord(char) == 127 for char in decoded) or
            any(segment == ".." for segment in decoded.split("/"))):
        return None
    return path


def mission_cookie_without_platform_session(value: str) -> str:
    retained = []
    for part in value.split(";"):
        name, separator, contents = part.strip().partition("=")
        if separator and name.strip().lower() != "ringfour_ui_session":
            retained.append(f"{name.strip()}={contents.strip()}")
    return "; ".join(retained)


@router.api_route("/v1/executions/{execution_id}/ui-proxy", methods=UI_PROXY_METHODS)
@router.api_route("/v1/executions/{execution_id}/ui-proxy/{path:path}", methods=UI_PROXY_METHODS)
async def proxy_ui(request: Request, execution_id: str, path: str = ""):
    if not authorize(request):
        return error(401, "unauthenticated", "Runner authentication required.")
    if not valid_id(execution_id):
        return error(400, "invalid_input", "Invalid execution ID.")
    row = get_row(request.app.state.settings, execution_id)
    if row is None:
        return error(404, "not_found", "Execution not found.")
    if row["state"] != "running":
        return error(409, "execution_not_running", "Execution is not running.")

    proxy_path = safe_proxy_path(request.scope.get("raw_path", b""), execution_id)
    query = request.scope.get("query_string", b"")
    if proxy_path is None or any(byte < 32 or byte == 127 for byte in query):
        return error(400, "invalid_input", "Invalid mission UI path.")
    try:
        query_text = query.decode("ascii")
    except UnicodeDecodeError:
        return error(400, "invalid_input", "Invalid mission UI query.")
    if len(request.headers) > UI_PROXY_HEADER_LIMIT or sum(
        len(name.encode("latin-1")) + len(value.encode("latin-1"))
        for name, value in request.headers.items()
    ) > UI_PROXY_HEADER_BYTES:
        return error(431, "headers_too_large", "Mission UI request headers exceed the limit.")
    content_length = request.headers.get("content-length")
    if content_length is not None:
        try:
            if int(content_length) > UI_PROXY_BODY_LIMIT:
                return error(413, "size_limit", "Mission UI request body exceeds 1 MiB.")
        except ValueError:
            return error(400, "invalid_input", "Invalid Content-Length.")
    semaphore = request.app.state.ui_proxy_semaphore
    try:
        await asyncio.wait_for(semaphore.acquire(), timeout=0.25)
    except TimeoutError:
        return error(503, "ui_busy", "Mission UI request capacity is full.")

    body = bytearray()
    try:
        async for chunk in request.stream():
            if len(body) + len(chunk) > UI_PROXY_BODY_LIMIT:
                semaphore.release()
                return error(413, "size_limit", "Mission UI request body exceeds 1 MiB.")
            body.extend(chunk)
    except Exception:
        semaphore.release()
        raise

    port = request.app.state.settings.ui_port
    if not await probe(port):
        semaphore.release()
        return error(503, "ui_unavailable", "Mission UI endpoint is unavailable.")

    upstream_headers = []
    connection_tokens = {
        token.strip().lower()
        for token in request.headers.get("connection", "").split(",")
        if token.strip()
    }
    for name, value in request.headers.items():
        key = name.lower()
        if (key in UI_PROXY_HOP_HEADERS or key in connection_tokens or
                key in {"host", "authorization", "forwarded"} or key.startswith("x-forwarded-")):
            continue
        if key == "cookie":
            value = mission_cookie_without_platform_session(value)
            if not value:
                continue
        upstream_headers.append((key, value))

    target = f"http://127.0.0.1:{port}{proxy_path}"
    if query_text:
        target += "?" + query_text
    client = httpx.AsyncClient(
        timeout=httpx.Timeout(120.0, connect=3.0), trust_env=False,
        follow_redirects=False,
    )
    try:
        upstream_request = client.build_request(
            request.method, target, headers=upstream_headers, content=bytes(body),
        )
        upstream = await client.send(upstream_request, stream=True)
    except (httpx.HTTPError, OSError):
        await client.aclose()
        semaphore.release()
        return error(503, "ui_unavailable", "Mission UI endpoint could not be reached.")

    response = StreamingResponse(
        upstream.aiter_raw(), status_code=upstream.status_code,
        background=BackgroundTask(_close_proxy, upstream, client, semaphore),
    )
    response_connection_tokens = {
        token.strip().lower().encode("latin-1")
        for token in upstream.headers.get("connection", "").split(",")
        if token.strip()
    }
    response.raw_headers = [
        (name.lower(), value)
        for name, value in upstream.headers.raw
        if name.decode("latin-1").lower() not in UI_PROXY_HOP_HEADERS
        and name.lower() not in response_connection_tokens
    ]
    response.headers["x-accel-buffering"] = "no"
    return response


async def _close_proxy(upstream, client, semaphore):
    await upstream.aclose()
    await client.aclose()
    semaphore.release()
