#!/usr/bin/env python3
"""
Zero-dependency mock REST API for exercising the dynamic-options / resource-locator feature.

It exposes clean `GET /collection` + `GET /collection/{id}` pairs (so the spec-inference
auto-detect fires) plus a nested store->pets resource (so cascading dependent options can be
demonstrated). No auth, no external deps — just the Python standard library.

Run:   python dev/mock-api/server.py            # listens on 127.0.0.1:8787
Then:  import dev/mock-api/openapi.json into the app, create a Server Config with
       BaseUrl http://127.0.0.1:8787 (use the IP, NOT 'localhost'), security 'none'.

See dev/mock-api/README.md for the full walkthrough (including the egress-policy toggle).
"""
import json
import os
import re
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

HOST = "127.0.0.1"
PORT = 8787

SPEC_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "openapi.json")

# ── In-memory data ───────────────────────────────────────────────────────────
STORES = [
    {"id": "store_paris", "name": "Paris Boutique", "country": "FR"},
    {"id": "store_berlin", "name": "Berlin Shop", "country": "DE"},
]

PETS = [
    {"id": "pet_fluffy", "name": "Fluffy", "species": "cat", "storeId": "store_paris"},
    {"id": "pet_rex", "name": "Rex", "species": "dog", "storeId": "store_paris"},
    {"id": "pet_nemo", "name": "Nemo", "species": "fish", "storeId": "store_berlin"},
    {"id": "pet_yoshi", "name": "Yoshi", "species": "lizard", "storeId": "store_berlin"},
]


# ── Routing ──────────────────────────────────────────────────────────────────
def route(method, path):
    """Return (status, payload) for a method+path, or (404, error)."""
    # Optional ?search= filter applied to name, to exercise server-side search.
    path, _, query = path.partition("?")
    search = None
    for part in query.split("&"):
        if part.startswith("search="):
            search = part[len("search="):].lower()

    def by_name(items):
        if not search:
            return items
        return [i for i in items if search in i["name"].lower()]

    if method == "GET":
        if path == "/" or path == "":
            return 200, {
                "service": "mock-api",
                "hint": "Import the OpenAPI spec at GET /openapi.json into the app (or upload dev/mock-api/openapi.json).",
                "routes": [
                    "/pets", "/pets/{petId}",
                    "/stores", "/stores/{storeId}",
                    "/stores/{storeId}/pets", "/stores/{storeId}/pets/{petId}",
                    "POST /stores/{storeId}/pets/{petId}/adopt",
                    "/openapi.json",
                ],
            }
        if path == "/openapi.json":
            try:
                with open(SPEC_PATH, "r", encoding="utf-8") as f:
                    return 200, json.load(f)
            except OSError as ex:
                return 500, {"error": f"could not read openapi.json: {ex}"}

        if path == "/pets":
            return 200, by_name(PETS)
        m = re.fullmatch(r"/pets/([^/]+)", path)
        if m:
            return _one(PETS, m.group(1))

        if path == "/stores":
            return 200, by_name(STORES)
        m = re.fullmatch(r"/stores/([^/]+)", path)
        if m:
            return _one(STORES, m.group(1))

        m = re.fullmatch(r"/stores/([^/]+)/pets", path)
        if m:
            store_id = m.group(1)
            return 200, by_name([p for p in PETS if p["storeId"] == store_id])
        m = re.fullmatch(r"/stores/([^/]+)/pets/([^/]+)", path)
        if m:
            store_id, pet_id = m.group(1), m.group(2)
            pet = next((p for p in PETS if p["id"] == pet_id and p["storeId"] == store_id), None)
            return (200, pet) if pet else (404, {"error": f"pet '{pet_id}' not found in store '{store_id}'"})

    if method == "POST":
        # A write that consumes a resolved store + pet id — mirrors the run-time side effect.
        m = re.fullmatch(r"/stores/([^/]+)/pets/([^/]+)/adopt", path)
        if m:
            store_id, pet_id = m.group(1), m.group(2)
            pet = next((p for p in PETS if p["id"] == pet_id and p["storeId"] == store_id), None)
            if not pet:
                return 404, {"error": f"pet '{pet_id}' not found in store '{store_id}'"}
            return 200, {"adopted": pet["id"], "store": store_id, "status": "confirmed"}

    return 404, {"error": f"no route for {method} {path}"}


def _one(items, item_id):
    found = next((i for i in items if i["id"] == item_id), None)
    return (200, found) if found else (404, {"error": f"'{item_id}' not found"})


class Handler(BaseHTTPRequestHandler):
    def _send(self, status, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        status, payload = route("GET", self.path)
        self._send(status, payload)

    def do_POST(self):
        status, payload = route("POST", self.path)
        self._send(status, payload)

    def log_message(self, fmt, *args):
        print(f"[mock-api] {self.command} {self.path} -> {args[1] if len(args) > 1 else ''}")


if __name__ == "__main__":
    print(f"Mock API listening on http://{HOST}:{PORT}  (use this IP, not 'localhost')")
    print("Routes: /pets, /pets/{id}, /stores, /stores/{id}, /stores/{id}/pets, /stores/{id}/pets/{petId}")
    ThreadingHTTPServer((HOST, PORT), Handler).serve_forever()
