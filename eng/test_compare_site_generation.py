"""Pure-Python runner regression tests; no dotnet, Git, Node or measurements."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "site_comparison", Path(__file__).with_name("compare-site-generation.py"))
runner = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runner)


class RunnerContractTests(unittest.TestCase):
    def test_overlay_preserves_each_arms_exact_lock_and_common_sources(self):
        common = {"Program.cs": b"common source", "LithoSharp.Performance.csproj": b"common project"}
        with tempfile.TemporaryDirectory() as temporary:
            for arm, version in (("baseline", "1.2.0"), ("candidate", "2.0.0-preview")):
                harness = Path(temporary) / arm
                harness.mkdir()
                original = json.dumps({"version": 1, "dependencies": {
                    "net10.0": {"Syntamark": {"resolved": version}}}}).encode()
                (harness / "packages.lock.json").write_bytes(original)
                (harness / "stale.cs").write_text("old source")
                lock = runner.overlay_harness(harness, common)
                self.assertEqual(lock.read_bytes(), original)
                for name, data in common.items():
                    self.assertEqual((harness / name).read_bytes(), data)
                self.assertFalse((harness / "stale.cs").exists())
                runner.verify_lock(lock, runner.sha(lock))

    def test_missing_lock_fails_before_overlay(self):
        with tempfile.TemporaryDirectory() as temporary:
            harness = Path(temporary)
            sentinel = harness / "Program.cs"
            sentinel.write_text("original")
            with self.assertRaisesRegex(RuntimeError, "own committed"):
                runner.overlay_harness(harness, {})
            self.assertEqual(sentinel.read_text(), "original")

    def test_invalid_lock_and_common_lock_fail_before_overlay(self):
        with tempfile.TemporaryDirectory() as temporary:
            harness = Path(temporary)
            lock = harness / "packages.lock.json"
            for contents, common in ((b'{}', {}),
                    (b'{"version":1,"dependencies":{"net10.0":{}}}',
                     {"packages.lock.json": b"wrong arm"})):
                lock.write_bytes(contents)
                with self.assertRaises(RuntimeError):
                    runner.overlay_harness(harness, common)
                self.assertEqual(lock.read_bytes(), contents)

    def test_changed_or_missing_lock_is_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            lock = Path(temporary) / "packages.lock.json"
            lock.write_text("original")
            expected = runner.sha(lock)
            lock.write_text("changed")
            with self.assertRaisesRegex(RuntimeError, "lock changed"):
                runner.verify_lock(lock, expected)
            lock.unlink()
            with self.assertRaisesRegex(RuntimeError, "lock changed"):
                runner.verify_lock(lock, expected)

    def test_exact_five_workloads_include_warm_cache(self):
        rows = [{"Name": name} for name in runner.WORKLOADS]
        runner.validate_workloads(rows)
        runner.validate_workloads(list(reversed(rows)))
        self.assertIn("warm-cache", runner.WORKLOADS)
        for invalid in (rows[:1] + rows[2:], rows + [rows[0]], rows[:-1] + [rows[0]],
                        rows + [{"Name": "unexpected"}]):
            with self.assertRaisesRegex(RuntimeError, "exactly one"):
                runner.validate_workloads(invalid)

    def test_default_and_zero_baseline_resolve_to_release_220(self):
        with patch.object(runner, "capture", return_value=runner.RELEASE_BASELINE) as capture:
            for supplied in ("", "0" * 40):
                sha, reason = runner.resolve_baseline(Path("unused"), "candidate", supplied)
                self.assertEqual(sha, "b6e315db13a5b07ddd77696c9c0a5eefe6e9341f")
                self.assertEqual(reason, "released-2.2.0")
                self.assertEqual(capture.call_args.args[0][-1], sha + "^{commit}")

    def test_main_preserves_per_arm_locks_and_all_five_samples(self):
        # All external commands are replaced with deterministic tiny file fixtures.
        with tempfile.TemporaryDirectory() as temporary:
            repo = Path(temporary) / "repo"
            harness = repo / runner.HARNESS
            harness.mkdir(parents=True)
            (harness / "Program.cs").write_text("common")
            (harness / "LithoSharp.Performance.csproj").write_text("common")
            (repo / "eng/performance").mkdir(parents=True)
            work = Path(temporary) / "work"
            work.mkdir()
            output = Path(temporary) / "results"
            candidate = "c" * 40
            commands = []

            def capture(args, cwd):
                if args[0] == "git":
                    return candidate if args[-1] == "HEAD" else runner.RELEASE_BASELINE
                return runner.SDK if args[-1] == "--version" else "fixture environment"

            def checkout(args, **kwargs):
                self.assertEqual(args[:3], ["git", "worktree", "add"])
                tree = Path(args[-2])
                target = tree / runner.HARNESS
                target.mkdir(parents=True)
                (target / "packages.lock.json").write_text(json.dumps({"version": 1,
                    "dependencies": {"net10.0": {"arm": tree.name}}}))
                product = tree / "src/LithoSharp/LithoSharp.csproj"
                product.parent.mkdir(parents=True)
                product.write_text("unchanged " + tree.name)

            def command(args, tree, log, limit):
                commands.append(args)
                if args[1] == "restore":
                    self.assertIn("--locked-mode", args)
                if args[1] == "build":
                    binary = tree / runner.HARNESS / "bin/Release/net10.0/LithoSharp.Performance.dll"
                    binary.parent.mkdir(parents=True)
                    binary.write_bytes(b"fixture")
                if args[1] == "exec":
                    result = Path(args[-1])
                    identity = {"Runtime": runner.RUNTIME, "SdkVersion": runner.SDK,
                                "Commit": runner.RELEASE_BASELINE if tree.name == "baseline" else candidate}
                    runner.write_json(result, {"Environment": identity, "Provenance": identity,
                        "Corpus": {"PageCount": 100}, "Workloads": [
                            {"Name": name, "ElapsedMilliseconds": 1} for name in runner.WORKLOADS]})
                    for directory in ("corpus-100", "site-100"):
                        folder = result.parent / directory
                        folder.mkdir()
                        (folder / "page.html").write_text("same")
                    if tree.name == "candidate":
                        (result.parent / "site-100/404.html").write_text("not found")
                return 0.01

            with patch.object(runner, "__file__", str(repo / "eng/compare-site-generation.py")), \
                 patch.object(runner.sys, "argv", ["runner", "--output", str(output)]), \
                 patch.dict(runner.os.environ, {"BASELINE_SHA": "", "GITHUB_STEP_SUMMARY": ""}), \
                 patch.object(runner, "capture", side_effect=capture), \
                 patch.object(runner.subprocess, "run", side_effect=checkout), \
                 patch.object(runner, "run", side_effect=command), \
                 patch.object(runner.tempfile, "mkdtemp", return_value=str(work)):
                runner.main()
            report = json.loads((output / "comparison.json").read_text())
            self.assertEqual(report["status"], "completed")
            self.assertEqual(report["acceptance"], "not-evaluated")
            self.assertEqual(report["comparisonKind"], "feature-scope-difference")
            self.assertEqual(len(report["comparison"]), 5)
            self.assertFalse(report["finalStateOutputBytesEqual"])
            self.assertEqual(len(commands), 8)
            for arm in ("baseline", "candidate"):
                setup = report["setup"][arm]
                self.assertTrue(setup["lockVerifiedAfterBuild"])
                self.assertEqual(setup["lockSha256"], runner.sha(output / setup["lockEvidence"]))
                self.assertEqual(json.loads((output / setup["lockEvidence"]).read_text())[
                    "dependencies"]["net10.0"]["arm"], arm)
                self.assertEqual((work / arm / "src/LithoSharp/LithoSharp.csproj").read_text(),
                                 "unchanged " + arm)
    def test_summary_discloses_feature_scope_and_incomplete_acceptance(self):
        report = {"status": "failed", "sdk": runner.SDK, "runtime": runner.RUNTIME,
                  "pages": 100, "error": "fixture failure"}
        summary = runner.summarize(report)
        self.assertIn("not equal-feature speed or acceptance claims", summary)
        self.assertIn("404.html", summary)
        self.assertIn("fixture failure", summary)
        self.assertNotIn("Status: **completed**", summary)


if __name__ == "__main__":
    unittest.main()
