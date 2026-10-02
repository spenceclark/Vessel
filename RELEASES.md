# Releasing Vessel

The version comes from the git tag (MinVer reads `v*` tags at build time), so
there is no version file to bump. A release is a changelog entry, a tag, and
one click on GitHub.

## 1. Write the changelog entry

`CHANGELOG.md` is maintained by hand. Add a new `## X.Y.Z — YYYY-MM-DD`
section at the **top** of the file — the release workflow uses the first
`## ` section as the release body, so it must be the newest one.

To see what has landed since the last release:

```bash
git log --oneline "$(git describe --tags --abbrev=0)..HEAD"
```

Group entries under `### Added`, `### Changed`, `### Fixed` and reference the
PR number. Merge the entry to `main` via a PR like any other change.

## 2. Tag and push

Releases use annotated tags:

```bash
git checkout main && git pull
git tag -a vX.Y.Z -m "Vessel X.Y.Z"
git push origin vX.Y.Z
```

## 3. Wait for the Release workflow

Pushing the tag runs [`.github/workflows/release.yml`](.github/workflows/release.yml):
publishes for win-x64, linux-x64, linux-arm64 and osx-arm64, smoke-tests each
archive and the container image, pushes `ghcr.io/spenceclark/vessel:vX.Y.Z`
and `:latest`, then creates a **draft** GitHub release with the archives,
checksums, and the changelog section as its body.

If a job fails, fix it on `main`, delete the tag locally and on the remote,
and re-tag:

```bash
git tag -d vX.Y.Z && git push origin :refs/tags/vX.Y.Z
```

## 4. Publish the draft

Review the draft on GitHub and click **Publish**. Nothing is public until
this step.

## 5. Package managers

Publishing fires [`.github/workflows/packages.yml`](.github/workflows/packages.yml),
which bumps each package manager gated on its secret (see
[`packaging/README.md`](packaging/README.md)):

- **Homebrew** — opens a PR against `spenceclark/homebrew-tap`. Merge it.
- **Scoop** — pushes straight to `spenceclark/scoop-bucket`. Nothing to do.
- **AUR** — blocked: the AUR isn't accepting new account signups, so there is
  no `AUR_SSH_PRIVATE_KEY` and the step is skipped.
- **winget** — opens a PR against `microsoft/winget-pkgs` from the
  `spenceclark/winget-pkgs` fork. A moderator approves it and a bot merges it,
  usually within a couple of hours. Nothing to do unless it gets a
  `Needs-Author-Feedback` label. If the step fails, submit by hand with
  [komac](https://github.com/russellbanks/Komac) (PowerShell):

  ```powershell
  $env:GITHUB_TOKEN = gh auth token; komac update spenceclark.Vessel --version X.Y.Z --urls https://github.com/spenceclark/Vessel/releases/download/vX.Y.Z/vessel-X.Y.Z-win-x64.zip --submit
  ```

So a release currently ships as direct binaries (GitHub release assets), the
Docker image, Homebrew, Scoop, and winget.
