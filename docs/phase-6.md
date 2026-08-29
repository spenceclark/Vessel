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

**Out:** any new product features (Phases 7–8); package managers (winget/homebrew/
docker — post-launch, demand-driven); code signing/notarization (documented as a
known first-run friction on macOS/Windows instead — see D2); auto-update.

**Depends on:** Phase 5 accepted (done). Phase 5b (MCP) is independent — if it lands
before the first release, the README's MCP section (D5) activates and the D6 banner
mentions MCP exposure; if not, both are dropped from v0.1 docs rather than promising
futures.

---

## 1. Decisions requiring sign-off (S-table)

| # | Decision | Recommendation |
|---|---|---|
| S1 | **Public name & repo.** "Vessel" is a crowded name across software (wallets, package tools, CSS libs). | Keep the product name **vessel** (identity, logo, docs are built on it; none of the collisions are in the LLM/proxy space), repo `vessel` under the author's account. A trademark-level check is a human task before flipping public; the fallback (`vessel-proxy` as repo name only) costs nothing later. |
| S2 | **Initial version.** | **v0.1.0.** Honest for a pre-feedback release; v1.0 after the Phase 7/8 arc and real external use. |
| S3 | **Ship the internal docs?** The `docs/` tree (specs, review saga, decision logs). | **Yes, as-is.** It is an unusually complete engineering record and a differentiator for contributors; README links brief + architecture as the entry points. Nothing in it is sensitive (grep-verify: no keys, no personal paths beyond the author's already-public identity). |
| S4 | **Landing page + domain.** RDAP-checked 2026-08-29: `getvessel.app`, `getvessel.dev`, `usevessel.app`, `usevessel.dev`, `vessel.tools` all **taken**; `vesselproxy.app`, `vesselproxy.dev`, `vessel-proxy.dev` **available**. | **Yes to a landing page (D9); domain `vesselproxy.app`** — descriptive, hyphenless, enforced-HTTPS TLD, and it sidesteps the crowding S1 flagged. Domain purchase is a human step (~$15–20/yr). |

## 2. Key implementation decisions

### D1 — Trimming: ship trimmed, gated per-RID by the smoke

The data has been gathered since Phase 0: `PublishTrimmed=true` has stayed
zero-warning and smoke-clean at every checkpoint (~21 MB vs ~102 MB — a 5× download
difference that matters for a "just download one file" pitch). Decision: **trimmed
on, in the csproj, now** — with the per-RID publish smoke as the gate. If any RID's
smoke fails trimmed, that RID ships untrimmed (a per-RID msbuild property, recorded
in this file) rather than blocking the release. Native AOT stays explicitly out —
the marginal size win doesn't justify a new compatibility frontier at launch. If
Phase 5b's MCP SDK lands, its trim-cleanliness is part of that phase's gate (already
specced there), not re-litigated here.

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
  `macos-13` (Intel). The existing `publish-smoke` logic is ported to a
  cross-platform script (the PowerShell version remains for local Windows use);
  ephemeral ports + temp config throughout, per the hardened G4 behavior. Smoke
  green → draft GitHub Release with the four archives + checksums file; release
  notes from `CHANGELOG.md`.
- No secrets required by CI (no live-API tests in the pipeline; `verify.ps1`'s
  live-key checks remain a local, opt-in tool).

### D4 — First-run and failure-mode polish

The happy path already exists (config creation + the two-line banner). This phase
adds honest *unhappy* paths, each currently a raw exception or confusing log:

- **Port in use** (observed during the review saga): clean one-line exit —
  `listen address 127.0.0.1:4550 is already in use — is Vessel already running?
  Change "listen" in vessel.json or pass --config.` Exit code 1, no stack trace.
- **DB locked/unwritable at startup**: named error with the db path and likely
  causes (second instance, permissions), no stack trace.
- **Malformed config**: already handled (error + exit); verify message quality in
  the gate.
- `--help` output: usage, `--config`, `--version`, config-file location rules, UI
  URL. Kept to one screen.
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
your network can read captured prompts{ and query MCP}") plus a startup log Warning.
Banner styling per ui-spec (`--warn` tinted fill, full-width strip above the header
panel — ui-spec gains the pattern in the same change). Loopback binds: nothing.

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

SemVer from `v0.1.0` (S2). Tag drives everything (csproj `Version` from the tag in
release builds; dev builds show `0.0.0-dev+{sha}`). `CHANGELOG.md` maintained by
hand per release — the repo's phase reports are the raw material, the changelog is
the user-facing distillation.

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

## 3. New/changed layout

```
LICENSE  CHANGELOG.md  CONTRIBUTING.md  THIRD-PARTY-NOTICES.md  README.md
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
   native smokes green** (or a RID consciously flipped untrimmed per D1, recorded).
3. **The literal two-minute test, performed fresh**: on a machine/VM with only
   Ollama, download the artifact, follow only the README quickstart, first captured
   request visible in the UI. Timed. Under two minutes or the README gets fixed.
4. Failure-mode checks: port-in-use, locked DB, malformed config — each produces
   its D4 message, no stack traces.
5. Bind `0.0.0.0` → banner + log warning; loopback → neither.
6. Live-key verification finally run: `verify.ps1 -OpenAI -Anthropic` (the item
   pending since Phase 0), plus one replay against each live API.
7. Human items: the Compare GIF + screenshots recorded; S1 name check done; repo
   made public; tag `v0.1.0` pushed; release published from the draft.

## 5. Acceptance

Gate items 1–6 automated/agent-verifiable and done; item 7's human steps checklist
handed to the author. plan.md Phase 6 ticked; deviations recorded here.
