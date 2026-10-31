#!/usr/bin/env python3
"""Compare a small deterministic site on one runner; not a speed ranking."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import sys
import tempfile
import time

SDK = "10.0.403"
RUNTIME = "10.0.14"
PAGES = 100
HARNESS = Path("benchmarks/LithoSharp.Performance")
PROJECT = HARNESS / "LithoSharp.Performance.csproj"
ORDER = ("baseline", "candidate", "candidate", "baseline")


def capture(args, cwd):
    return subprocess.check_output(args, cwd=cwd, text=True, timeout=60).strip()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def files_manifest(root, exclude=()):
    return {p.relative_to(root).as_posix(): sha(p)
            for p in sorted(root.rglob("*"))
            if p.is_file() and p.name not in exclude}


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def run(args, cwd, log, limit):
    started = time.monotonic()
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.run(args, cwd=cwd, stdout=stream,
                                 stderr=subprocess.STDOUT, timeout=limit)
    if process.returncode:
        raise RuntimeError(f"Command exited {process.returncode}; see {log.name}")
    return time.monotonic() - started


def resolve_baseline(repo, candidate, supplied):
    if supplied and supplied != "0" * 40:
        if not re.fullmatch(r"[0-9a-fA-F]{40}", supplied):
            raise ValueError("Baseline must be a full 40-character commit SHA")
        # A force-push's previous head may not be reachable from fetched branches.
        if subprocess.run(["git", "cat-file", "-e", supplied + "^{commit}"],
                          cwd=repo, stdout=subprocess.DEVNULL,
                          stderr=subprocess.DEVNULL).returncode:
            subprocess.run(["git", "fetch", "--no-tags", "origin", supplied],
                           cwd=repo, check=True, timeout=120)
        return capture(["git", "rev-parse", supplied + "^{commit}"], repo), "requested-or-push-before"
    return capture(["git", "rev-parse", candidate + "^"], repo), "first-parent (new branch or manual default)"


def summarize(report):
    lines = ["# Site generation comparison", "",
             "Approximate trends on one hosted runner, not a precise ranking.",
             "The ordinary build and unit tests ran before this job.",
             "Generation time excludes restore, compilation and corpus parsing.",
             "Fresh processes do not imply a cold OS filesystem cache.", "",
             f"Status: **{report['status']}**",
             f"Candidate: `{report.get('candidate', 'unknown')}`",
             f"Baseline: `{report.get('baseline', 'unresolved')}`",
             f"Baseline selection: {report.get('baselineSelection', 'unresolved')}",
             f"Common harness: `{report.get('candidate', 'unknown')}`",
             f"SDK {SDK}; runtime {RUNTIME}; {PAGES} Blog pages; order A/B/B/A.", ""]
    if report.get("error"):
        lines += ["Failure: " + report["error"], "No failed observation was replaced or retried.", ""]
    if report.get("comparison"):
        lines += ["| Workload | Baseline median ms (range) | Candidate median ms (range) | Difference ms |",
                  "|---|---:|---:|---:|"]
        for item in report["comparison"]:
            a, b = item["baseline"], item["candidate"]
            lines.append(f"| {item['workload']} | {statistics.median(a):.2f} ({min(a):.2f}–{max(a):.2f}) | "
                         f"{statistics.median(b):.2f} ({min(b):.2f}–{max(b):.2f}) | "
                         f"{statistics.median(b)-statistics.median(a):+.2f} |")
        lines += ["", "Only two observations per revision: small differences are inconclusive.",
                  "Timing differences never fail this job.",
                  f"Final-state output bytes equal across all observations: **{report['finalStateOutputBytesEqual']}**.",
                  "Hashes cover only the final mutated corpus and output after layout-change, not each workload.",
                  "Output differences are reported for review, not automatically classified as defects.",
                  "The harness separately checks file counts and incremental invalidation invariants.",
                  "Only the internal .lithosharp-output-manifest.json is excluded from byte comparison."]
    return "\n".join(lines) + "\n"


def main():
    repo = Path(__file__).resolve().parents[1]
    output = repo / "artifacts/site-generation-comparison"
    output.mkdir(parents=True, exist_ok=False)
    report = {"status": "incomplete", "sdk": SDK, "runtime": RUNTIME,
              "pages": PAGES, "observations": [], "setup": {}}
    try:
        candidate = capture(["git", "rev-parse", "HEAD"], repo)
        report["candidate"] = candidate
        baseline, reason = resolve_baseline(repo, candidate, os.environ.get("BASELINE_SHA", ""))
        report.update(baseline=baseline, baselineSelection=reason)
        report["harnessFiles"] = files_manifest(repo / HARNESS)
        # Preserve the common input code independently from both measured revisions.
        common = {p.name: p.read_bytes() for p in (repo / HARNESS).iterdir()
                  if p.is_file() and (p.suffix in (".cs", ".csproj") or p.name == "packages.lock.json")}
        report["runner"] = {key: os.environ.get(key) for key in
                            ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")}
        (output / "dotnet-info.txt").write_text(capture(["dotnet", "--info"], repo) + "\n")
        (output / "cpu.txt").write_text(capture(["lscpu"], repo) + "\n")
        work = Path(tempfile.mkdtemp(prefix="lithosharp-compare-", dir=os.environ.get("RUNNER_TEMP")))
        trees = {}
        for label, commit in (("baseline", baseline), ("candidate", candidate)):
            tree = work / label
            subprocess.run(["git", "worktree", "add", "--detach", str(tree), commit], cwd=repo, check=True, timeout=120)
            trees[label] = tree
            harness = tree / HARNESS
            if not (tree / "src/LithoSharp/LithoSharp.csproj").is_file():
                raise RuntimeError(f"{label} does not contain the compatible LithoSharp project")
            # A common harness prevents changed measurement code from skewing the pair.
            if harness.exists():
                shutil.rmtree(harness)
            harness.mkdir(parents=True)
            for name, data in common.items():
                (harness / name).write_bytes(data)
            global_path = tree / "global.json"
            config = json.loads(global_path.read_text()) if global_path.exists() else {}
            config["sdk"] = {"version": SDK, "rollForward": "disable"}
            write_json(global_path, config)
            if capture(["dotnet", "--version"], tree) != SDK:
                raise RuntimeError("Unexpected SDK selection")
            restore = run(["dotnet", "restore", str(PROJECT), "--locked-mode"], tree,
                          output / f"{label}-restore.log", 300)
            build = run(["dotnet", "build", str(PROJECT), "--no-restore", "--configuration", "Release"], tree,
                        output / f"{label}-build.log", 300)
            dll = tree / HARNESS / "bin/Release/net10.0/LithoSharp.Performance.dll"
            report["setup"][label] = {"restoreSeconds": restore, "buildSeconds": build,
                                      "assemblies": files_manifest(dll.parent)}
            write_json(output / "comparison.json", report)
        for index, label in enumerate(ORDER, 1):
            tree = trees[label]
            run_dir = output / f"{index:02d}-{label}"
            run_dir.mkdir()
            result = run_dir / "result.json"
            observation = {"order": index, "revision": label, "status": "started"}
            report["observations"].append(observation)
            write_json(output / "comparison.json", report)
            dll = tree / HARNESS / "bin/Release/net10.0/LithoSharp.Performance.dll"
            run(["dotnet", "exec", "--fx-version", RUNTIME, str(dll), "--size", str(PAGES),
                 "--mode", "site", "--smoke", "--output", str(result)], tree, run_dir / "run.log", 180)
            data = json.loads(result.read_text())
            if data["Environment"]["Runtime"] != RUNTIME:
                raise RuntimeError("Actual benchmark runtime did not match the pin")
            selected_commit = baseline if label == "baseline" else candidate
            for section in ("Environment", "Provenance"):
                if data[section]["Commit"] != selected_commit:
                    raise RuntimeError(f"{section} commit did not match the selected revision")
                if data[section]["SdkVersion"] != SDK:
                    raise RuntimeError(f"{section} SDK did not match the pin")
            if data["Corpus"]["PageCount"] != PAGES:
                raise RuntimeError("Actual corpus page count did not match the requested size")
            if {x["Name"] for x in data["Workloads"]} != {"clean", "no-op", "single-page-change", "layout-change"}:
                raise RuntimeError("Expected site workloads missing")
            # The harness mutates the corpus and site in place; these are final-state hashes.
            inputs = files_manifest(run_dir / f"corpus-{PAGES}")
            generated = files_manifest(run_dir / f"site-{PAGES}", (".lithosharp-output-manifest.json",))
            if not inputs or not generated:
                raise RuntimeError("Input or generated output is empty")
            write_json(run_dir / "final-input-files.json", inputs)
            write_json(run_dir / "final-output-files.json", generated)
            observation.update(status="completed", workloads=data["Workloads"], finalInputFiles=inputs,
                               finalOutputFiles=generated, provenance=data["Provenance"])
            write_json(output / "comparison.json", report)
        first = report["observations"][0]
        if any(x["finalInputFiles"] != first["finalInputFiles"] for x in report["observations"]):
            raise RuntimeError("The paired observations did not use the same inputs")
        report["finalStateOutputBytesEqual"] = all(x["finalOutputFiles"] == first["finalOutputFiles"] for x in report["observations"])
        report["comparison"] = []
        for name in ("clean", "no-op", "single-page-change", "layout-change"):
            row = {"workload": name}
            for label in ("baseline", "candidate"):
                row[label] = [next(w["ElapsedMilliseconds"] for w in o["workloads"] if w["Name"] == name)
                              for o in report["observations"] if o["revision"] == label]
            report["comparison"].append(row)
        report["status"] = "completed"
    except Exception as error:
        report.update(status="failed", error=str(error))
        if report["observations"] and report["observations"][-1]["status"] == "started":
            report["observations"][-1]["status"] = "failed"
        raise
    finally:
        write_json(output / "comparison.json", report)
        summary = summarize(report)
        (output / "summary.md").write_text(summary, encoding="utf-8")
        if os.environ.get("GITHUB_STEP_SUMMARY"):
            with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
                stream.write(summary)


if __name__ == "__main__":
    main()
