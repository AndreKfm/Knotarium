# Running Knotarium on Linux as a systemd service

This runs the **copy-and-run folder build** (the release zip) as a hardened background service on any
systemd-based distribution. It's the no-Docker path — for Docker, use `docker compose up` from the repo
root instead (see [`docs/install.md`](../../docs/install.md)).

The service and its data survive reboots, run as a dedicated non-root user, and share the same state
directory as any interactive run of the **same version** — exactly like the Windows service install.

## Prerequisites

- A systemd-based Linux distribution (Ubuntu, Debian, Fedora, …), x64.
- A Knotarium **Linux folder build**. Two ways to get one:
  - **Release zip** from the [Releases page](https://github.com/AndreKfm/Knotarium/releases) — a
    self-contained build with **no separate .NET runtime to install**. *(Note: the automated release
    currently publishes Windows artifacts only; until a Linux asset is attached, use the source build
    below.)*
  - **Build from source** (needs the .NET SDK once, on a build machine):
    ```bash
    # UI, then a self-contained Linux publish that bundles the runtime:
    npm --prefix Frontend ci && npm --prefix Frontend run build
    dotnet publish Backend/Knotarium.Api/Knotarium.Api.csproj -c Release \
      -r linux-x64 --self-contained \
      -p:DebugType=none -o ./knotarium-linux
    cp -r Frontend/dist ./knotarium-linux/wwwroot   # same-origin SPA
    ```
    Then zip/copy `./knotarium-linux` to the target host and use it as the install dir below. Keep it
    **framework-dependent-free but non-trimmed / non-AOT** as shown — the engine JIT-compiles nodes
    with Roslyn at runtime, which trimming/AOT would break.

## Install

```bash
# 1. Dedicated, unprivileged service account (no login shell, no home login).
sudo useradd --system --home /var/lib/knotarium --shell /usr/sbin/nologin knotarium

# 2. Lay out the install dir and the persistent state dir.
sudo mkdir -p /opt/knotarium /var/lib/knotarium

# 3. Unzip the release into /opt/knotarium (adjust the filename to what you downloaded).
sudo unzip Knotarium-<version>-linux-x64.zip -d /opt/knotarium

# 4. Make the binary executable and hand ownership to the service user.
sudo chmod +x /opt/knotarium/Knotarium.Api
sudo chown -R knotarium:knotarium /opt/knotarium /var/lib/knotarium

# 5. Install and start the service.
sudo cp deploy/linux/knotarium.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now knotarium
```

Open **http://localhost:43120** and create your admin account on first run.

```bash
systemctl status knotarium      # health
journalctl -u knotarium -f      # live logs (Knotarium logs to the journal)
```

## Configuration

Edit environment in the unit (`/etc/systemd/system/knotarium.service`) or, cleaner, drop an override:

```bash
sudo systemctl edit knotarium   # creates /etc/systemd/system/knotarium.service.d/override.conf
```

Common knobs (see the unit file for the full list):

| Setting | Env var | Default |
|---|---|---|
| Listen address / port | `ASPNETCORE_URLS` | `http://localhost:43120` |
| Data directory (DB + credential key) | `Storage__DataDirectory` | `/var/lib/knotarium` |
| Require login | `Auth__Enabled` | `true` (first run creates the admin) |
| Bring-your-own at-rest key | `Security__Credentials__EncryptionKeyBase64` | auto-generated onto the data dir |
| Node code sandbox mode | `Security__Sandbox__Mode` | `InProcess` |

For secrets (the encryption key), prefer an `EnvironmentFile` over inlining them in the unit:

```bash
sudo install -d -m 0750 -o knotarium -g knotarium /etc/knotarium
printf 'Security__Credentials__EncryptionKeyBase64=%s\n' "$(openssl rand -base64 32)" \
  | sudo tee /etc/knotarium/knotarium.env >/dev/null
sudo chmod 0640 /etc/knotarium/knotarium.env
sudo chown root:knotarium /etc/knotarium/knotarium.env
# then uncomment `EnvironmentFile=/etc/knotarium/knotarium.env` in the unit and:
sudo systemctl daemon-reload && sudo systemctl restart knotarium
```

## Node-code sandbox on Linux

If you enable the isolated node sandbox (`Security__Sandbox__Mode=Process`, in the app under
**Settings → Sandbox**), on Linux it confines worker processes with **cgroups v2**
(`memory.max` / `cpu.max` / `pids.max`) when a delegated, writable cgroup hierarchy is available,
falling back to `prlimit(RLIMIT_AS)` otherwise. CPU-percent capping specifically needs cgroup
delegation. Under systemd you can grant it per service:

```ini
# in an override.conf
[Service]
Delegate=yes
```

The Windows-only `RestrictedToken` option is a no-op on Linux; the hard kill-on-timeout and per-worker
process isolation work on both platforms regardless.

## Upgrade

```bash
sudo systemctl stop knotarium
# replace /opt/knotarium with the new release (keep /var/lib/knotarium untouched — that's your data)
sudo rm -rf /opt/knotarium/* && sudo unzip Knotarium-<new-version>-linux-x64.zip -d /opt/knotarium
sudo chmod +x /opt/knotarium/Knotarium.Api
sudo chown -R knotarium:knotarium /opt/knotarium
sudo systemctl start knotarium
```

The database schema is created/migrated on startup, so a version bump needs no manual migration step.

## Uninstall

```bash
sudo systemctl disable --now knotarium
sudo rm /etc/systemd/system/knotarium.service
sudo systemctl daemon-reload
sudo rm -rf /opt/knotarium
# Keeps your data. To wipe it too:
# sudo rm -rf /var/lib/knotarium && sudo userdel knotarium
```

## The hardening in the unit

The unit applies systemd sandboxing as defence-in-depth (independent of the app's own node sandbox):
`NoNewPrivileges`, `ProtectSystem=strict` with a single `ReadWritePaths` for the state dir,
`ProtectHome`, `PrivateTmp`, `PrivateDevices`, kernel/cgroup protections, and an `AF_INET`/`AF_INET6`/
`AF_UNIX`-only address-family restriction. One deliberate exception: **`MemoryDenyWriteExecute` is left
`false`** because the engine JIT-compiles inline/dynamic node code with Roslyn at runtime, which needs
writable-then-executable memory — enabling it would break those features.
