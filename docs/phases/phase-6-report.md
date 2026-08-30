# Phase 6 — Review Report

Reviewer pass over the uncommitted working tree against [phase-6.md](phase-6.md),
[plan.md](../plan.md), [architecture.md](../architecture.md), and the project rules in
[AGENTS.md](../../AGENTS.md). Reviewed at working-tree state on 2026-08-29 (branch `main`,
atop `bb867f7`).

## Summary of verdict

**Phase 6 is implemented comprehensively and correctly.** Every decision in the spec
(D1–D10) has a corresponding, coherent change, and all agent-verifiable gate items
(§4 items 1, 2, 4, 5) pass. The build is clean, the full test suite is green, and the
central technical risk of the phase — trimming the single-file binary without losing the
Phase 5b MCP endpoint (D1) — is validated end-to-end here, not just asserted.

Findings are all **minor / polish**; none block the release candidate. The remaining gate
items (§4 items 3, 6, 7) are human-assisted by nature (fresh-machine two-minute test, live
API keys, GIF/screenshots, name check, repo-public flip, tag push) and are correctly left
to the author.

## What I verified (not just read)

| Check | Result |
|---|---|
| `dotnet build Vessel.sln -c Release` | **0 warnings** (Directory.Build.props sets `TreatWarningsAsErrors`, so this is the D7 zero-warning lint gate) |
| `dotnet test Vessel.sln -c Release` | **320/320 passed**, 0 skipped |
| `oxlint` (frontend lint gate) | **clean**, exit 0 |
| `tsc --noEmit` | clean |
| `vitest` | **97/97 passed** (15 files) |
| `vite build` | succeeds (chunk-size advisory only) |
| Trimmed single-file publish, win-x64 (`PublishTrimmed=true -p:PublishSingleFile=true`) | **clean, no IL trim warnings**, 24.6 MB — matches the phase-6.md §6 record |
| `verify/publish-smoke.sh` against the trimmed binary | **passed** — `/vessel/api/status`, **MCP `initialize`**, proxy passthrough, and embedded UI all served. This is the exact D1 blocker (`/vessel/mcp` 404 under trimming) proven resolved by `Mcp/ILLink.Descriptors.xml`. |
| MinVer stamp (`vessel --version`) | `0.0.0-alpha.0.21+<sha>` on an untagged tree — version + commit per D2/D8; a `v0.1.0` tag will stamp `0.1.0+<sha>` |
| Config-doc parity test | logic traced against `VesselConfig`; the 12 documented fields exactly match the reflected model (test passes in the suite) |

## Spec coverage (D1–D10)

- **D1 (trim):** `PublishTrimmed=true` in the csproj; `Mcp/ILLink.Descriptors.xml` roots
  both `ModelContextProtocol` assemblies via `<TrimmerRootDescriptor>`. Verified trimmed +
  MCP-live above. No untrimmed fallback needed. ✓
- **D2 (artifacts/version):** four RIDs, archive-per-RID with `LICENSE` + `README.txt`,
  checksums, `--version` prints version+commit, `/status` + settings dialog read the same
  informational version (`StatusEndpoint.Version` now returns the full string incl. `+sha`).
  ✓ (one packaging nit — finding 1)
- **D3 (CI + release):** `ci.yml` reshaped into `backend` (ubuntu+windows matrix, build +
  test), `frontend` (test + build), `lint` (backend zero-warning + oxlint) jobs;
  `release.yml` builds four RIDs, smokes each on its native runner (incl. `macos-13`
  Intel), plus container-smoke, then a draft release gated on all smokes. `fetch-depth: 0`
  is set on every job that builds (MinVer needs tags). ✓
- **D4 (first-run/failure polish):** `ConfigPathResolver` implements the three-level
  resolution (explicit → beside-exe-if-exists → platform `vessel-proxy` dir); port-in-use
  and DB-locked produce clean one-line exits with no stack trace; `--help`/`--version`/
  `--no-open`; first-run browser open (per-OS, Debug-logged on failure, suppressed in
  container); banner/help print resolved paths. The stale `(phase 3)` suffix is removed. ✓
- **D5 (README):** rewritten to the two-minute structure — quickstart, per-client
  one-liners, features, routing/tags, MCP, container/compose, config reference (with the
  CI-asserted `config-fields` parity marker), replay auth, privacy, build-from-source. ✓
- **D6 (bind banner):** `ListenSecurity(isNonLoopback, isContainer)` on `/status`, computed
  from the address Kestrel **actually bound** (via `IServerAddressesFeature`), plus a
  startup log Warning (Info in-container). `BindAddressBanner.tsx` renders the `--warn`
  strip (softened to `--info` in-container); ui-spec updated. ✓
- **D7 (hygiene):** `LICENSE` (MIT, author), `CHANGELOG.md`, `CONTRIBUTING.md`,
  `THIRD-PARTY-NOTICES.md`, `.github/ISSUE_TEMPLATE/bug.md`; `.gitignore` now covers
  `vessel.json`/`vessel.db*`/`frontend/dist`/`node_modules`; the tolerated lint warnings are
  individually suppressed-with-reason and the gate is now enforced. ✓
- **D8 (MinVer):** replaces the hardcoded `<Version>`; tag-driven, alpha.0 default. ✓
- **D9 (site):** single static `site/index.html`, design-system tokens verbatim, bundled
  fonts, OS-detected download resolving the latest release client-side, no trackers. ✓
- **D10 (container):** `Dockerfile` (frontend stage → cross-publish → runtime-deps),
  `compose.yaml`, `.dockerignore`, container-aware first-run (`0.0.0.0` default +
  `host.docker.internal` backend + browser-open suppressed + banner softened), and a
  container-smoke job asserting status, proxy stub, `/data` persistence across restart, and
  the shipped compose file. ✓

## Findings (all minor)

### 1. LOW — release archive ships stray files beside the executable

`release.yml` packages with `cp publish/${{ matrix.rid }}/Vessel* "$stage/"`. The publish
output directory contains not only the single-file executable but also `Vessel.pdb`
(~287 KB debug symbols) and `Vessel.staticwebassets.endpoints.json`. The `Vessel*` glob
copies all three, so the shipped archive contains `vessel.exe` **plus** `Vessel.pdb` **plus**
the stray json — where D2 specifies "the single executable, `LICENSE`, and a `README.txt`".

Harmless but untidy, and it slightly undercuts the "just one file" pitch. Copy the
executable explicitly instead of globbing, e.g. copy `Vessel`/`Vessel.exe` by exact name
(the subsequent `mv` already special-cases both), or add `-p:DebugType=none` /
`-p:DebugSymbols=false` to the publish so no pdb is emitted.

### 2. LOW — `dialog.test.ts` import relocated to the bottom of the file

In `frontend/src/components/ui/dialog.test.ts`, `import { createElement } from 'react'` was
moved from line 1 to the **last line of the file**. It still works (ES module imports are
hoisted) and oxlint does not flag it, but it reads as an editing artifact and violates the
usual import-first convention. Move it back to the top with the other imports.

### 3. LOW — verify the MCP SDK license row in THIRD-PARTY-NOTICES.md

`THIRD-PARTY-NOTICES.md` lists `ModelContextProtocol.AspNetCore` as **Apache-2.0**. The
official ModelContextProtocol C# SDK is published under **MIT**. D7 asked for this to be
verified while writing it; please confirm against the package's own license and correct the
row if needed. (All other rows look right: CVA is Apache-2.0, lucide-react is ISC, the
fontsource packages are OFL-1.1.)

### 4. LOW — `--help` prints a literal `<listen>` and omits `--help` itself

`--help` prints `UI: http://<listen>/vessel/` (a literal placeholder rather than a resolved
URL) and does not list `--help`/`-h` among the options. The higher-value promise — printing
the **resolved config and data paths** — is met. Optional: resolve the listen from the
loaded config for the URL line, and add `--help` to the usage list.

## Observations (non-blocking, no change requested)

- **Banner width vs. ui-spec wording.** `BindAddressBanner` sits inside the
  `max-w-[1600px]` content column (identical placement to the existing `CaptureHealthBanner`)
  rather than a literal edge-to-edge strip. This matches the established banner convention in
  the app; it reads as a deliberate consistency choice rather than a miss.
- **GHCR owner casing.** `compose.yaml` hardcodes `ghcr.io/spenceclark/vessel:latest` while
  `release.yml` pushes to `ghcr.io/${{ github.repository_owner }}/vessel`. GHCR requires a
  lowercase owner; these agree only if the account is literally `spenceclark`. Worth a glance
  during the item-7 repo flip. (README's `github.com/spenceclark/Vessel` with a capital V is
  fine — GitHub web URLs are case-insensitive.)
- **DB-lock path.** The startup DB-failure message relies on `SqliteException` surfacing
  during `app.StartAsync()` — it does, because `CaptureWriterService.StartAsync` calls
  `store.Initialize()` (open + migrate) there. The dominant real case (read-only install dir,
  e.g. Program Files) is covered; a genuine two-instances collision usually trips the
  port-in-use check first, which is also handled.
- **Release assets.** `site/` references `assets/compare.gif` and the README references
  `docs/assets/*`; these are the author-recorded GIF/screenshots deferred to gate item 7. The
  landing page keeps the `<img>` hidden until it loads, so a missing GIF degrades gracefully.

## Rules / process compliance (AGENTS.md)

- **No commits.** Working tree left intact for review. ✓
- **House naming (.editorconfig).** No private-field naming violations introduced; new code
  is records, statics, locals, and a TSX component. ✓
- **Whole-tree call-site sweep.** The `StatusPayload` signature gained a positional
  `ListenSecurity` — the only C# constructor (`StatusEndpoint`), the frontend `StatusPayload`
  type, and the one `getStatus` test mock (`DetailPane.test.ts`) are all updated; other
  `getStatus` consumers read fields structurally and are unaffected. ✓
- **Findings written in place; docs corrected in place.** phase-0.md's beside-the-exe-only
  config rule is superseded in place, ui-spec gains the banner pattern, and phase-6.md's
  implementation record is appended — no correction-note-after-wrong-content pattern. ✓
- **In scope.** Everything maps to Phase 6 decisions; nothing reaches into Phases 7–8. ✓

## Gate status (phase-6.md §4)

| Item | Status |
|---|---|
| 1. CI green both OS, lint zero-warning | Agent-verified locally (build/test/lint all green); confirm on the runners once pushed |
| 2. `release.yml` dry-run, four native smokes green | Trimmed publish + MCP smoke verified locally for win-x64; the full four-RID + container matrix is the first `workflow_dispatch` run (author) |
| 3. Fresh two-minute test | Human — fresh machine + Ollama + real download |
| 4. Failure-mode messages (port/DB/config) | Implemented per D4; no stack traces |
| 5. `0.0.0.0` → banner + log warning; loopback → neither | Implemented and wired (D6) |
| 6. Live-key + real-MCP-client verification | Human — author's keys |
| 7. GIF/screenshots, name check, repo public, tag push | Human checklist |

**Recommendation:** address the four low findings (all quick), then proceed to the human
gate items. The implementation itself is release-candidate quality.
