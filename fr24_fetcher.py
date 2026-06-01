"""
FlightRadar24 fetcher sidecar (Zendriver).

The raw FlightRadar24 endpoints reject plain HTTP clients (HTTP 302/403), so this
helper drives a real headless Chrome session with Zendriver and performs the FR24
requests from inside an authenticated flightradar24.com page (cross-origin fetch
with web-security disabled). It exposes a tiny localhost HTTP API the C# app calls
instead of talking to FlightRadar24 directly:

    GET /health            -> 200 "ok" once the browser session is ready
    GET /fetch?url=<FR24 url> -> the JSON body returned by FlightRadar24

Usage:
    python fr24_fetcher.py --port 8743 --chrome "C:\\path\\to\\chrome.exe"
"""
import argparse
import asyncio
import json
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import zendriver as zd

ALLOWED_HOSTS = (
    "data-cloud.flightradar24.com",
    "data-live.flightradar24.com",
    "www.flightradar24.com",
    "flightradar24.com",
)

WARMUP_URL = "https://www.flightradar24.com/"


class Fetcher:
    """Owns the Zendriver browser on a dedicated asyncio loop in a background thread."""

    def __init__(self, chrome_path, user_data_dir):
        self._chrome_path = chrome_path
        self._user_data_dir = user_data_dir
        self._loop = asyncio.new_event_loop()
        self._browser = None
        self._tab = None
        self._lock = asyncio.Lock()
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run_loop, daemon=True)
        self._thread.start()

    def _run_loop(self):
        asyncio.set_event_loop(self._loop)
        self._loop.run_until_complete(self._start_browser())
        self._loop.run_forever()

    async def _start_browser(self):
        args = [
            "--disable-web-security",
            "--disable-site-isolation-trials",
            "--disable-features=IsolateOrigins,site-per-process",
        ]
        kwargs = dict(headless=True, user_data_dir=self._user_data_dir, browser_args=args)
        if self._chrome_path:
            kwargs["browser_executable_path"] = self._chrome_path
        self._browser = await zd.start(**kwargs)
        await self._warmup()
        self._ready.set()

    async def _warmup(self):
        self._tab = await self._browser.get(WARMUP_URL)
        await asyncio.sleep(6)

    async def _fetch(self, url):
        js = ("fetch(%r,{credentials:'include',headers:{'accept':'application/json'}})"
              ".then(r=>r.text())" % url)
        async with self._lock:
            try:
                return await self._tab.evaluate(js, await_promise=True)
            except Exception:
                # Session may have gone stale; re-warm once and retry.
                await self._warmup()
                return await self._tab.evaluate(js, await_promise=True)

    def fetch(self, url, timeout=40):
        fut = asyncio.run_coroutine_threadsafe(self._fetch(url), self._loop)
        return fut.result(timeout=timeout)

    def wait_ready(self, timeout=120):
        return self._ready.wait(timeout)


def make_handler(fetcher):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass  # keep stdout clean for the parent process

        def _send(self, code, body, content_type="application/json"):
            data = body.encode("utf-8") if isinstance(body, str) else body
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            parsed = urllib.parse.urlparse(self.path)
            if parsed.path == "/health":
                self._send(200 if fetcher.wait_ready(0) else 503,
                           "ok" if fetcher.wait_ready(0) else "starting", "text/plain")
                return
            if parsed.path == "/fetch":
                params = urllib.parse.parse_qs(parsed.query)
                target = (params.get("url") or [""])[0]
                host = urllib.parse.urlparse(target).hostname or ""
                if host not in ALLOWED_HOSTS:
                    self._send(400, json.dumps({"error": "host not allowed", "host": host}))
                    return
                try:
                    body = fetcher.fetch(target)
                    self._send(200, body if body else "{}")
                except Exception as exc:  # noqa: BLE001
                    self._send(502, json.dumps({"error": str(exc)}))
                return
            self._send(404, json.dumps({"error": "not found"}))

    return Handler


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8743)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--chrome", default="")
    parser.add_argument("--user-data-dir", default="")
    args = parser.parse_args()

    user_data_dir = args.user_data_dir or None
    fetcher = Fetcher(args.chrome or None, user_data_dir)

    server = ThreadingHTTPServer((args.host, args.port), make_handler(fetcher))
    print("fr24_fetcher listening on http://%s:%d" % (args.host, args.port), flush=True)
    if fetcher.wait_ready(120):
        print("fr24_fetcher browser ready", flush=True)
    else:
        print("fr24_fetcher browser not ready (continuing)", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
