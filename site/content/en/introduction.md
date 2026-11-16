---
title: LithoSharp documentation
supported_version: "3.0"
sidebar_position: 1
---

LithoSharp combines C# site configuration, typed content and Markdown. This handbook
targets the 3.0 development line. The repository version matrix identifies the
selected candidate; a successful local build does not establish a published release.

Start with [building this site](../quickstart/), then use the
[feature inventory](../features/) to choose a guide. The English and Japanese
handbooks share document IDs and the same `3.0` variant. The language control
switches to the corresponding document; this site does not fabricate an older
version from current content.

This site uses the public package APIs in
[WebsiteFactory.cs](https://github.com/Htkym/lithosharp/blob/main/site/WebsiteFactory.cs).
Its handbook uses the documentation Markdown profile, scoped search and typed
component attributes. Separate example pages use a generated C# schema and input
catalog. The knowledge section uses an OKF reader with its own policy.

For existing 2.x sites, read [migration to 3.0](../migration/) before replacing
dependencies. Keep the existing output and publication baseline during migration.
