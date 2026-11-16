---
title: CLI, editor and language server
supported_version: "3.0"
prerequisites: [quickstart]
---

The CLI and editor use the same site factory and explicitly acquired project
context. Use the [CLI guide](../../reference/en/cli/)
for build, inspect, check and serve commands, and the
[tooling capability reference](../../reference/en/tooling-capabilities/)
for availability and acquisition requirements.

Trust the project before running site code through a build or inspection.
Typing itself consumes static schemas and captured project evidence. It must not
run DTO constructors, setters, runtime validators or C# component renderers.
Schema definitions and file membership changes invalidate their evidence;
catalog-dependent checks stay deferred when the relevant context is incomplete.

The language server applies document edits in order and suppresses diagnostics
from older generations. Reference navigation and rename use parsed reference
facts and the known catalog; unresolved context is not proof that no target
exists. Dynamic resolution and unsupported syntax can limit the available actions.
Review rename edits before applying them across documents.

Install the extension from its documented distribution path and match the
repository version matrix when testing candidates. Core/CLI versions and extension
versions have separate numbers. See [migration](../migration/) for 3.0 changes and
[operations](../operations/) for the difference between inspection and executed checks.
