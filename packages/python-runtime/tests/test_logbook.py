import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest
from ringfour_runtime import logbook


def test_info_sends_author_fields_and_checks_acknowledgement(monkeypatch):
    received = []
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            received.append((self.path, self.headers.get("Authorization"), body))
            reply = json.dumps({"entry_id": body["entry_id"]}).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(reply)))
            self.end_headers()
            self.wfile.write(reply)
        def log_message(self, *args):
            pass
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        monkeypatch.setenv("RINGFOUR_LOGBOOK_URL", f"http://127.0.0.1:{server.server_port}")
        monkeypatch.setenv("RINGFOUR_LOGBOOK_TOKEN", "local-test-token")
        entry = logbook.info("Inspection skipped", station="B", reason="Operator reported maintenance")
        path, authorization, body = received[0]
        assert path == "/v1/runtime/logbook/entries" and authorization == "Bearer local-test-token"
        assert body == {"entry_id": entry, "level": "info", "message": "Inspection skipped", "fields": {"station": "B", "reason": "Operator reported maintenance"}}
    finally:
        server.shutdown()
        server.server_close()
        worker.join()


def test_invalid_input_and_missing_runtime_fail_explicitly(monkeypatch):
    monkeypatch.delenv("RINGFOUR_LOGBOOK_URL", raising=False)
    monkeypatch.delenv("RINGFOUR_LOGBOOK_TOKEN", raising=False)
    with pytest.raises(logbook.LogbookError, match="runtime"):
        logbook.info("hello")
    for fields in ({"x": []}, {"x": float("nan")}, {"x": "a" * 513}, {"x": "\x00"}, {"x": 10 ** 50}, {"x": 1e100}, {"bad-key": "x"}):
        with pytest.raises(logbook.LogbookError):
            logbook.info("hello", **fields)


def test_lost_response_retries_same_entry_and_redirect_is_not_followed(monkeypatch):
    received = []
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            received.append(body)
            if len(received) == 1:
                self.close_connection = True
                return
            if len(received) == 2:
                reply = json.dumps({"entry_id": body["entry_id"]}).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(reply)))
                self.end_headers()
                self.wfile.write(reply)
                return
            self.send_response(307)
            self.send_header("Location", f"http://127.0.0.1:{self.server.server_port}/redirected")
            self.end_headers()
        def log_message(self, *args):
            pass
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    worker = threading.Thread(target=server.serve_forever, daemon=True)
    worker.start()
    try:
        monkeypatch.setenv("RINGFOUR_LOGBOOK_URL", f"http://127.0.0.1:{server.server_port}")
        monkeypatch.setenv("RINGFOUR_LOGBOOK_TOKEN", "local-test-token")
        logbook.info("one", station="B")
        assert len(received) == 2 and received[0] == received[1]
        with pytest.raises(logbook.LogbookError, match="HTTP 307"):
            logbook.info("two")
        assert len(received) == 3
    finally:
        server.shutdown()
        server.server_close()
        worker.join()
