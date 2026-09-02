# Package manager distribution

Closes #31 (Homebrew), #32 (winget/Scoop), #50 (AUR).

[`.github/workflows/release.yml`](../.github/workflows/release.yml) always
drafts a release for review; it never publishes one itself. Publishing that
draft on GitHub fires [`.github/workflows/packages.yml`](../.github/workflows/packages.yml),
which bumps each package manager's manifest to point at the new release's
(now-public) assets. Each step is gated on a secret existing in this repo
(Settings → Secrets and variables → Actions), so this workflow is a no-op
until you add the secret for a given package manager.

| Package manager | Secret | Setup |
|---|---|---|
| Homebrew (`spenceclark/homebrew-tap`) | `TAP_PUSH_TOKEN` | A GitHub [personal access token](https://github.com/settings/tokens) (classic, `repo` scope, or a fine-grained token with contents:write + pull-requests:write on both `homebrew-tap` and `scoop-bucket`) belonging to an account with push access to both repos. The workflow opens a PR against `homebrew-tap` (per #31) rather than pushing straight to `main`. |
| Scoop (`spenceclark/scoop-bucket`) | `TAP_PUSH_TOKEN` | Same token as Homebrew — both repos are updated by the same job. Pushes straight to `main` (no PR requirement was specified for the bucket). |
| AUR (`vessel-bin`) | `AUR_SSH_PRIVATE_KEY` | See [`aur/README.md`](aur/README.md) — needs an AUR account and a registered SSH key. |
| winget (`spenceclark.Vessel`) | `WINGET_TOKEN` | A PAT with `public_repo` scope. Used by [winget-releaser](https://github.com/vedantmgoyal9/winget-releaser), which **requires the package to already have at least one manifest version merged into `microsoft/winget-pkgs`, and a `winget-pkgs` fork already present under the token's account** — it only automates *updates*, not the first submission. See "Bootstrapping winget" below before adding this secret. |

`homebrew-tap` and `scoop-bucket` are separate repos (Homebrew/Scoop convention
— a tap/bucket *is* a git repo of manifests) and already exist at
https://github.com/spenceclark/homebrew-tap and
https://github.com/spenceclark/scoop-bucket, seeded with a placeholder
`0.0.0` manifest. They start reflecting real releases as soon as
`TAP_PUSH_TOKEN` is added and a release is published.

## Bootstrapping winget

Unlike the other three, winget has no "create the repo, let the bot fill it
in" path — the first manifest has to land in `microsoft/winget-pkgs` by hand:

1. Install [`komac`](https://github.com/russellbanks/Komac) (the tool
   `winget-releaser` itself uses) or Microsoft's own `wingetcreate`.
2. Fork `microsoft/winget-pkgs` on GitHub under the account whose PAT you'll
   use as `WINGET_TOKEN`.
3. Run `komac new spenceclark.Vessel` (or `wingetcreate new`) against a real
   published release's `win-x64.zip`, following its prompts to describe the
   nested `vessel.exe`, and let it submit the PR.
4. Once that PR is merged, add `WINGET_TOKEN` — from then on, `packages.yml`
   submits an update PR for every subsequent release automatically.
