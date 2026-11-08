#!/usr/bin/env python3
"""Protect public measurement exports from local paths and misleading commit labels."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("curate_reference", Path(__file__).with_name("curate-reference.py"))
curator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(curator)


class PublicReferenceTests(unittest.TestCase):
    def test_rejects_nested_and_embedded_local_paths(self):
        for value in (r"C:\Users\person\result.json", "D:/gitroot/result.json", r"\\server\share\result.json",
                      "/home/person/result.json", "/tmp/result.json", "file:///private/result.json",
                      "Saved at D:/gitroot/result.json", "Saved at /custom/result.json"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                curator.assert_public({"observations": [{"value": value}]})
        with self.assertRaises(ValueError):
            curator.assert_public({"/private/result.json": 1})

    def test_allows_repository_paths_identities_and_public_urls(self):
        curator.assert_public({"src/LithoSharp/SiteGenerator.cs": "abcdef",
                               "source": "https://github.com/Htkym/lithosharp",
                               "scope": "Core runtime/parser and reflection-call overhead; ms"})

    def test_committed_label_requires_matching_clean_candidate(self):
        commit = "a" * 40
        good = {"role": "candidate", "commit": commit, "changes": []}
        curator.validate_committed_campaign({"targets": [good]}, commit)
        for targets in ([], [{**good, "commit": "b" * 40}],
                        [{**good, "changes": [" M Directory.Build.props"]}],
                        [{**good, "changes": [" M src/LithoSharp/SiteGenerator.cs"]}]):
            with self.subTest(targets=targets), self.assertRaises(ValueError):
                curator.validate_committed_campaign({"targets": targets}, commit)


    def test_generator_requires_three_distinct_complete_pairs(self):
        good = [{"label": label, "round": round_number} for label in ("baseline", "candidate") for round_number in (1, 2, 3)]
        curator.validate_generator_pairs(good)
        invalid = [good[:-1], good[:-1] + [good[0]],
                   [{"label": "baseline", "round": n} for n in range(1, 7)]]
        for observations in invalid:
            with self.subTest(observations=observations), self.assertRaises(ValueError):
                curator.validate_generator_pairs(observations)

    def test_committed_capture_rejects_mixed_harness_commits(self):
        baseline, commit, stale = "a" * 40, "b" * 40, "c" * 40
        def identity(sha):
            return {name: {"informationalVersion": "2.2.0+" + sha} for name in ("product", "harness")}
        def site(sha):
            return {"targets": [{"label": "candidate", "role": "candidate", "commit": sha}],
                    "observations": [{"label": "candidate", "raw": {"Provenance": {"Commit": sha}, "Identity": identity(sha)}}]}
        report = {"blog": site(commit), "typedDocsQuality": site(commit),
                  "generator": [{"label": "candidate", "raw": {"provenance": {"Commit": commit},
                                  "identity": identity(commit), "generator": {"informationalVersion": "2.2.0+" + commit}}}],
                  "mdx": [{"label": "candidate", "raw": {"identity": identity(commit)}}],
                  "editor": {"latency": {"identity": {"sourceCommit": commit}}}}
        report["blog"]["targets"].append({"label": "released-2.1.3", "role": "baseline", "commit": baseline})
        curator.validate_measurement_commits(report, commit)
        paths = [("blog", "observations", 0, "raw", "Provenance", "Commit"),
                 ("typedDocsQuality", "observations", 0, "raw", "Identity", "product", "informationalVersion"),
                 ("generator", 0, "raw", "provenance", "Commit"),
                 ("generator", 0, "raw", "generator", "informationalVersion"),
                 ("mdx", 0, "raw", "identity", "harness", "informationalVersion"),
                 ("editor", "latency", "identity", "sourceCommit")]
        for path in paths:
            changed = copy.deepcopy(report)
            parent = changed
            for key in path[:-1]:
                parent = parent[key]
            parent[path[-1]] = "2.2.0+" + stale if path[-1] == "informationalVersion" else stale
            with self.subTest(path=path), self.assertRaises(ValueError):
                curator.validate_measurement_commits(changed, commit)



if __name__ == "__main__":
    unittest.main()
