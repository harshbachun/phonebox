"""Serve the staged A52 APK's panel with the official release's simulated desktop data."""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import mimetypes
from pathlib import Path
import subprocess
from urllib.parse import urlsplit
from zipfile import ZipFile

ROOT = Path(__file__).resolve().parents[1]
TAG = "v0.9.28"


def resources(apk):
    with ZipFile(apk) as archive:
        assets = {"/" + name.removeprefix("assets/www/"): archive.read(name)
                  for name in archive.namelist() if name.startswith("assets/www/") and not name.endswith("/")}
    demo = subprocess.check_output(["git", "show", TAG + ":tools/pages/demo.js"], cwd=ROOT)
    assets["/demo.js"] = demo
    assets["/a52-sample.js"] = Path(__file__).with_name("a52-sample.js").read_bytes()
    for name in ("templates.json", "widgets.json"):
        assets["/" + name] = (ROOT / "agent/PhoneBox.Agent" / name).read_bytes()
    marker = b'<script type="module" src="main.js"></script>'
    html = assets["/index.html"]
    if html.count(marker) != 1:
        raise ValueError("Unexpected panel entry point in APK")
    assets["/"] = assets["/index.html"] = html.replace(marker,
        b'<script src="demo.js" data-version="0.9.28-a52" data-panel></script>\n'
        b'<script src="a52-sample.js"></script>\n' + marker)
    return assets


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, default=ROOT / "android/app/build/outputs/apk/debug/app-debug.apk")
    parser.add_argument("--port", type=int, default=8798)
    args = parser.parse_args()
    assets = resources(args.apk)

    class Preview(BaseHTTPRequestHandler):
        def do_GET(self):
            path = urlsplit(self.path).path
            if path not in assets:
                self.send_error(404)
                return
            content = assets[path]
            kind = "text/javascript" if path.endswith(".js") else mimetypes.guess_type(path)[0]
            self.send_response(200)
            self.send_header("Content-Type", kind or "text/html")
            self.send_header("Content-Length", str(len(content)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(content)

    server = ThreadingHTTPServer(("127.0.0.1", args.port), Preview)
    print(f"A52 sample-data preview: http://127.0.0.1:{args.port}/", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
