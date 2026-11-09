#!/usr/bin/env python3
"""Curate the completed 100-page reference campaign into a public numeric record.

This deliberately excludes logs, arguments, absolute paths and generated files.
Raw local evidence is never modified. Missing/failed inputs fail curation.
"""
import argparse
import hashlib
import json
from pathlib import Path
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


def site_campaign(root):
    campaign = read(root / "campaign.json")
    if campaign["status"] != "completed":
        raise ValueError(f"Campaign is not completed: {root.name}")
    targets = [{key: item[key] for key in ("label", "role", "commit", "productSources", "harnessSources", "harnessSha256", "dependencyLockSha256")}
               for item in campaign["targets"]]
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
    for label in sorted({item["label"] for item in observations}):
        for name in ("clean", "no-op", "single-page-change", "layout-change"):
            values = [next(work["ElapsedMilliseconds"] for work in item["raw"]["Workloads"] if work["Name"] == name)
                      for item in observations if item["label"] == label]
            aggregates.append({"label": label, "scenario": name, "unit": "ms", "raw": values,
                               "median": statistics.median(values), "min": min(values), "max": max(values)})
    first = observations[0]
    return {"profile": campaign["configuration"]["profile"], "pages": 100, "rounds": campaign["configuration"]["rounds"],
            "targets": targets, "observations": observations, "aggregates": aggregates,
            "finalInputsEqual": all(item["finalInputFiles"] == first["finalInputFiles"] for item in observations),
            "finalOutputsEqual": all(item["finalOutputFiles"] == first["finalOutputFiles"] for item in observations),
            "hashScope": "Final mutated corpus/output after layout-change, not per-stage snapshots. Output manifest excluded only."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    root = args.root
    notes = read(root / "run-notes.json") if (root / "run-notes.json").exists() else {}
    first_ledger = root / "blog-fixed/released-2.1.3/round-1/ledger.jsonl"
    measured_date = json.loads(first_ledger.read_text().splitlines()[0])["startedAt"][:10]
    report = {"schemaVersion": 1, "measuredDateUtc": measured_date, "status": "completed",
              "scope": "One Windows source-build reference, not a release acceptance or speed guarantee. Candidate source manifest and binary hashes identify an uncommitted 2.2.0 snapshot.",
              "blog": site_campaign(root / "blog-fixed"), "typedDocsQuality": site_campaign(root / "typed-docs-fixed"),
              "generator": [], "mdx": [], "editor": {}}
    for path in sorted(root.glob("generator-*.json")):
        raw = read(path)
        if raw.get("status") != "completed":
            raise ValueError("Incomplete Generator run")
        report["generator"].append({"label": path.stem.split("-")[1], "round": int(path.stem.split("-")[-1]), "raw": sanitize(raw)})
    if len(report["generator"]) != 6:
        raise ValueError("Expected three process pairs for Generator header scanning")
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
    report["excludedAttempts"] = sanitize(notes.get("excludedAttempts", []))
    report["limitations"] = ["Only 100 pages, one Windows computer; no 1000/10000-page or cross-OS reference.",
        "Three Blog/Docs process runs per target; overlapping ranges do not establish small performance changes.",
        "One MDX process per target, no MDX ratio conclusion.",
        "Generator header scan excludes schema binding, emission, Roslyn and C# compilation.",
        "Editor has 20 edits per shape/language and a 40-edit restart soak, not a long-duration soak or Extension Host benchmark.",
        "Verification stages overlap execution. Worker heap snapshots are not peak RSS. Unmeasured worker RSS and editor loaded-dependency identity remain unavailable."]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    text = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    # Reject accidental local path publication, including JSON-escaped Windows paths.
    if any(token in text for token in ("C:\\\\", "C:/Users/", "h_tky", "file:///")):
        raise ValueError("A local/private path remains in the public projection")
    args.output.write_text(text, encoding="utf-8")
    for section in ("blog", "typedDocsQuality"):
        print(section, json.dumps(report[section]["aggregates"]))


if __name__ == "__main__":
    main()
