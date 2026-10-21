"""A sample Tuvima plugin written in Python, using only the standard library.

The Library starts this program and tells it three things through the environment:
  TUVIMA_PLUGIN_PORT  the port to listen on (on 127.0.0.1)
  TUVIMA_PLUGIN_KEY   this plugin's own key for the plugin door
  TUVIMA_LIBRARY_URL  where the plugin door is (https://.../plugin-host/v1/...)

It answers GET /health (the Library asks every few seconds) and GET /lookup?title=... (a metadata lookup).
GET /probe shows what the plugin door allows: it asks for things this plugin was and was not approved for.
"""
import json
import os
import urllib.error
import urllib.parse
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(os.environ["TUVIMA_PLUGIN_PORT"])
KEY = os.environ["TUVIMA_PLUGIN_KEY"]
LIBRARY = os.environ.get("TUVIMA_LIBRARY_URL", "")
DOOR = "/plugin-host/v1"


def call_door(method, path, body=None):
    """Calls the Library's plugin door as this plugin and returns the HTTP status."""
    if not LIBRARY:
        return 0
    data = None if body is None else json.dumps(body).encode()
    request = urllib.request.Request(LIBRARY + DOOR + path, data=data, method=method)
    request.add_header("X-Tuvima-Plugin-Key", KEY)
    request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code


class Handler(BaseHTTPRequestHandler):
    def log_message(self, format, *args):  # keep the Library's log quiet
        pass

    def reply(self, status, payload):
        body = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        url = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(url.query)
        if url.path == "/health":
            return self.reply(200, {"status": "ok"})

        # Everything else is for the Library only: it sends the key it gave this program.
        if self.headers.get("X-Tuvima-Plugin-Key") != KEY:
            return self.reply(401, {"error": "Only the Library may ask this program."})

        if url.path == "/lookup":
            title = (query.get("title") or [""])[0]
            return self.reply(200, {
                "source": "python-sample",
                "pid": os.getpid(),
                "title": title,
                "results": [{"title": title, "year": 1965, "note": "A made-up answer from the Python sample."}],
            })

        if url.path == "/probe":
            return self.reply(200, {
                "whoami": call_door("GET", "/whoami"),
                "save_own_record": call_door("PUT", "/records/notes/first", {"hello": "world"}),
                "use_local_ai": call_door("POST", "/ai/text", {"role": "text_fast", "prompt": "Say hi."}),
            })

        return self.reply(404, {"error": "No such path."})


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
