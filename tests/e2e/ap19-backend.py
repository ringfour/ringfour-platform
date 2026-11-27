#!/usr/bin/env python3
"""API/daemon/engine benchmark in the disposable AP-17 fixture; no browser timing."""

import concurrent.futures
import json
import math
import platform
import re
import statistics
import time
from datetime import UTC, datetime
from pathlib import Path

import httpx


def summary(values):
    ordered = sorted(values)
    return {
        "n": len(values),
        "p50_ms": round(statistics.median(values), 3),
        "p95_ms": round(ordered[math.ceil(len(ordered) * 0.95) - 1], 3),
        "max_ms": round(max(values), 3),
    }


def stamp(value):
    return datetime.fromisoformat(value)


def duration(first, last):
    return (
        (stamp(last) - stamp(first)).total_seconds() * 1000 if first and last else None
    )


def wait(read, message, timeout=45):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        value = read()
        if value:
            return value
        time.sleep(0.01)
    raise AssertionError(message)


def benchmark(c, report_path, samples=20):
    if samples < 5:
        raise ValueError(
            "At least five samples are required; 20+ is preferred for p95."
        )
    client, checked, post = (c[k] for k in ("client", "checked", "post"))
    resource, revision, body = (
        c[k] for k in ("bench_resource", "bench_revision", "bench_start_body")
    )
    organization = "/v1/organizations/" + c["org"]
    report = {
        "captured_at": datetime.now(UTC).isoformat(),
        "environment": {
            "python": platform.python_version(),
            "kernel": platform.release(),
            "machine": platform.machine(),
            "dotnet": c["support"].command(["dotnet", "--version"]),
            "api_sweep_ms": int(c["api_env"]["Apps__InstallationSweepMilliseconds"]),
            "daemon_run_fallback_ms": 500,
            "daemon_engine_reconciliation_ms": 1000,
            "robot_count": 1,
            "engine_storage_driver": "vfs",
            "auth": "verified RS256 Development-only test JWT; no Auth0 network/token acquisition",
            "http": "loopback HTTP; persistent clients unless fresh_tcp is specified",
            "runtime": "real pinned Python wheel client in Docker; final summary file on every stop; no simulated sensor claim",
        },
        "method": {
            "percentile": "nearest rank; median p50",
            "lifecycle_samples": samples,
            "reads_per_endpoint": 100,
            "state_poll_ms": 10,
            "limits": [
                "One real Agent and one enrolled robot; this is not the ten-robot/browser acceptance.",
                "Disposable nested Docker uses vfs and includes a test Unix/TCP proxy and runtime stdio relay; it is not the live simulator storage/network topology.",
                "Response timing includes client serialization, networking, authentication, API logic, database and response body consumption.",
                "Phase timestamps come from persisted API/daemon/engine records; small differences can include clock and observation granularity.",
                "Docker proxy timings measure first response byte, not completion of streaming pulls or logs.",
            ],
        },
        "api_reads": [],
        "lifecycle": [],
    }
    if Path("/proc/cpuinfo").exists():
        report["environment"]["cpu"] = next(
            (
                line.split(":", 1)[1].strip()
                for line in Path("/proc/cpuinfo").read_text().splitlines()
                if line.startswith("model name")
            ),
            "unknown",
        )

    def sql(query):
        return c["support"].command(
            [
                "docker",
                "exec",
                c["pg"],
                "psql",
                "-U",
                "ap07",
                "-d",
                "ap07",
                "-At",
                "-c",
                query,
            ]
        )

    sql("CREATE EXTENSION IF NOT EXISTS pg_stat_statements")

    def read(path):
        began = time.perf_counter()
        response = client.get(path)
        response.raise_for_status()
        return (time.perf_counter() - began) * 1000

    def measure_reads(label, apps):
        paths = {
            "robots": organization + "/robots?limit=50",
            "imports": organization + "/app-imports?limit=50",
            "active_runs": organization + "/app-runs?active_only=true&limit=50",
            "recent_runs": organization + "/app-runs?recent_first=true&limit=20",
            "installations": f"/v1/robots/{c['robot']}/app-installations?limit=50",
            "app_logbook": organization + "/app-logbook?recent_first=true&limit=50",
            "settings": resource + "/settings",
            "files": f"/v1/apps/{revision['app_id']}/artifacts?organization_id={c['org']}&limit=50",
        }
        sql("SELECT pg_stat_statements_reset()")
        for name, path in paths.items():
            for _ in range(3):
                read(path)
            raw = [read(path) for _ in range(100)]
            report["api_reads"].append(
                {
                    "dataset": label,
                    "apps": apps,
                    "endpoint": name,
                    "summary": summary(raw),
                    "raw_ms": raw,
                }
            )
        with concurrent.futures.ThreadPoolExecutor(max_workers=8) as workers:
            concurrent_raw = list(
                workers.map(lambda _: read(paths["active_runs"]), range(200))
            )
        report["api_reads"].append(
            {
                "dataset": label,
                "apps": apps,
                "endpoint": "active_runs_concurrency_8",
                "summary": summary(concurrent_raw),
                "raw_ms": concurrent_raw,
            }
        )
        stats = json.loads(
            sql(
                "SELECT COALESCE(json_agg(row_to_json(s)), '[]'::json) FROM (SELECT left(query,500) AS query, calls,total_exec_time,mean_exec_time,max_exec_time,rows,shared_blks_hit,shared_blks_read FROM pg_stat_statements WHERE dbid=(SELECT oid FROM pg_database WHERE datname=current_database()) AND query NOT LIKE '%pg_stat_statements%' ORDER BY total_exec_time DESC LIMIT 15) s"
            )
        )
        report.setdefault("postgres", []).append(
            {"dataset": label, "statements": stats}
        )

    existing = checked(client.get(organization + "/app-imports?limit=100"))["items"]
    apps = len({item["app_id"] for item in existing})
    measure_reads("initial", apps)
    for target in (10, 100):
        for number in range(apps, target):
            manifest = json.loads(json.dumps(revision["manifest"]))
            manifest["app_key"] = "ap19-fixture-" + str(number)
            published = checked(
                post(
                    organization + "/app-revisions",
                    {"manifest": manifest, "visibility": "unlisted"},
                )
            )
            checked(
                post(
                    organization + "/app-imports",
                    {"revision_id": published["id"], "share_id": None},
                )
            )
        apps = target
        measure_reads(f"{target}_apps", apps)

    cold = []
    for _ in range(20):
        with httpx.Client(
            base_url=str(client.base_url), headers=dict(client.headers), trust_env=False
        ) as fresh:
            began = time.perf_counter()
            fresh.get(
                organization + "/app-runs?active_only=true&limit=50"
            ).raise_for_status()
            cold.append((time.perf_counter() - began) * 1000)
    report["api_fresh_tcp"] = {"summary": summary(cold), "raw_ms": cold}
    daemon_reads = {}
    with httpx.Client(
        base_url=f"http://127.0.0.1:{c['daemon_port']}", trust_env=False
    ) as daemon:
        for path in ("/health/live", "/health/ready"):
            values = []
            for _ in range(50):
                began = time.perf_counter()
                daemon.get(path).raise_for_status()
                values.append((time.perf_counter() - began) * 1000)
            daemon_reads[path] = {"summary": summary(values), "raw_ms": values}
    report["daemon_reads"] = daemon_reads

    with httpx.Client(
        base_url="http://engine",
        transport=httpx.HTTPTransport(uds=c["socket_path"]),
        timeout=30,
    ) as engine:
        report["environment"]["engine_version"] = engine.get("/version").json()[
            "Version"
        ]
        direct = {}
        for path in ("/_ping", "/v1.46/info", "/v1.46/containers/json?all=true"):
            values = []
            for _ in range(50):
                began = time.perf_counter()
                engine.get(path).raise_for_status()
                values.append((time.perf_counter() - began) * 1000)
            direct[path] = {"summary": summary(values), "raw_ms": values}
        report["docker_direct"] = direct

    first_timing = len(c["engine_timings"])
    for index in range(samples):
        began = time.perf_counter()
        accepted = checked(post(resource + "/start", body))
        response_ms = (time.perf_counter() - began) * 1000
        run_path = "/v1/app-runs/" + accepted["run_id"]

        def running(path=run_path):
            result = checked(client.get(path))
            if result["state"] in ("failed", "interrupted"):
                raise AssertionError(result)
            return result if result["state"] == "running" else None

        current = wait(running, "API observed start")
        start_visible_ms = (time.perf_counter() - began) * 1000
        start_operation = checked(
            client.get("/v1/app-operations/" + accepted["operation_id"])
        )
        # Wait until the App has confirmed initial settings before requesting a graceful stop.
        wait(
            lambda: checked(client.get(resource + "/settings"))["last_applied"],
            "App initial settings",
        )
        began = time.perf_counter()
        stopped = checked(
            post(resource + "/stop", {"expected_run_id": accepted["run_id"]})
        )
        stop_response_ms = (time.perf_counter() - began) * 1000
        terminal = wait(
            lambda path=run_path: (
                result
                if (result := checked(client.get(path)))["state"] == "stopped"
                else None
            ),
            "API observed stop",
        )
        stop_visible_ms = (time.perf_counter() - began) * 1000
        stop_operation = checked(
            client.get("/v1/app-operations/" + stopped["operation_id"])
        )
        report["lifecycle"].append(
            {
                "index": index,
                "run_id": accepted["run_id"],
                "start_response_ms": response_ms,
                "start_observed_ms": start_visible_ms,
                "start_dispatch_ack_ms": duration(
                    start_operation["requested_at"], start_operation["acknowledged_at"]
                ),
                "start_ack_to_engine_ms": duration(
                    start_operation["acknowledged_at"], current["started_at"]
                ),
                "start_engine_to_hosted_ms": duration(
                    current["started_at"], current["received_at"]
                ),
                "stop_response_ms": stop_response_ms,
                "stop_observed_ms": stop_visible_ms,
                "stop_dispatch_ack_ms": duration(
                    stop_operation["requested_at"], stop_operation["acknowledged_at"]
                ),
                "stop_ack_to_engine_end_ms": duration(
                    stop_operation["acknowledged_at"], terminal["ended_at"]
                ),
                "stop_engine_end_to_hosted_ms": duration(
                    terminal["ended_at"], terminal["received_at"]
                ),
                "exit_code": terminal["exit_code"],
            }
        )
        assert terminal["exit_code"] == 0, terminal
        print(
            json.dumps(
                {
                    "ap19_sample": index + 1,
                    "start_ms": round(start_visible_ms, 1),
                    "stop_ms": round(stop_visible_ms, 1),
                }
            ),
            flush=True,
        )
    keys = [key for key in report["lifecycle"][0] if key.endswith("_ms")]
    report["lifecycle_summary"] = {
        key: summary([row[key] for row in report["lifecycle"] if row[key] is not None])
        for key in keys
    }
    grouped = {}
    for timing in c["engine_timings"][first_timing:]:
        route = re.sub(r"[a-f0-9]{64}", "{id}", timing["request"]).split("?", 1)[0]
        grouped.setdefault(route, []).append(timing["first_response_byte_ms"])
    report["docker_proxy_first_byte"] = {
        route: summary(values) for route, values in grouped.items()
    }
    report["historical_runs_after"] = len(
        checked(client.get(organization + "/app-runs?limit=100"))["items"]
    )
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(report, indent=2) + "\n")
    print(
        json.dumps(
            {
                "ap19_backend_report": str(report_path),
                "lifecycle_summary": report["lifecycle_summary"],
            }
        ),
        flush=True,
    )
