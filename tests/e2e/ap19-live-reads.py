#!/usr/bin/env python3
"""Read-only authenticated latency sampling of the existing local deployment.

Run with uv run --project apps/cli python. Credentials stay in memory and are
never written to the report. /me can refresh the caller's verified profile as
part of its normal GET behavior. This never starts/stops workloads or stacks.
"""

import argparse
import importlib.util
import json
import time
from datetime import UTC, datetime
from pathlib import Path

import httpx
from ringfour_cli.auth import TokenStore
from ringfour_cli.config import load_settings


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location(
        "backend_stats", Path(__file__).with_name("ap19-backend.py")
    )
    backend = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(backend)
    settings = load_settings()
    tokens = TokenStore().load(settings)
    if not tokens or not tokens.get("access_token"):
        raise RuntimeError(
            "Log in with ringfour-cli login before collecting live measurements."
        )
    with httpx.Client(
        base_url=settings.api_url,
        headers={"Authorization": "Bearer " + tokens["access_token"]},
        trust_env=False,
        timeout=20,
    ) as client:
        orgs = client.get("/v1/organizations")
        orgs.raise_for_status()
        organizations = orgs.json()
        organizations = (
            organizations.get("items", [])
            if isinstance(organizations, dict)
            else organizations
        )
        org = settings.config.get("selected_org") or organizations[0]["id"]
        prefix = "/v1/organizations/" + org
        robots = client.get(prefix + "/robots")
        robots.raise_for_status()
        robot = robots.json()["items"][0]["id"] if robots.json()["items"] else None
        paths = {
            "me": "/v1/me",
            "robots": prefix + "/robots",
            "imports": prefix + "/app-imports",
            "active_runs": prefix + "/app-runs?active_only=true&limit=50",
            "recent_runs": prefix + "/app-runs?recent_first=true&limit=20",
            "app_logbook": prefix + "/app-logbook?recent_first=true&limit=50",
            "members": prefix + "/members",
        }
        if robot:
            paths |= {
                "robot_status": f"/v1/robots/{robot}/app-status",
                "installations": f"/v1/robots/{robot}/app-installations",
            }
        results = {}
        for name, path in paths.items():
            response = client.get(path)
            response.raise_for_status()
            raw = []
            for _ in range(10 if name == "me" else 100):
                began = time.perf_counter()
                response = client.get(path)
                response.raise_for_status()
                raw.append((time.perf_counter() - began) * 1000)
            results[name] = {"summary": backend.summary(raw), "raw_ms": raw}
        report = {
            "captured_at": datetime.now(UTC).isoformat(),
            "setup": {
                "api": settings.api_url,
                "authentication": "current cdr Auth0 access token; token acquisition excluded; /me includes normal external userinfo lookup",
                "transport": "persistent local HTTP connection; response body consumed",
                "mutations": "no workload or stack mutations",
            },
            "api": results,
        }
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2) + "\n")
        print(
            json.dumps(
                {name: item["summary"] for name, item in results.items()}, indent=2
            )
        )


if __name__ == "__main__":
    main()
