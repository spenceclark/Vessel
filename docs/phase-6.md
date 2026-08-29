# Phase 6 — Ship / Open-Source: Implementation Spec

> Expands Phase 6 of [plan.md](plan.md). Design authority: [architecture.md](architecture.md);
> UI items per [ui-spec.md](ui-spec.md).
>
> **Goal:** a stranger with Ollama installed goes from download to seeing their first
> captured request in under two minutes, without reading more than the quickstart.

## 0. Scope

**In:** the four-RID self-contained single-file release with the trimming decision
finally taken (D1); CI + tag-driven releases (D3); first-run and failure-mode polish
(D4); README + licensing + repo hygiene (D5, D7); the bind-address banner (D6);
versioning (D8); the pre-release verification gate (§4).

**Out:** any new product features (Phases 7–8); package managers (winget/homebrew —
post-launch, demand-driven); code signing/notarization (documented as a known
first-run friction on macOS/Windows instead — see D2); auto-update. (Docker moved
IN — see D10.)

**Depends on:** Phase 5 accepted (done). Phase 5b (MCP) has landed, so the README's
MCP section (D5) is active and the D6 banner mentions MCP exposure; 5b's one open
manual gate item (real-client verification) is discharged at the §4 gate (item 6).

---

## 1. Decisions (S-table)

> **Status: S1–S4 ALL APPROVED as recommended, 2026-08-29.** Product name vessel,
> v0.1.0, docs ship as-is, landing page on `vesselproxy.app` (domain purchased,
> Cloudflare registrar + Pages hosting per D9). The recommendation column is
> authoritative for implementing agents; human steps (S1 name check, domain DNS,
> repo-public flip, tag push) are the author's and tracked in §4 item 7.

| # | Decision | Recommendation |
|---|---|---|
| S1 | **Public name & repo.** "Vessel" is a crowded name across software (wallets, package tools, CSS libs). | Keep the product name **vessel** (identity, logo, docs are built on it; none of the collisions are in the LLM/proxy space), repo `vessel` under the author's account. A trademark-level check is a human task before flipping public; the fallback (`vessel-proxy` as repo name only) costs nothing later. |
| S2 | **Initial version.** | **v0.1.0.** Honest for a pre-feedback release; v1.0 after the Phase 7/8 arc and real external use. |
| S3 | **Ship the internal docs?** The `docs/` tree (specs, review saga, decision logs). | **Yes, as-is.** It is an unusually complete engineering record and a differentiator for contributors; README links brief + architecture as the entry points. Nothing in it is sensitive (grep-verify: no keys, no personal paths beyond the author's already-public identity). |
| S4 | **Landing page + domain.** RDAP-checked 2026-08-29: `getvessel.app`, `getvessel.dev`, `usevessel.app`, `usevessel.dev`, `vessel.tools` all **taken**; `vesselproxy.app`, `vesselproxy.dev`, `vessel-proxy.dev` **available**. | **Yes to a landing page (D9); domain `vesselproxy.app`** — descriptive, hyphenless, enforced-HTTPS TLD, and it sidesteps the crowding S1 flagged. Domain purchase is a human step (~$15–20/yr). |

## 2. Key implementation decisions

### D1 — Trimming: ship trimmed, MCP trim-safety fixed first (updated post-5b)

> **Updated 2026-08-29 (pre-implementation review).** The original premise —
> "`PublishTrimmed=true` has stayed zero-warning and smoke-clean at every
> checkpoint" — predates Phase 5b and is false as written: phase-5b.md §6 records
> that a trimmed `win-x64` publish starts and serves `/vessel/api/status`, but
> `/vessel/mcp` 404s. The official MCP SDK's endpoint/tool registration is not
> trim-safe unaided. That failure lives in the managed IL, so it is
> RID-independent — the original per-RID fallback ("a RID that fails trimmed ships
> untrimmed") would have silently resolved to **all four RIDs untrimmed** (~102 MB),
> voiding the size rationale below.

The decision itself stands: **trimmed on, in the csproj** (~21 MB vs ~102 MB — a 5×
download difference that matters for a "just download one file" pitch). The recorded
Phase 6 blocker is resolved first, by making the SDK trim-safe: trimmer roots for
the `ModelContextProtocol*` assemblies (a `TrimmerRootDescriptor`/ILLink XML in the
csproj is the first thing to try; if SDK annotations alone suffice, that's smaller),
verified by `verify/publish-smoke.ps1 -Trimmed` — whose Phase 5b M7 MCP-initialize
probe is exactly the check that catches this. The cross-platform smoke port (D3)
keeps that probe on every RID for the same reason.

Fallback, recorded: if trim-safety cannot be made clean before release, **all RIDs
ship untrimmed** for v0.1 and trimming becomes a fast-follow release — one property
flip, not a per-RID split. Native AOT stays explicitly out — the marginal size win
doesn't justify a new compatibility frontier at launch.

### D2 — Release artifacts

- RIDs: `win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`.
- Artifact = archive (`.zip` for win, `.tar.gz` for the rest) named
  `vessel-{version}-{rid}`, containing the single executable (`vessel`/
  `vessel.exe`), `LICENSE`, and a `README.txt` pointing at the repo (the real README
  lives there; don't ship a stale copy).
- Unsigned binaries, stated plainly: README documents the macOS Gatekeeper
  (`xattr -d com.apple.quarantine` or right-click-open) and Windows SmartScreen
  first-run steps. Signing/notarization is a cost decision deferred until the
  project earns it.
- `vessel --version` prints version + commit; `/vessel/api/status` and the UI's
  settings dialog surface the same (version is stamped from the release tag via
  csproj `Version` property).

### D3 — CI and releases (GitHub Actions)

- **`ci.yml`** (push/PR): backend build + full test suite on a matrix of
  `ubuntu-latest` + `windows-latest` (the PD4 restoration is the baseline — Linux
  signal is non-negotiable pre-release); frontend `tsc` + `vitest` + `vite build`
  on ubuntu; lint job (warnings currently tolerated, tracked in D7).
- **`release.yml`** (tag `v*`): build all four RIDs (self-contained publish is
  cross-RID-capable; frontend builds once, embedded per-RID), then **smoke each
  artifact on its native runner**: win-x64 on `windows-latest`, linux-x64 on
  `ubuntu-latest`, osx-arm64 on `macos-latest` (Apple Silicon), osx-x64 on
  `macos-13` (Intel). `macos-13` is GitHub's last free Intel runner and is
  deprecating — if it retires before release, osx-x64 is still built on any runner
  and smoked under Rosetta 2 on an arm64 macOS runner, or that RID consciously
  ships build-only (recorded here). The existing `publish-smoke` logic is ported
  to a cross-platform script (the PowerShell version remains for local Windows
  use); ephemeral ports + temp config throughout, per the hardened G4 behavior —
  and the Phase 5b MCP-initialize probe retained on every RID (it is the check
  that catches the D1 trim failure). Smoke green → draft GitHub Release with the
  four archives + checksums file; release notes from `CHANGELOG.md`.
- No *user* secrets required by CI (no live-API tests in the pipeline;
  `verify.ps1`'s live-key checks remain a local, opt-in tool). D10's GHCR push
  authenticates with the workflow's built-in `GITHUB_TOKEN` (`packages: write`) —
  not a stored secret.

### D4 — First-run and failure-mode polish

The happy path already exists (config creation + the two-line banner — whose
Program.cs text still prints a stale dev-era `(phase 3)` suffix from phase-0 D6's
example; the polished two-liner lands here and phase-0.md is corrected in place).
This phase adds honest *unhappy* paths, each currently a raw exception or confusing log:

- **Port in use** (observed during the review saga): clean one-line exit —
  `listen address 127.0.0.1:4550 is already in use — is Vessel already running?
  Change "listen" in vessel.json or pass --config.` Exit code 1, no stack trace.
- **DB locked/unwritable at startup**: named error with the db path and likely
  causes (second instance, permissions), no stack trace.
- **Malformed config**: already handled (error + exit); verify message quality in
  the gate.
- **Config/data location — three-level resolution** (replaces Phase 0 D6's
  beside-the-exe-only rule for shipped builds; phase-0.md updated in place when
  this lands):
  1. `--config <path>` — explicit, always wins (containers: `/data/vessel.json`).
  2. `vessel.json` **beside the exe, if it already exists** — "portable mode":
     a deliberate self-contained folder (and every existing setup, incl. the
     author's) keeps working unchanged; placing a config next to the exe is the
     opt-in.
  3. Otherwise — **platform config dir, created on first run**:
     `%LOCALAPPDATA%\vessel-proxy\` (Windows), `~/.config/vessel-proxy/` (Linux,
     XDG-respecting), `~/Library/Application Support/vessel-proxy/` (macOS).
     `vessel-proxy`, not `vessel` — collision-safe against the crowded name, same
     reasoning as the S4 domain. `vessel.db` lives beside whichever config wins,
     as always.
  This fixes the three fresh-download failure modes: data landing in `~/Downloads`,
  read-only install locations (Program Files) breaking first-run, and macOS
  quarantine's read-only execution path. The startup banner and `--help` **print
  the resolved config + data paths**, and the README privacy section names them —
  "where is my data" must never require reading source.
- `--help` output: usage, `--config`, `--version`, config-file location rules
  (the three levels above), resolved paths, UI URL. Kept to one screen.
- **First-run browser open**: when the config file was *just created* (first run
  only — never on routine restarts), open the system browser at `/vessel/` after
  the listener is up. `--no-open` suppresses it (scripts, headless). This is the
  single highest-leverage line for the two-minute test: double-click → the UI is
  in front of you. Implementation per-OS (`start`/`open`/`xdg-open`), failure to
  open is logged at Debug and never fatal.

### D5 — README (the launch's real deliverable)

Structure, in order — optimized for the two-minute stranger:

1. **Hero**: one-paragraph what/why + the Compare GIF (human-recorded — §4) + a
   screenshot of the main UI.
2. **Quickstart** (the 2-minute path): download → run → point a client at it —
   per-client one-liners for Ollama CLI (`OLLAMA_HOST`), OpenAI SDK (`base_url`),
   Anthropic SDK, curl. First captured request visible in the UI = success state.
   States plainly: Vessel is a **foreground process** — the terminal window is
   Vessel; closing it stops the proxy and capture. One sentence pointing always-on
   users at their OS's own mechanism (Task Scheduler / systemd / launchd) — 
   documented, not built.
3. **Features**: capture/metrics, search/filters, sessions/tags, replay + Compare
   (with the same-format matrix stated), copy-as-curl, warnings, health, themes.
   MCP section iff Phase 5b shipped.
4. **Configuration reference**: every `vessel.json` field, generated *from* the
   validated config model so it can't drift (a small doc-gen check in CI, or a
   hand table with a test asserting field-list parity — implementer's choice, but
   parity must be asserted, not assumed).
5. **Replay auth**: the env-var convention (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`,
   `authEnv`), explicitly "Vessel never stores keys".
6. **Privacy & data**: `vessel.db` contains your prompts, stored locally, bodies
   compressed; auth headers redacted at rest; localhost-by-default + what the
   banner means; retention caps and clear-down.
7. **Building from source** (dotnet 10 + Node), **architecture** (links into
   `docs/`), **license**.

### D6 — Bind-address banner (the §8 promise, due now)

When the effective listen address is non-loopback: a persistent, non-dismissable
warning banner in the UI header region ("Vessel is listening on 0.0.0.0 — anyone on
your network can read captured prompts" plus ", and MCP clients can reach
/vessel/mcp" when `mcp.enabled`) plus a startup log Warning. The condition keys off
the address Kestrel **actually bound** — `ConfigStore.RecordBoundListen`, the fixed
point the R16 review introduced — never the configured `listen` string, which may
name port `0` or something the OS didn't grant. Banner styling per ui-spec (`--warn`
tinted fill, full-width strip above the header panel — ui-spec gains the pattern in
the same change). Loopback binds: nothing; in-container `0.0.0.0` is softened to an
info note per D10.

### D7 — Repo hygiene & polish pass

- `LICENSE` (MIT, author's name), `CHANGELOG.md` (v0.1.0 entry summarizing the
  product, not the build saga), minimal `CONTRIBUTING.md` (build/test commands,
  house rules pointer to AGENTS.md, "specs live in docs/").
- Third-party notices: a `THIRD-PARTY-NOTICES.md` listing direct dependencies and
  licenses (all MIT/Apache/BSD-class — verify while writing it).
- The six tolerated lint warnings from the review rounds: fixed or individually
  suppressed-with-reason; zero-warning lint becomes a CI gate thereafter.
- Repo top-level `README.md` per D5; `.github/` issue template kept to one minimal
  bug template (repro + `vessel --version` + backend types involved).
- Sweep: no personal absolute paths, no keys, no `vessel.db`/`vessel.json` artifacts
  tracked (gitignore already covers; verify).

### D8 — Versioning

SemVer from `v0.1.0` (S2). Tag drives everything. Mechanism (named so it isn't
rediscovered at implementation): **MinVer** — a build-time NuGet package that
derives `Version` from git tags, so a publish from the `v0.1.0` checkout stamps
itself exactly, untagged dev builds get its pre-release default (e.g.
`0.0.1-alpha.0.7+{sha}` — superseding the earlier `0.0.0-dev+{sha}` sketch), and
neither CI nor the developer passes `-p:Version` by hand. (`--version`, `/status`,
and the settings dialog already read the informational version via
`StatusEndpoint.Version`, so nothing downstream changes.) `CHANGELOG.md` maintained
by hand per release — the repo's phase reports are the raw material, the changelog
is the user-facing distillation.

### D9 — Landing page (S4): one static page, same design system, zero drift

A single static page in `site/`, deployed via GitHub Pages with the custom domain —
it lives in the repo, versioned like everything else.

- **Design**: the vessel design system verbatim — ui-spec tokens, mark, wordmark,
  bundled fonts, dark-first with the same light support. It should look like the
  product because it is dressed as the product. No new design language.
- **Content (thin, deliberately)**: hero (one sentence + the Compare GIF), three
  feature bullets max, OS-detected download button (links resolve the latest
  release via the GitHub API client-side — no hardcoded versions), the quickstart
  one-liner, the privacy sentence ("local-first — your prompts never leave your
  machine"), and a GitHub link. Depth lives in the README; the page never
  duplicates prose that can drift.
- **No trackers, no analytics, no cookies** — on-brand for a privacy-pitched tool,
  and one less thing to disclose. GitHub's built-in traffic stats suffice.
- Deploy: **Cloudflare Pages** (the domain is registered at Cloudflare — purchased;
  same hosting as the author's blog): a Pages project connected to the repo, build
  output = `site/` with no build step, deploy-on-push via Cloudflare's Git
  integration — no GitHub workflow needed. Custom domain attached natively in the
  Cloudflare dashboard (a human step, minutes). One repo consequence: `site/` must
  stay pure static (no framework build) so the Pages config remains
  "no build command"; if the page ever needs a build, revisit deliberately.

---

### D10 — Container image on GHCR

The compose crowd (Ollama + Open WebUI stacks) is a first-class slice of the
audience, and for them `ghcr.io/{owner}/vessel` is the native install. Scope kept
tight:

- **Image**: `mcr.microsoft.com/dotnet/runtime-deps:10.0` base + the self-contained
  linux binary; multi-arch `linux/amd64` + `linux/arm64` if the cross-publish is
  clean, amd64-only otherwise (recorded either way). Tags: `{version}` + `latest`,
  pushed by `release.yml` on the same tag that cuts the binaries.
- **State convention**: `VOLUME /data`; entrypoint defaults to `--config
  /data/vessel.json` — config and `vessel.db` live on the volume, never in the
  ephemeral layer.
- **Container-aware first-run** (via `DOTNET_RUNNING_IN_CONTAINER`): default listen
  `0.0.0.0:4550` (the published port is the boundary; loopback is useless in a
  container), browser-open suppressed, and the D6 bind banner softened to an info
  note in-container (0.0.0.0 is the normal state there — the banner keeps its full
  severity everywhere else). **And the default backend URL**: a fresh in-container
  config writes `http://host.docker.internal:11434`, not `localhost` — inside the
  container `localhost` is the container itself, so the stock default would point
  at nothing and the compose story below would capture its first request never.
  `extra_hosts` only makes the name resolvable; it cannot redirect `localhost`.
- **README**: a compose example as the canonical container doc — `open-webui →
  vessel → ollama` with the one-line `OLLAMA_BASE_URL` change — plus the
  bare-docker `host.docker.internal` note. The default backend URL is wrong inside
  a container by construction; the docs say so instead of the image guessing.
- **A shipped `compose.yaml`** (repo root, embedded verbatim in the README): the
  one-line `docker compose up -d` experience — vessel service on the GHCR image,
  `4550:4550`, named volume `vessel-data:/data`, `restart: unless-stopped`, and
  `extra_hosts: host.docker.internal:host-gateway` so the **default assumption is
  Ollama on the host** (the most common real setup — which only holds because of
  the container first-run backend default above; `host-gateway` alone merely makes
  the name resolvable). A commented-out `ollama`
  service block turns it into the full in-compose stack (backend URL switching to
  `http://ollama:11434` noted inline in the comments). One file, both stories; the
  comments are the documentation.
- Container smoke in `release.yml`: run the pushed image, hit status + proxy a stub,
  verify `/data` persistence across a container restart — and `docker compose up`
  with the shipped file as part of the smoke, so the artifact users copy is itself
  tested. amd64 runs natively on the runner; the arm64 variant runs under QEMU
  (`setup-qemu-action`) or, if too slow for the gate, is build-verified only —
  whichever ships is recorded here.

## 3. New/changed layout

```
LICENSE  CHANGELOG.md  CONTRIBUTING.md  THIRD-PARTY-NOTICES.md  README.md
site/                              # D9 landing page (pure static; Cloudflare Pages)
compose.yaml                       # D10 shipped compose example (repo root)
.github/workflows/{ci.yml,release.yml}
.github/ISSUE_TEMPLATE/bug.md
verify/publish-smoke.sh            # cross-platform port of the smoke (ps1 remains)
src/Vessel/                        # D4 startup failure paths; --help/--version;
                                   # D6 banner support on /status (nonLoopback flag)
frontend/src/components/...        # D6 banner strip
```

## 4. Verification gate (release candidate)

1. CI green on both OS runners; lint zero-warning.
2. `release.yml` dry-run (workflow_dispatch) produces all four artifacts; **all four
   native smokes green** (or D1's recorded fallback taken: all RIDs untrimmed).
3. **The literal two-minute test, performed fresh**: on a machine/VM with only
   Ollama, download the artifact, follow only the README quickstart, first captured
   request visible in the UI. Timed. Under two minutes or the README gets fixed.
4. Failure-mode checks: port-in-use, locked DB, malformed config — each produces
   its D4 message, no stack traces.
5. Bind `0.0.0.0` → banner + log warning; loopback → neither.
6. Live-key verification finally run: `verify.ps1 -OpenAI -Anthropic` (the item
   pending since Phase 0), plus one replay against each live API — and Phase 5b's
   one open manual gate item (a real MCP client against live traffic) discharged
   here, since the README's MCP section (D5) publishes the `claude mcp add`
   two-liner that gate exists to verify.
7. Human items: the Compare GIF + screenshots recorded; S1 name check done; repo
   made public; tag `v0.1.0` pushed; release published from the draft.

## 5. Acceptance

Gate items 1, 2, 4, and 5 automated/agent-verifiable and done. Items 3 and 6 are
human-assisted by nature (item 3 needs a fresh machine with Ollama and a real
download; item 6 needs the author's live keys) — performed by or with the author
and recorded. Item 7's human steps checklist handed to the author. plan.md Phase 6
ticked; deviations recorded here.

## 6. Implementation record (2026-08-29)

- D1 shipped trimmed: `verify/publish-smoke.ps1 -Trimmed -InPlace` passed its status,
  proxy, embedded-UI, and Phase 5b MCP-initialize checks at 24.6 MB on win-x64. The MCP
  SDK assemblies are rooted through `Mcp/ILLink.Descriptors.xml`; no untrimmed fallback.
- D2/D3 ship all four artifacts, per-native-runner smoke jobs, checksums, GHCR publishing,
  and a draft release. The first GitHub validation must run `Release` via
  `workflow_dispatch` from the release commit and confirm win-x64, linux-x64, osx-arm64,
  osx-x64, and container-smoke all pass; workflow dispatch deliberately does not publish.
- D9 uses the more specific Cloudflare Pages instruction rather than the earlier "GitHub
  Pages" wording: `site/` is static with no build/deploy workflow. Compare GIF and UI
  screenshot remain author-recorded release assets; no synthetic media was substituted.
- D10 builds linux/amd64 and linux/arm64 through Buildx/QEMU. The release container gate
  verifies status, a proxy stub, `/data` persistence, restart, and the shipped compose file.
