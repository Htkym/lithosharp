# Official GitHub Pages deployment

Target: https://htkym.github.io/lithosharp/ (BasePath `/lithosharp/`).
The repository's Pages source must already be **GitHub Actions**. The workflow
never enables hosting, changes visibility or grants repository access.

`.github/workflows/publish-pages.yml` runs only by manual dispatch. Pushing it,
opening a PR or pushing a release tag does not deploy. `publish` defaults to
`false`; this builds and checks the package-only bilingual site and saves a
`github-pages` artifact without deploying it. Normal build CI separately checks
candidate packages using the same official BaseUrl and never deploys them.

## Publication order

1. Complete the stable Syntamark intake described in `eng/markdown/README.md`.
   The active `2.0.0-preview.2026111303` pair is not accepted for publication;
   `2.0.0` is the intended component target, not evidence of availability.
2. Complete review and normal push CI on current default `main`, tag that exact
   SHA with the stable LithoSharp release version, and finish its existing NuGet
   publication workflow. Every release package must be available from NuGet.org
   with that version and repository commit. No preview is relabeled or packed
   by the Pages workflow. LithoSharp's own version is taken from its projects,
   independently of the Syntamark component version.
3. Dispatch **publish-pages** from **main**, set `release_tag` to that existing
   stable tag, and leave `publish` false for validation first. It requires the
   latest successful normal push CI and NuGet publication for the exact source,
   and reuses the live stable-pair provenance/archive checks. A moving main or
   unavailable/inconsistent package fails closed. The site restores only from
   NuGet.org, with no candidate or local source feed. In the disposable Actions
   checkout, it backs up only the two provisional site locks, refreshes those
   locks from official packages, and locked-restores both consumers. Before
   building, every consumed LithoSharp version and restored archive SHA256 must
   match the release tag and the official archives checked by the publication
   gate. Checked-in candidate locks are not represented as final publication locks.
4. After reviewing validation and obtaining publication authorization, dispatch
   the same inputs from main with `publish` true. This builds afresh, checks the
   artifact, then rechecks main/tag/package provenance immediately before deploy.
   Only the deploy job has `pages: write` and `id-token: write`. It uses the
   `github-pages` environment and the repository's existing environment policy.
   Configure required reviewers there separately if that protection is desired.

The workflow reads the existing Pages URL and rejects a different destination
(such as an unexpected custom domain). The artifact contains the generated site
root directly, including `404.html`; do not nest it inside another `lithosharp`
directory or replace the custom404 with an SPA redirect. Generation checks links,
localized API content and mapped examples; the extra Pages check verifies root
files, custom404 BasePath and the canonical sitemap URL. Deployment is serialized
without cancelling an in-progress publication.

## Offline guard checks

```powershell
./eng/Test-PagesPublishGuards.ps1
```

These checks use owned temporary fixtures and do not publish or contact services.
They do not prove that stable packages exist, CI passed or the live site works.
The real workflow performs those live prerequisite checks. After an authorized
deployment, separately verify the reported public URL and a missing-page route.

Workflow dependencies must be recorded by the official `gh actions-lock`
generator; never hand-edit `.github/workflows/actions.lock`. Preserve existing
explicit SHA pins when regenerating.
