import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location(
    "apps_acceptance", ROOT / "scripts/apps-acceptance.py"
)
runner = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = runner
spec.loader.exec_module(runner)


class AcceptanceRunnerTests(unittest.TestCase):
    def test_default_and_full_profiles_never_select_live_mutations(self):
        self.assertNotIn("live-apps", runner.PROFILES["quick"])
        self.assertNotIn("live-apps", runner.PROFILES["full"])
        self.assertEqual(["live-apps"], runner.PROFILES["live"])

    def test_result_records_actual_process_exit_and_log(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            suite = runner.Suite(
                "failing",
                "failure",
                (
                    sys.executable,
                    "-c",
                    "print('failure evidence'); raise SystemExit(9)",
                ),
            )
            result = runner.run_suite(suite, output, timeout=5)
            self.assertEqual("failed", result["status"])
            self.assertEqual(9, result["returncode"])
            self.assertIn("failure evidence", (output / result["log"]).read_text())

    def test_child_output_is_relayed_before_exit_and_credentials_are_redacted(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            release, finished = output / "release", output / "finished"
            script = (
                "import time; from pathlib import Path\n"
                f"release=Path({str(release)!r}); finished=Path({str(finished)!r})\n"
                "print('live marker Bearer abc.def.ghi', flush=True)\n"
                "deadline=time.monotonic()+5\n"
                "while not release.exists() and time.monotonic()<deadline: time.sleep(.02)\n"
                "assert release.exists(), 'Output was not relayed before exit'\n"
                "finished.touch()\n"
            )
            suite = runner.Suite(
                "streaming", "streaming", (sys.executable, "-u", "-c", script)
            )
            messages = []

            def capture(message, **kwargs):
                messages.append(message)
                if "] live marker " in message:
                    self.assertFalse(finished.exists())
                    release.touch()

            with patch("builtins.print", side_effect=capture):
                result = runner.run_suite(suite, output, timeout=8)
            self.assertEqual(result["status"], "passed")
            self.assertTrue(finished.exists())
            self.assertTrue(
                any("live marker Bearer [redacted]" in line for line in messages)
            )
            self.assertTrue(all("abc.def.ghi" not in line for line in messages))
            self.assertTrue(result["started_at"] <= result["finished_at"])
            self.assertTrue(any("Command:" in line for line in messages))
            self.assertTrue(
                any("PASS" in line and "exit 0" in line for line in messages)
            )

    def test_missing_prerequisite_is_pending_and_does_not_launch(self):
        with (
            tempfile.TemporaryDirectory() as directory,
            patch.object(runner, "prerequisite", return_value="Docker unavailable"),
            patch.object(runner.subprocess, "Popen") as launch,
        ):
            result = runner.run_suite(runner.SUITES[0], Path(directory), 5)
            self.assertEqual("pending", result["status"])
            launch.assert_not_called()

    def test_report_distinguishes_unselected_evidence_and_live_gates(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            result = {
                "id": "console",
                "kind": "component",
                "status": "passed",
                "log": "console.log",
            }
            runner.write_report(output, [result], ["console"])
            report = json.loads((output / "report.json").read_text())
            self.assertFalse(report["full_live_acceptance"])
            self.assertFalse(report["live_stack_operations"])
            self.assertTrue(
                all(c["live_status"] == "pending" for c in report["coverage"])
            )
            self.assertEqual(
                "not_selected", report["coverage"][0]["automated_evidence"]["api"]
            )
            self.assertIn("console.log", (output / "report.md").read_text())

    def test_live_requires_explicit_target_and_registry_before_any_process(self):
        with patch.object(runner, "run_suite") as run:
            with self.assertRaises(SystemExit) as error:
                runner.main(["--profile", "live"])
            self.assertEqual(2, error.exception.code)
            run.assert_not_called()

    def test_timeout_is_a_failure_and_requests_child_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            suite = runner.Suite(
                "slow", "slow", (sys.executable, "-c", "import time; time.sleep(60)")
            )
            result = runner.run_suite(suite, Path(directory), timeout=0.1)
            self.assertEqual("failed", result["status"])
            self.assertIn("Timed out", result["reason"])


if __name__ == "__main__":
    unittest.main()
