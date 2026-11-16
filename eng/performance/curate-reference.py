#!/usr/bin/env python3
"""Curate the completed 100-page reference campaign into a public numeric record.

This deliberately excludes logs, arguments, absolute paths and generated files.
Raw local evidence is never modified. Missing/failed inputs fail curation.
"""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath, PureWindowsPath
import statistics


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def manifest(root, excluded=()):
    return {path.relative_to(root).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(root.rglob("*")) if path.is_file() and path.name not in excluded}


def sanitize(value):
    if isinstance(value, dict):
        return {key: sanitize(item) for key, item in value.items()
                if key.lower() not in {"arguments", "rootdirectory", "processid", "options", "serverinitializeresult"}}
    if isinstance(value, list):
        return [sanitize(item) for item in value]
    return value


def assert_public(value):
    if isinstance(value, dict):
        for key, item in value.items():
            assert_public(key)
            assert_public(item)
    elif isinstance(value, list):
        for item in value:
            assert_public(item)
    elif isinstance(value, str):
        if (PureWindowsPath(value).is_absolute() or PurePosixPath(value).is_absolute()
                or re.search(r"(?<![A-Za-z0-9])[A-Za-z]:[\\/]|\\\\|file://", value, re.IGNORECASE)
                or re.search(r"(?:^|[\s\"'(])/(?!/)[^\s]", value)
                or "h_tky" in value):
            raise ValueError("A local/private path remains in the public projection")


def validate_committed_campaign(campaign, commit):
    candidates = [target for target in campaign["targets"] if target["role"] == "candidate"]
    if not candidates or any(target["commit"] != commit or target["changes"] for target in candidates):
        raise ValueError("Committed candidate does not match the recorded clean working tree")




def validate_generator_pairs(observations):
    expected = {(label, round_number) for label in ("baseline", "candidate") for round_number in (1, 2, 3)}
    if len(observations) != 6 or {(item["label"], item["round"]) for item in observations} != expected:
        raise ValueError("Expected three process pairs for Generator header scanning")


def validate_binary_commit(identity, commit):
    if not identity.get("informationalVersion", "").endswith("+" + commit):
        raise ValueError("Loaded binary commit does not match its measurement target")


def validate_measurement_commits(report, commit):
    baseline = next(target["commit"] for target in report["blog"]["targets"] if target["role"] == "baseline")
    for section in ("blog", "typedDocsQuality"):
        targets = {target["label"]: target["commit"] for target in report[section]["targets"]}
        for observation in report[section]["observations"]:
            raw = observation["raw"]
            expected = targets[observation["label"]]
            if raw["Provenance"]["Commit"] != expected:
                raise ValueError("Site observation commit does not match its measurement target")
            for component in ("product", "harness"):
                validate_binary_commit(raw["Identity"][component], expected)
    for observation in report["generator"]:
        expected = commit if observation["label"] == "candidate" else baseline
        raw = observation["raw"]
        if raw["provenance"]["Commit"] != expected:
            raise ValueError("Generator observation commit does not match its measurement target")
        for component in ("product", "harness"):
            validate_binary_commit(raw["identity"][component], expected)
        validate_binary_commit(raw["generator"], expected)
    for observation in report["mdx"]:
        expected = commit if observation["label"] == "candidate" else baseline
        for component in ("product", "harness"):
            validate_binary_commit(observation["raw"]["identity"][component], expected)
    if report["editor"]["latency"]["identity"]["sourceCommit"] != commit:
        raise ValueError("Editor observation commit does not match its measurement target")


def site_campaign(root):
    campaign = read(root / "campaign.json")
    if campaign["status"] != "completed":
        raise ValueError(f"Campaign is not completed: {root.name}")
    targets = [{key: item[key] for key in ("label", "role", "commit", "productSources", "harnessSources", "harnessSha256", "dependencyLockSha256")}
               for item in campaign["targets"]]
    for target, original in zip(targets, campaign["targets"]):
        target["featureScope"] = original.get("featureScope", "not-recorded")
    for target in targets:
        target["productSourceManifestSha256"] = digest(target["productSources"])
    observations = []
    for run in campaign["runs"]:
        if run["status"] != "completed":
            raise ValueError("Incomplete run")
        directory = root / run["label"] / f"round-{run['round']}" / "runs/markdown-100-all-run-1"
        result = read(directory / "result.json")
        inputs = manifest(directory / "corpus-100")
        outputs = manifest(directory / "site-100", (".lithosharp-output-manifest.json",))
        observations.append({"label": run["label"], "round": run["round"], "status": "completed",
                             "raw": sanitize(result), "rawFileSha256": hashlib.sha256((directory / "result.json").read_bytes()).hexdigest(),
                             "finalInputFiles": inputs, "finalOutputFiles": outputs})
    aggregates = []
    scenario_names = tuple(work["Name"] for work in observations[0]["raw"]["Workloads"])
    if not {"clean", "no-op", "single-page-change", "layout-change"}.issubset(scenario_names) or len(set(scenario_names)) != len(scenario_names) or any(
        tuple(work["Name"] for work in item["raw"]["Workloads"]) != scenario_names for item in observations
    ):
        raise ValueError("Workload scenarios differ between compared observations")
    for label in sorted({item["label"] for item in observations}):
        for name in scenario_names:
            values = [next(work["ElapsedMilliseconds"] for work in item["raw"]["Workloads"] if work["Name"] == name)
                      for item in observations if item["label"] == label]
            aggregates.append({"label": label, "scenario": name, "unit": "ms", "raw": values,
                               "median": statistics.median(values), "min": min(values), "max": max(values)})
    first = observations[0]
    return {"profile": campaign["configuration"]["profile"], "pages": 100, "rounds": campaign["configuration"]["rounds"],
            "comparisonKind": campaign.get("comparisonKind", "not-recorded"), "automaticSpeedupOrVerdict": "not-calculated",
            "targets": targets, "observations": observations, "aggregates": aggregates,
            "finalInputsEqual": all(item["finalInputFiles"] == first["finalInputFiles"] for item in observations),
            "finalOutputsEqual": all(item["finalOutputFiles"] == first["finalOutputFiles"] for item in observations),
            "hashScope": "Final mutated corpus/output after layout-change, not per-stage snapshots. Output manifest excluded only."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--candidate-commit", help="Full SHA when curating a clean committed product; omitted for historical uncommitted captures.")
    args = parser.parse_args()
    root = args.root
    if args.candidate_commit and not re.fullmatch(r"[0-9a-f]{40}", args.candidate_commit):
        parser.error("--candidate-commit must be a full lowercase commit SHA")
    notes = read(root / "run-notes.json") if (root / "run-notes.json").exists() else {}
    first_ledger = root / "blog-fixed/released-2.1.3/round-1/ledger.jsonl"
    measured_date = json.loads(first_ledger.read_text().splitlines()[0])["startedAt"][:10]
    report = {"schemaVersion": 1, "measuredDateUtc": measured_date, "status": "completed",
              "scope": (f"One Windows source-build reference for committed candidate {args.candidate_commit}, not a release acceptance or speed guarantee."
                        if args.candidate_commit else "One Windows source-build reference, not a release acceptance or speed guarantee. Candidate source manifest and binary hashes identify an uncommitted 2.2.0 snapshot."),
              "blog": site_campaign(root / "blog-fixed"), "typedDocsQuality": site_campaign(root / "typed-docs-fixed"),
              "generator": [], "mdx": [], "editor": {}}
    if args.candidate_commit:
        for name in ("blog-fixed", "typed-docs-fixed"):
            campaign = read(root / name / "campaign.json")
            validate_committed_campaign(campaign, args.candidate_commit)
        report["candidateCommit"] = args.candidate_commit
        report["candidateWorkingTreeClean"] = True
    for path in sorted(root.glob("generator-*.json")):
        raw = read(path)
        if raw.get("status") != "completed":
            raise ValueError("Incomplete Generator run")
        report["generator"].append({"label": path.stem.split("-")[1], "round": int(path.stem.split("-")[-1]), "raw": sanitize(raw)})
    validate_generator_pairs(report["generator"])
    for label in ("baseline", "candidate"):
        run = root / "mdx" / label / "runs/mdx-100-all-run-1/harness"
        rows = [json.loads(line) for line in (run.parents[2] / "ledger.jsonl").read_text().splitlines()]
        if len(rows) != 10 or any(item["status"] != "completed" for item in rows):
            raise ValueError("Incomplete MDX scenario set")
        report["mdx"].append({"label": label, "raw": sanitize(read(run / "measurements.json")),
                              "finalInputFiles": manifest(run / "content"),
                              "finalOutputFiles": manifest(run / "site", (".lithosharp-output-manifest.json",))})
    editor_raw = read(root / "editor/editor-latency.json")
    report["editor"] = {"method": {key: editor_raw["options"][key] for key in ("samples", "warmup", "soak", "gap", "timeout", "languages")},
                        "latency": sanitize(editor_raw), "soak": sanitize(read(root / "editor/editor-soak.json"))}
    report["editor"]["method"].update(documentBytes=50 * 1024, debounceMilliseconds=50, latencyUnit="ms")
    report["editor"]["executionOutcome"] = sanitize(notes.get("editorExecutionOutcome", {"status": "unavailable"}))
    if report["editor"]["latency"]["status"] != "completed" or report["editor"]["soak"]["status"] != "completed":
        raise ValueError("Incomplete editor run")
    if any(item["status"] != "exited" for item in report["editor"]["soak"]["serverProcesses"]):
        raise ValueError("Editor owned-process cleanup is not confirmed")
    if args.candidate_commit:
        validate_measurement_commits(report, args.candidate_commit)
    report["excludedAttempts"] = sanitize(notes.get("excludedAttempts", []))
    report["limitations"] = ["Only 100 pages, one Windows computer; no 1000/10000-page or cross-OS reference.",
        "Three Blog/Docs process runs per target; overlapping ranges do not establish small performance changes.",
        "One MDX process per target, no MDX ratio conclusion.",
        "Generator header scan excludes schema binding, emission, Roslyn and C# compilation.",
        "Editor has 20 edits per shape/language and a 40-edit restart soak, not a long-duration soak or Extension Host benchmark.",
        "Verification stages overlap execution. Worker heap snapshots are not peak RSS. Unmeasured worker RSS and editor loaded-dependency identity remain unavailable."]
    if "startedAtUtc" in notes and "completedAtUtc" in notes:
        report["measurementWindowUtc"] = {key: notes[key] for key in ("startedAtUtc", "completedAtUtc", "elapsedSeconds")}
    if (root / "machine.json").exists():
        machine = read(root / "machine.json")
        report["machine"] = {key: machine[key] for key in ("dotnetSdk", "node", "os", "cpu", "logicalProcessors", "conditions")}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    # Fail closed for filesystem paths on any host, including paths embedded in prose.
    assert_public(report)
    args.output.write_text(text, encoding="utf-8")
    for section in ("blog", "typedDocsQuality"):
        print(section, json.dumps(report[section]["aggregates"]))


if __name__ == "__main__":
    main()
