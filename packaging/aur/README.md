# AUR packaging (vessel-bin)

`PKGBUILD` here is the template the release workflow bumps and pushes to AUR.
It is **not wired up yet** — AUR publishing needs a one-time manual setup:

1. Create an account at https://aur.archlinux.org/register/.
2. Generate an SSH keypair for it (`ssh-keygen -t ed25519 -f aur`) and add the
   **public** key to your AUR account under "My Account" → SSH Public Key.
3. Create the package repo by pushing an initial commit:
   ```bash
   git clone ssh://aur@aur.archlinux.org/vessel-bin.git
   cd vessel-bin
   cp ../Vessel/packaging/aur/PKGBUILD .
   makepkg --printsrcinfo > .SRCINFO   # requires an Arch machine, or see below
   git add PKGBUILD .SRCINFO && git commit -m "Initial import" && git push
   ```
   (No Arch machine handy? `.SRCINFO` is just PKGBUILD's metadata in a plain
   key/value format — the release workflow generates it automatically for you
   on every release, so a hand-written one only needs to exist for this first
   push. `makepkg` ships in the `pacman` package; on Arch it's already there.)
4. Add the **private** key as a repo secret named `AUR_SSH_PRIVATE_KEY` in
   this repo's GitHub Actions settings (Settings → Secrets and variables →
   Actions).

Once that secret exists, the `packages` job in
[`.github/workflows/release.yml`](../../.github/workflows/release.yml) bumps
`pkgver`/checksums and pushes the update to AUR on every tagged release. Until
then, that step is skipped (not failed) so releases aren't blocked on it.
