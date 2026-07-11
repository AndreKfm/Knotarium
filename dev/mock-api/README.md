# Mock API for dynamic-options / resource-locator testing

A tiny, zero-dependency REST API (Python stdlib only) plus a matching OpenAPI spec, for
exercising the resource-locator picker end-to-end — including **auto-detection** and
**cascading dependent options**, which public APIs rarely demonstrate cleanly.

## What it serves

| Method & path | Purpose |
| --- | --- |
| `GET /pets`, `GET /pets/{petId}` | Flat collection + item → `getPet.petId` auto-detects `GET /pets`. |
| `GET /stores`, `GET /stores/{storeId}` | `getStore.storeId` auto-detects `GET /stores`. |
| `GET /stores/{storeId}/pets` | The cascading collection. |
| `GET /stores/{storeId}/pets/{petId}` | `petId` auto-detects `GET /stores/{storeId}/pets` and **depends on `storeId`**. |
| `POST /stores/{storeId}/pets/{petId}/adopt` | A write that consumes the resolved ids (run-time side effect). |

All list responses are arrays of `{ id, name, ... }`, so the loader's defaults
(`valueField: id`, `labelField: name`) work with no extra config.

## Run it

```bash
python dev/mock-api/server.py          # listens on http://127.0.0.1:8787
```

(Needs Python 3.7+. No `pip install`.)

Or launch it alongside the backend + frontend in one go:

```powershell
./run.ps1 -MockApi
```

## Make it reachable from the app (egress policy)

The backend's HTTP egress policy blocks the hostname `localhost` and, by default, all
loopback/private IPs (SSRF protection). For local testing:

1. **Use the IP, not the name** — Server Config BaseUrl must be `http://127.0.0.1:8787`
   (the literal string `localhost` is always blocked).
2. **Allow private networks in dev** — `appsettings.Development.json` already ships with
   `Security:HttpEgress:DenyPrivateNetworks: false` for this reason. Production keeps it `true`.

## End-to-end walkthrough

1. Start the mock API (above) and the app (`dotnet run` in `Backend/KnotGarden.Api`).
2. **Import the spec**: open the app's OpenAPI importer. There's no URL import — use either tab:
   - **Upload File** → select `dev/mock-api/openapi.json` from disk, or
   - **Paste** → paste the JSON (the running mock also serves it at `http://127.0.0.1:8787/openapi.json`,
     and `GET /` lists all routes).
3. **Create a Server Config**: name it `Mock`, BaseUrl `http://127.0.0.1:8787`, security `none`.
4. Add the **Mock Pet Stores** node to a workflow.
5. Pick operation **`getStorePet`** and the `Mock` server config.
   - `storeId` shows **"⚲ Auto-detected: GET /stores"** → pick a store (Paris / Berlin).
   - `petId` shows **"⚲ Auto-detected: GET /stores/{storeId}/pets"** → its list is filtered by the
     store you picked (cascading); changing the store clears and reloads it.
6. Selecting persists the stable id; at run time it flows into the request URL.

To prove fail-closed resolution / reorder-safety, edit the mock data in `server.py` (rename or
remove a pet id) and re-run the workflow.
