"""Check launcher wiring and process cleanup without touching a developer database."""

import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import unittest


ROOT = Path(__file__).resolve().parent.parent
STUB = """#!/usr/bin/env python3
import json, os, signal, sys
from pathlib import Path
name = Path(sys.argv[0]).name
args = sys.argv[1:]
mode = args[-1] if name == 'dotnet' and args[0].endswith('Nulysa.dll') else ''
def record(event):
    with open(os.environ['DEV_TEST_LOG'], 'a') as log:
        log.write(json.dumps(dict(event=event, name=name, args=args,
            environment=os.environ.get('DOTNET_ENVIRONMENT'),
            connection=os.environ.get('ConnectionStrings__Database'),
            keys=os.environ.get('DataProtection__Path'))) + '\\n')
record(mode or name)
if name == 'docker' and 'exec' in args and os.environ.get('DEV_TEST_DB_DOWN'):
    sys.exit(1)
if mode in ('web', 'worker'):
    if mode == 'web' and os.environ.get('DEV_TEST_CRASH'):
        sys.exit(17)
    def stop(*_):
        record('stopped-' + mode)
        sys.exit(0)
    signal.signal(signal.SIGTERM, stop)
    while True:
        signal.pause()
"""


class DevelopmentLauncherTests(unittest.TestCase):
    def setUp(self):
        Path("/tmp/opencode").mkdir(parents=True, exist_ok=True)
        self.directory = tempfile.TemporaryDirectory(prefix="nulysa-dev-", dir="/tmp/opencode")
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        for name in ("dotnet", "docker", "sleep"):
            command = self.path / name
            command.write_text("#!/bin/sh\nexit 0\n" if name == "sleep" else STUB)
            command.chmod(0o755)
        self.log = self.path / "commands.jsonl"
        self.env = {**os.environ, "PATH": f"{self.path}:{os.environ['PATH']}",
                    "CONTAINER_ENGINE": "docker", "DEV_TEST_LOG": str(self.log),
                    "ConnectionStrings__Database": "production-sentinel"}

    def rows(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []

    def test_shared_local_environment_setup_forwarding_and_shutdown(self):
        command = ["bash", str(ROOT / "scripts/dev.sh")]
        with subprocess.Popen(command, cwd=self.path, env=self.env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE) as process:
            try:
                deadline = time.monotonic() + 10
                while not {"web", "worker"} <= {r["event"] for r in self.rows()}:
                    self.assertIsNone(process.poll())
                    self.assertLess(time.monotonic(), deadline)
                    time.sleep(0.05)
                process.send_signal(signal.SIGTERM)
                process.communicate(timeout=10)
                self.assertEqual(143, process.returncode)
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.communicate(timeout=10)
        events = [r["event"] for r in self.rows()]
        self.assertLess(events.index("migrate"), events.index("install-schedules"))
        self.assertLess(events.index("install-schedules"), events.index("web"))
        self.assertTrue({"stopped-web", "stopped-worker"} <= set(events))
        for row in self.rows():
            self.assertEqual("Development", row["environment"])
            self.assertIn("Host=127.0.0.1;Port=55432;Database=nulysa;", row["connection"])
            self.assertEqual(str(ROOT / ".local/data-protection"), row["keys"])
        result = subprocess.run(command + ["status"], cwd=self.path, env=self.env, capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("status", self.rows()[-1]["event"])

        previous = len(self.rows())
        with subprocess.Popen(command, cwd=self.path, env=self.env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE) as process:
            try:
                deadline = time.monotonic() + 10
                while not {"web", "worker"} <= {r["event"] for r in self.rows()[previous:]}:
                    self.assertIsNone(process.poll())
                    self.assertLess(time.monotonic(), deadline)
                    time.sleep(0.05)
                process.send_signal(signal.SIGINT)
                process.communicate(timeout=10)
                self.assertEqual(130, process.returncode)
                self.assertTrue({"stopped-web", "stopped-worker"} <= {r["event"] for r in self.rows()[previous:]})
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.communicate(timeout=10)

    def test_child_failure_and_database_readiness_failure_are_not_hidden(self):
        for setting, expected in (("DEV_TEST_CRASH", 17), ("DEV_TEST_DB_DOWN", 1)):
            with self.subTest(setting=setting):
                self.log.unlink(missing_ok=True)
                result = subprocess.run(["bash", str(ROOT / "scripts/dev.sh")], cwd=self.path,
                                        env={**self.env, setting: "1"}, capture_output=True, timeout=10)
                self.assertEqual(expected, result.returncode, result.stderr)
                if setting == "DEV_TEST_DB_DOWN":
                    self.assertFalse(any(row["name"] == "dotnet" for row in self.rows()))


if __name__ == "__main__":
    unittest.main()
