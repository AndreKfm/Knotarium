# Download & run

Knotarium is a single self-contained process that serves both the API and the UI; your data lives
in a local SQLite file. Pick the option that fits your setup — then head to
[Install & first workflow](/getting-started).

## Docker (recommended)

```bash
docker compose up --build
```

Open **http://localhost:43120**. On first run you create an admin account. The SQLite database **and**
the auto-generated credential-encryption key persist in the `knotarium-data` volume, so credentials
survive restarts with no extra setup.

- **Skip login for a throwaway try:** `KG_AUTH_ENABLED=false docker compose up --build`
- **Bring your own encryption key** (e.g. to share one across instances):
  `export KG_ENCRYPTION_KEY="$(openssl rand -base64 32)"` before `docker compose up`.

## Zip archive (copy-and-run folder)

For a run without Docker, download the release **zip** from the
[Releases page](https://github.com/AndreKfm/Knotarium/releases), unzip it anywhere, and start the
executable — it's a self-contained folder build, so there's no separate runtime to install.

- **Windows:** run `Knotarium.Api.exe` (or the bundled start script).
- **Linux / macOS:** run `./Knotarium.Api`.

Open the address it logs on startup (by default **http://localhost:43120**). Data — the SQLite
database and the credential key — lives in a machine-wide data directory
(`%ProgramData%\Knotarium` on Windows), so it survives restarts and is shared with a service
install of the same version. Set the port, data directory, and auth via environment variables or
`appsettings.json`.

::: tip Running as a background service
Because the data directory is machine-wide, you can register the same folder build as a
Windows service (e.g. with `sc create` / NSSM) or a Linux `systemd` unit and it will use the same
database as an interactive run.
:::

## Installer

A first-class installer (Windows service registration in a few clicks) is planned — for now the
**zip archive** above is the no-Docker path. If you want it prioritised, let us know on the
[issue tracker](https://github.com/AndreKfm/Knotarium/issues).

## Run from source

Developers can run the .NET API and the Vite dev server directly — see the
[README](https://github.com/AndreKfm/Knotarium#run-from-source).
