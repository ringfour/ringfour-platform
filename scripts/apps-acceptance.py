#!/usr/bin/env python3
"""Run the existing Apps acceptance suites sequentially without touching live stacks."""

from __future__ import annotations

import argparse
import json
import os
import re
import shlex
import shutil
import signal
import subprocess
import sys
import time
import uuid
from collections import Counter
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


@dataclass(frozen=True)
class Suite:
    id: str
    title: str
    command: tuple[str, ...]
    cwd: str = "."
    kind: str = "component"
    docker: bool = False
    agent: bool = False


def protocol(name: str, title: str, *, agent: bool = True) -> Suite:
    return Suite(
        name,
        title,
        (
            "uv",
            "run",
            "--locked",
            "--project",
            "apps/daemon",
            "python",
            f"tests/e2e/{name}.py",
        ),
        kind="isolated_protocol",
        docker=True,
        agent=agent,
    )


SUITES = (
    Suite(
        "contracts",
        "Contracts and representative fixtures",
        ("npm", "test", "--", "--test-reporter=spec"),
        "packages/contracts",
    ),
    Suite(
        "console-types",
        "Console type checking",
        ("npm", "run", "typecheck"),
        "apps/console",
    ),
    Suite(
        "console",
        "Console interactions, forms, routing and permissions",
        ("npm", "test", "--", "--reporter=verbose"),
        "apps/console",
    ),
    Suite(
        "cli",
        "CLI validation, targeting and commands",
        ("uv", "run", "--locked", "pytest", "-vv", "--durations=10"),
        "apps/cli",
    ),
    Suite(
        "daemon",
        "Daemon durability and App lifecycle",
        ("uv", "run", "--locked", "pytest", "-vv", "--durations=10"),
        "apps/daemon",
    ),
    Suite(
        "api",
        "API authorization, isolation, operations and persistence",
        (
            "dotnet",
            "test",
            "tests/Ringfour.Api.Tests",
            "--verbosity",
            "normal",
            "--logger",
            "console;verbosity=detailed",
        ),
        "apps/api",
        docker=True,
    ),
    protocol(
        "ap03-inventory", "Agent inventory through real isolated Daemon/API processes"
    ),
    protocol(
        "ap08-engine",
        "Nested Docker engine ownership and process recovery",
        agent=False,
    ),
    Suite(
        "ap19-backend",
        "Isolated lifecycle latency and PostgreSQL/engine timings",
        (
            "uv",
            "run",
            "--locked",
            "--project",
            "apps/daemon",
            "python",
            "tests/e2e/ap17-settings.py",
            "--benchmark-only",
            "--backend-report",
            "{output}/ap19-backend-report.json",
        ),
        kind="isolated_protocol",
        docker=True,
        agent=True,
    ),
    protocol(
        "ap05-catalog",
        "Catalog, private grants, imports and pull authorization",
        agent=False,
    ),
    protocol("ap06-publish", "Packaged CLI publish/build/push", agent=False),
    protocol("ap07-install", "Installation, configuration, update and rollback"),
    protocol("ap09-runs", "Run slots, stale stop and disposable Daemon replacement"),
    protocol("ap10-runtime", "Runtime output, artifacts and decisions"),
    protocol("ap-development", "App development: pinned source sync, image reuse and session closure"),
    protocol("ap17-settings", "Live/startup settings, fractions, files and recovery"),
    protocol(
        "ap18-lifecycle",
        "Official discovery, cross-workspace install, uninstall and reinstall",
    ),
    Suite(
        "browser",
        "Browser fixture interactions, responsive layout and screenshots",
        ("node", "scripts/apps-acceptance-browser.mjs"),
        kind="browser_fixture",
    ),
    Suite(
        "live-apps",
        "Real Auth0/CLI Robot Check, settings, stop, update, rollback and removal",
        (
            str(ROOT / "apps/cli/.venv/bin/python"),
            "tests/e2e/apps-live-acceptance.py",
        ),
        kind="live_workflow",
        docker=True,
    ),
)
QUICK = [s.id for s in SUITES[:5]]
INTEGRATION = [s.id for s in SUITES if s.docker and s.kind != "live_workflow"]
PROFILES = {
    "quick": QUICK,
    "integration": INTEGRATION,
    "full": [s.id for s in SUITES if s.kind != "live_workflow"],
    "live": ["live-apps"],
}

# These are automated counterparts, not a claim that the human/live rows passed.
COVERAGE = (
    (
        "Setup and identity",
        "1",
        ["api", "console"],
        "Real Auth0 identities, publisher entitlement and two enrolled robots",
    ),
    (
        "Publish, share and install",
        "2",
        ["api", "cli", "ap05-catalog", "ap06-publish", "ap07-install"],
        "Confirm publisher A / consumer B with their real CLI accounts",
    ),
    (
        "Run slots and independent targets",
        "3",
        ["api", "daemon", "ap09-runs"],
        "Two real robot sessions and cross-device continuity",
    ),
    (
        "State agreement and navigation",
        "3",
        ["console", "browser"],
        "Live deployed browser/CLI state agreement; fixture APIs do not establish this",
    ),
    (
        "IMU, settings and fractions",
        "3",
        ["api", "cli", "daemon", "ap17-settings"],
        "Reference Robot Check IMU behavior and its own summary artifact",
    ),
    (
        "Files, outputs and decisions",
        "3",
        ["api", "console", "ap10-runtime", "ap17-settings"],
        "Real device bytes and prompted human decision attribution",
    ),
    (
        "Storefront and installation context",
        "3",
        ["console", "browser", "ap18-lifecycle"],
        "Real deployed Store and installation acceptance",
    ),
    (
        "Update, rollback and removal",
        "4",
        ["api", "daemon", "ap07-install", "ap18-lifecycle"],
        "B's disposable live installation and A's continuing installation",
    ),
    (
        "Foreign IDs and roles",
        "5",
        ["api", "cli", "console"],
        "Real invite/email acceptance and the two users' deployed sessions",
    ),
    (
        "Keyboard, small screens and errors",
        "5",
        ["console", "browser"],
        "Visual judgment and real session expiry / recovery",
    ),
    (
        "Recovery and negative delivery",
        "6",
        [
            "api",
            "daemon",
            "ap09-runs",
            "ap10-runtime",
            "ap17-settings",
            "ap18-lifecycle",
        ],
        "Operator-only interruption of the live disposable topology; never automated by this runner",
    ),
    (
        "Conditional deployment gates",
        "7",
        ["contracts", "api"],
        "Hosted registry, HTTPS/Auth0, second supported platform and load/performance matrix",
    ),
)


def prerequisite(suite: Suite) -> str | None:
    if not shutil.which(suite.command[0]):
        return f"Missing executable: {suite.command[0]}"
    if suite.command[0] == "npm" and not (ROOT / suite.cwd / "node_modules").exists():
        return f"Install dependencies in {suite.cwd} first"
    if (
        suite.kind == "browser_fixture"
        and not (ROOT / "apps/console/node_modules/playwright").exists()
    ):
        return "Install Console dependencies and Playwright first"
    if suite.docker:
        try:
            result = subprocess.run(
                ["docker", "info", "--format", "{{.ServerVersion}}"],
                capture_output=True,
                timeout=10,
                check=False,
            )
            if result.returncode:
                return "Docker daemon is unavailable"
        except (OSError, subprocess.TimeoutExpired):
            return "Docker is unavailable"
        if suite.agent:
            try:
                image = subprocess.run(
                    ["docker", "image", "inspect", "ringfour-agent:local"],
                    capture_output=True,
                    timeout=10,
                    check=False,
                )
            except (OSError, subprocess.TimeoutExpired):
                return "Could not inspect the prebuilt Agent image"
            if image.returncode:
                return "Missing prebuilt ringfour-agent:local; ask the operator to prepare it"
            for sibling in ("ringfour-agent", "ringfour-sdk"):
                if not (ROOT.parent / sibling).is_dir():
                    return f"Missing sibling checkout: {sibling}"
    return None


def interrupt(process: subprocess.Popen, signum: int = signal.SIGINT) -> None:
    if process.poll() is not None:
        return
    # SIGINT lets the existing Python harnesses run their disposable cleanup.
    os.killpg(process.pid, signum)
    try:
        process.wait(timeout=30)
    except subprocess.TimeoutExpired:
        os.killpg(process.pid, signal.SIGKILL)
        process.wait()


def diagnostic(suite_id: str, message: str) -> None:
    timestamp = datetime.now(UTC).strftime("%H:%M:%S")
    # Test output may contain sample JWTs or failure responses. Never echo credentials.
    message = re.sub(r"(?i)Bearer\s+[A-Za-z0-9._~+/-]+=*", "Bearer [redacted]", message)
    message = re.sub(
        r"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        "[redacted JWT]",
        message,
    )
    print(f"[{timestamp} UTC] [{suite_id}] {message}", flush=True)


def run_suite(suite: Suite, output: Path, timeout: float) -> dict:
    # Evidence destinations are substituted as argument values, never shell text.
    command = tuple(part.replace("{output}", str(output)) for part in suite.command)
    result = {
        "id": suite.id,
        "title": suite.title,
        "kind": suite.kind,
        "command": list(command),
        "cwd": suite.cwd,
        "started_at": datetime.now(UTC).isoformat(),
    }
    diagnostic(suite.id, f"Checking prerequisites · {suite.title}")
    missing = prerequisite(suite)
    if missing:
        diagnostic(suite.id, f"PENDING · {missing}")
        return result | {
            "status": "pending",
            "reason": missing,
            "finished_at": datetime.now(UTC).isoformat(),
        }
    log = output / f"{suite.id}.log"
    diagnostic(suite.id, f"Command: {shlex.join(command)}")
    diagnostic(
        suite.id, f"Directory: {ROOT / suite.cwd} · timeout: {timeout:g}s · log: {log}"
    )
    env = os.environ | {
        "PYTHONUNBUFFERED": "1",
        "PYTHONDONTWRITEBYTECODE": "1",
        "APPS_ACCEPTANCE_OUTPUT": str(output / "browser"),
    }
    if suite.kind.startswith("live_"):
        env["PATH"] = (
            str(ROOT / "apps/cli/.venv/bin") + os.pathsep + env.get("PATH", "")
        )
    began = last_output = time.monotonic()
    pending = ""
    with log.open("w") as stream, log.open(errors="replace") as reader:

        def relay(final=False):
            nonlocal pending, last_output
            chunk = reader.read()
            if chunk:
                last_output = time.monotonic()
                pending += chunk
            while "\n" in pending:
                line, pending = pending.split("\n", 1)
                diagnostic(suite.id, line.rstrip("\r"))
            if final and pending:
                diagnostic(suite.id, pending)
                pending = ""

        try:
            process = subprocess.Popen(
                command,
                cwd=ROOT / suite.cwd,
                env=env,
                stdout=stream,
                stderr=subprocess.STDOUT,
                start_new_session=True,
            )
        except OSError as error:
            diagnostic(suite.id, f"FAILED to start · {error}")
            return result | {
                "status": "failed",
                "reason": str(error),
                "log": log.name,
                "finished_at": datetime.now(UTC).isoformat(),
            }
        try:
            while True:
                remaining = timeout - (time.monotonic() - began)
                if remaining <= 0:
                    raise subprocess.TimeoutExpired(suite.command, timeout)
                try:
                    code = process.wait(timeout=min(1, remaining))
                    break
                except subprocess.TimeoutExpired:
                    relay()
                    if time.monotonic() - last_output >= 10:
                        diagnostic(
                            suite.id,
                            f"Still running · {time.monotonic() - began:.1f}s elapsed · waiting for test output",
                        )
                        last_output = time.monotonic()
            status = "passed" if code == 0 else "pending" if code == 78 else "failed"
            reason = (
                "Browser prerequisites unavailable; see log"
                if code == 78
                else f"Process exited with code {code}; see {log.name}"
                if code
                else None
            )
        except subprocess.TimeoutExpired:
            diagnostic(
                suite.id, "Timeout reached · interrupting test and waiting for cleanup"
            )
            interrupt(process)
            code, status, reason = (
                process.returncode,
                "failed",
                "Timed out; inspect disposable cleanup in the suite log",
            )
        except KeyboardInterrupt:
            diagnostic(suite.id, "Interrupted · waiting for test cleanup")
            interrupt(process)
            raise
        finally:
            relay(final=True)
    seconds = round(time.monotonic() - began, 2)
    diagnostic(
        suite.id,
        f"{status.upper()} · {seconds:.2f}s · exit {code} · {reason or suite.title}",
    )
    return result | {
        "status": status,
        "returncode": code,
        "reason": reason,
        "seconds": seconds,
        "log": log.name,
        "finished_at": datetime.now(UTC).isoformat(),
    }


def write_report(
    output: Path, results: list[dict], selected: list[str], interrupted: bool = False
) -> dict:
    states = {r["id"]: r["status"] for r in results}
    counts = dict(Counter(states.values()))
    live = any(r["kind"].startswith("live_") for r in results)
    report = {
        "schema_version": 1,
        "generated_at": datetime.now(UTC).isoformat(),
        "scope": "explicitly selected live workflow"
        if live
        else "component and disposable fixtures only",
        "live_stack_operations": False,
        "full_live_acceptance": False,
        "interrupted": interrupted,
        "selected": selected,
        "summary": counts,
        "suites": results,
        "coverage": [
            {
                "check": title,
                "section": section,
                "automated_evidence": {
                    name: states.get(name, "not_selected") for name in suites
                },
                "live_status": "pending",
                "live_prerequisite": manual,
            }
            for title, section, suites, manual in COVERAGE
        ],
    }
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    lines = [
        "# Apps automated acceptance",
        "",
        report["generated_at"],
        "",
        f"{report['scope']}. **Full two-user human acceptance remains pending.**",
        "No Platform or ringfour-virtual stack operations are performed.",
        "",
        "| Suite | Evidence type | Result | Log / prerequisite |",
        "|---|---|---|---|",
    ]
    for result in results:
        detail = (
            f"[{result['log']}]({result['log']})"
            if result.get("log")
            else result.get("reason", "")
        )
        lines.append(
            f"| {result['id']} | {result['kind']} | {result['status']} | {detail} |"
        )
    lines += [
        "",
        "## Checklist coverage",
        "",
        "| Section / check | Automated evidence | Live follow-up (pending) |",
        "|---|---|---|",
    ]
    for item in report["coverage"]:
        evidence = ", ".join(
            f"{name}: {state}" for name, state in item["automated_evidence"].items()
        )
        lines.append(
            f"| {item['section']}. {item['check']} | {evidence} | {item['live_prerequisite']} |"
        )
    (output / "report.md").write_text("\n".join(lines) + "\n")
    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=PROFILES, default="quick")
    parser.add_argument(
        "--suite",
        action="append",
        choices=[s.id for s in SUITES],
        help="Run only this suite; repeat to select several",
    )
    parser.add_argument(
        "--list", action="store_true", help="Show suites without running anything"
    )
    parser.add_argument("--output", type=Path)
    parser.add_argument(
        "--timeout",
        type=float,
        default=1800,
        help="Seconds per suite; interruption requests disposable cleanup",
    )
    parser.add_argument(
        "--live-robot",
        help="Explicit UUID or unambiguous robot name; only used with the live profile/suite",
    )
    parser.add_argument("--live-org", help="Expected selected CLI organization UUID")
    parser.add_argument(
        "--live-repository",
        help="Registry repository prefix for private test images; a unique suffix is appended",
    )
    parser.add_argument(
        "--live-preflight",
        action="store_true",
        help="With the live profile, check the named robot without installing or starting anything",
    )
    args = parser.parse_args(argv)
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    selected = list(dict.fromkeys(args.suite or PROFILES[args.profile]))
    suites = [s for s in SUITES if s.id in selected]
    if args.list:
        for suite in suites:
            print(f"{suite.id:20} {suite.kind:20} {suite.title}")
        return 0
    if "live-apps" in selected:
        if not args.live_robot or not args.live_org:
            parser.error("Live workflows require --live-robot and --live-org")
        if not args.live_preflight and not args.live_repository:
            parser.error("Live workflows require --live-repository")
    output = (
        args.output
        or ROOT
        / "test-results/apps-acceptance"
        / (datetime.now(UTC).strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:6])
    ).resolve()
    if (output / "report.json").exists():
        parser.error(
            "Output already contains a report; choose a new directory to preserve evidence"
        )
    output.mkdir(parents=True, exist_ok=True)
    if "live-apps" in selected:
        live_args = (
            "--robot",
            args.live_robot,
            "--org",
            args.live_org,
            "--output",
            str(output / "live-workflow.json"),
        )
        if args.live_preflight:
            live_args += ("--preflight",)
        else:
            live_args += (
                "--repository",
                args.live_repository.rstrip("/") + "/run-" + uuid.uuid4().hex[:12],
            )
        suites = [
            Suite(
                s.id,
                "Live workflow preflight (read-only)"
                if args.live_preflight
                else s.title,
                s.command + live_args,
                s.cwd,
                "live_preflight" if args.live_preflight else s.kind,
                False if args.live_preflight else s.docker,
                s.agent,
            )
            if s.id == "live-apps"
            else s
            for s in suites
        ]
    results = []
    interrupted = False
    print(f"Report: {output / 'report.md'}", flush=True)
    try:
        for index, suite in enumerate(suites, 1):
            print(
                f"Running {suite.id} ({suite.kind})… [{index}/{len(suites)}]",
                flush=True,
            )
            write_report(
                output,
                results
                + [
                    {
                        "id": suite.id,
                        "title": suite.title,
                        "kind": suite.kind,
                        "status": "running",
                        "started_at": datetime.now(UTC).isoformat(),
                        "log": f"{suite.id}.log",
                    }
                ],
                selected,
            )
            result = run_suite(suite, output, args.timeout)
            results.append(result)
            write_report(output, results, selected)
            print(
                f"  {result['status']}: {result.get('reason') or suite.title}",
                flush=True,
            )
    except KeyboardInterrupt:
        interrupted = True
    completed = {r["id"] for r in results}
    for suite in suites:
        if suite.id not in completed:
            results.append(
                {
                    "id": suite.id,
                    "kind": suite.kind,
                    "status": "pending",
                    "reason": "Interrupted before completion",
                }
            )
    report = write_report(output, results, selected, interrupted)
    print(json.dumps(report["summary"]), flush=True)
    print(
        "Full two-user acceptance, operator-only live stack recovery and conditional gates remain pending.",
        flush=True,
    )
    if interrupted:
        return 130
    return (
        1
        if any(r["status"] == "failed" for r in results)
        else 2
        if any(r["status"] == "pending" for r in results)
        else 0
    )


if __name__ == "__main__":
    sys.exit(main())
