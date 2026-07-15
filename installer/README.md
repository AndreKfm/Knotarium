# Distribution

Knotarium ships as a self-contained Windows build via three channels. All artifacts are produced
by [`publish.ps1`](../publish.ps1) from the folder build in `publish/app`.

## Channels

| Channel | Artifact | How the user runs it |
|---|---|---|
| Zip (zero-install) | `Knotarium-<ver>-win-x64.zip` | Extract, double-click `Start.bat` (runs on demand, no admin) |
| Installer | `Knotarium-<ver>-setup.exe` | Installs to Program Files, registers + starts the `Knotarium` **Windows service** |
| winget | (points at the installer above) | `winget install AndreKaufmann.Knotarium` |

## Building locally

```powershell
# Zip only
./publish.ps1 -Version 1.0.0-rc.1 -Zip

# Zip + installer (needs Inno Setup 6 — winget install JRSoftware.InnoSetup)
./publish.ps1 -Version 1.0.0-rc.1 -Zip -Installer
```

Each artifact is emitted alongside a `.sha256` sidecar. Outputs land in `publish/`.

## Installer behaviour

- Installs the published folder to `%ProgramFiles%\Knotarium`.
- Registers a Windows service named **Knotarium** (auto-start), bound to `http://localhost:43120`
  via `--urls`, and starts it. (Matches the Docker/docs port; overridable with `publish.ps1 -Port`.)
- Start Menu: **Open Knotarium** (opens the browser) + **Uninstall Knotarium**.
- On upgrade the existing service is stopped/removed before files are replaced, then recreated.
- On uninstall the service is stopped and deleted, but `%ProgramData%\Knotarium` (DB + credential
  key) is **left intact** so a reinstall keeps state. Delete it manually for a clean wipe.
- Silent install (used by winget): `Knotarium-<ver>-setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`

## Release flow (GitHub Actions)

Pushing a `v*` tag triggers [`.github/workflows/release.yml`](../.github/workflows/release.yml),
which builds the zip + installer on `windows-latest` and attaches them (with `.sha256` sidecars) to
a GitHub Release. Hyphenated tags (e.g. `v1.0.0-rc.1`) are marked pre-release.

```bash
git tag v1.0.0-rc.1
git push origin v1.0.0-rc.1
```

## winget submission

The manifest in [`winget/`](winget/) targets `AndreKaufmann.Knotarium`. Per release:

1. Publish the GitHub Release (above) so the installer URL resolves.
2. Update `PackageVersion` + `InstallerUrl` in all three YAML files.
3. Paste the installer's SHA-256 into `InstallerSha256` (from the `.sha256` sidecar / release asset).
4. Fill in `License` / `LicenseUrl` in the locale file.
5. Validate and submit:
   ```powershell
   winget validate --manifest installer/winget
   # optional local install test (elevated):
   winget install --manifest installer/winget
   # then open a PR to microsoft/winget-pkgs (wingetcreate submit does this for you)
   ```

Note: the community `winget-pkgs` repo discourages pre-releases in the main index; you may prefer to
keep `rc.*` on the GitHub Release + zip channels and submit to winget on the first stable tag.

## Not yet done: code signing

All artifacts are currently **unsigned**, so Windows SmartScreen and some AV will warn on first run.
This is acceptable for an rc.* pre-release. For a stable public release, sign `Knotarium.Api.exe`
(before it is packed) and `*-setup.exe`. Options:

- **SignPath Foundation** — free code signing for open-source projects. Knotarium's Apache-2.0
  license + CI build make it eligible, but it requires an application + eligibility review (they
  favour projects with some traction), so plan for it around 1.0 rather than rc.1.
- **Azure Trusted Signing** — ~€10/month, fully managed (no cert/key to hold).
- A traditional OV/EV certificate (~€200+/yr).

Either way, wire a `-Sign` step into `publish.ps1` (after the installer build) and store the signing
credentials as GitHub Actions secrets used by `release.yml`. Note: OV-class signing removes the
"unknown publisher" prompt but SmartScreen reputation still builds over download volume.
