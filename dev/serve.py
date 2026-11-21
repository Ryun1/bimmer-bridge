#!/usr/bin/env python3
"""Mac-side deploy/discovery server for ediabas-bridge.

Implements the routes in docs/deploy.md and nothing else:

  GET /discover.ps1, /install.ps1, /update.ps1, /uninstall.ps1   files from vm-setup/
  GET /EdiabasAgent.zip                                           release/EdiabasAgent.zip
  PUT /upload/<name>                                              saved into discovery/
  PUT /token                                                      only with --accept-token

Standard library only. Run from anywhere:  python3 dev/serve.py [--bind IP] [--port N]
"""

import argparse
import hashlib
import http.server
import ipaddress
import json
import re
import subprocess
import sys
import threading
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

DEFAULT_PORT = 8000
UPLOAD_LIMIT = 20 * 1024 * 1024  # 20 MB
TOKEN_BODY_LIMIT = 1024
CHUNK = 64 * 1024

UPLOAD_NAME_RE = re.compile(r"[A-Za-z0-9._-]{1,80}")
TOKEN_RE = re.compile(r"[A-Za-z0-9_-]{43}")
CONTENT_LENGTH_RE = re.compile(r"[0-9]{1,15}")
BRIDGE_IFACE_RE = re.compile(r"bridge1[0-9][0-9]")

SCRIPT_NAMES = ("discover.ps1", "install.ps1", "update.ps1", "uninstall.ps1")
ZIP_NAME = "EdiabasAgent.zip"

KEYCHAIN_SERVICE = "ediabas-bridge"
KEYCHAIN_ACCOUNT = "agent"
SECURITY_TOOL = "/usr/bin/security"


# --------------------------------------------------------------------------- bind address

def validate_bind(host):
    """Return the normalised IPv4 address, or raise ValueError.

    Refuses empty hosts, hostnames, IPv6, 0.0.0.0 and any other wildcard/broadcast address.
    """
    if host is None:
        raise ValueError("no bind address given")
    host = str(host).strip()
    if not host:
        raise ValueError("empty bind address refused")
    try:
        addr = ipaddress.IPv4Address(host)
    except ValueError:
        raise ValueError("bind address must be a specific IPv4 address, got %r" % host)
    if addr.is_unspecified:
        raise ValueError("refusing to bind to the wildcard address %s" % addr)
    if addr == ipaddress.IPv4Address("255.255.255.255") or addr.is_multicast:
        raise ValueError("refusing to bind to broadcast/multicast address %s" % addr)
    return str(addr)


def find_bridge_address(ifconfig_text):
    """Parse macOS `ifconfig` output; return the IPv4 of an UP bridge1xx interface, or None."""
    candidates = {}
    iface = None
    up = False
    for line in ifconfig_text.splitlines():
        header = re.match(r"^(\S+?):\s+flags=[0-9a-fA-F]+<([^>]*)>", line)
        if header:
            iface = header.group(1)
            up = "UP" in header.group(2).split(",")
            continue
        if line[:1] not in (" ", "\t"):
            iface = None
            continue
        inet = re.match(r"^\s+inet\s+([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)\b", line)
        if inet and iface and up and BRIDGE_IFACE_RE.fullmatch(iface):
            try:
                ip = validate_bind(inet.group(1))
            except ValueError:
                continue
            candidates.setdefault(iface, ip)
    if not candidates:
        return None
    return candidates[sorted(candidates)[0]]


def detect_bridge_address():
    try:
        out = subprocess.run(["/sbin/ifconfig"], capture_output=True, text=True, timeout=10).stdout
    except (OSError, subprocess.SubprocessError):
        return None
    return find_bridge_address(out)


def client_allowed(bind, client):
    """Clients must be on the bind address's /24; a loopback bind accepts loopback only."""
    try:
        b = ipaddress.IPv4Address(bind)
        c = ipaddress.ip_address(client)
    except ValueError:
        return False
    if isinstance(c, ipaddress.IPv6Address):
        if c.ipv4_mapped is None:
            return False
        c = c.ipv4_mapped
    if b.is_loopback:
        return c.is_loopback
    return c in ipaddress.IPv4Network("%s/24" % b, strict=False)


# --------------------------------------------------------------------------- keychain

def store_token_in_keychain(token):
    """Store the token with `security`, never placing it on a command line."""
    command = "add-generic-password -U -s %s -a %s -w %s\n" % (
        KEYCHAIN_SERVICE, KEYCHAIN_ACCOUNT, token)
    result = subprocess.run([SECURITY_TOOL, "-i"], input=command, capture_output=True,
                            text=True, timeout=30)
    if result.returncode != 0:
        raise RuntimeError("security -i exited with %d" % result.returncode)
    check = subprocess.run([SECURITY_TOOL, "find-generic-password",
                            "-s", KEYCHAIN_SERVICE, "-a", KEYCHAIN_ACCOUNT],
                           capture_output=True, timeout=30)
    if check.returncode != 0:
        raise RuntimeError("keychain item not found after storing")


# --------------------------------------------------------------------------- config / server

class Config:
    def __init__(self, bind, port=DEFAULT_PORT, setup_dir=None, release_dir=None,
                 discovery_dir=None, accept_token=False, store_token=None, out=None,
                 upload_limit=UPLOAD_LIMIT):
        self.bind = bind
        self.port = port
        self.setup_dir = Path(setup_dir) if setup_dir else REPO_ROOT / "vm-setup"
        self.release_dir = Path(release_dir) if release_dir else REPO_ROOT / "release"
        self.discovery_dir = Path(discovery_dir) if discovery_dir else REPO_ROOT / "discovery"
        self.accept_token = accept_token
        self.store_token = store_token or store_token_in_keychain
        self.out = out
        self.upload_limit = upload_limit


class Server(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, config):
        config.bind = validate_bind(config.bind)
        self.config = config
        self.token_lock = threading.Lock()
        self.token_stored = False
        self.log_lock = threading.Lock()
        super().__init__((config.bind, config.port), Handler)

    def log(self, line):
        out = self.config.out or sys.stdout
        with self.log_lock:
            out.write(line + "\n")
            out.flush()


def make_server(config):
    return Server(config)


def _printable(text, limit=200):
    return "".join(ch if 32 <= ord(ch) < 127 else "?" for ch in text)[:limit]


class Handler(http.server.BaseHTTPRequestHandler):
    server_version = "ediabas-bridge-serve"
    sys_version = ""
    timeout = 60  # socket timeout, so a stalled client cannot hold a thread forever

    # -- logging --------------------------------------------------------------------------
    def log_message(self, format, *args):  # silence the default stderr logging
        pass

    def log_error(self, format, *args):
        pass

    def _log(self, status, nbytes=None, note=None):
        now = datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
        parts = [now, self.client_address[0], getattr(self, "command", "-") or "-",
                 _printable(getattr(self, "path", "-") or "-"), str(status)]
        if note is not None:
            parts.append(note)
        else:
            parts.append(str(nbytes if nbytes is not None else 0))
        self.server.log(" ".join(parts))

    # -- responses ------------------------------------------------------------------------
    def _send(self, status, body=b"", ctype="text/plain; charset=utf-8", note=None,
              logged_bytes=None):
        self.close_connection = True
        try:
            self.send_response(status)
            if status != 204:
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(body)))
            self.send_header("Connection", "close")
            self.end_headers()
            if body and status != 204:
                self.wfile.write(body)
        except OSError:
            pass
        self._log(status, logged_bytes if logged_bytes is not None else len(body), note)

    def _error(self, status, message, note=None):
        self._send(status, (message + "\n").encode("utf-8"), note=note)

    def _check_client(self):
        if client_allowed(self.server.config.bind, self.client_address[0]):
            return True
        self._error(403, "forbidden: client not on the local network")
        return False

    def _content_length(self, limit, note=None):
        """Return the declared body length, or None after sending an error response."""
        if self.headers.get("Transfer-Encoding") is not None:
            self._error(411, "Transfer-Encoding not supported; send Content-Length", note)
            return None
        raw = self.headers.get("Content-Length")
        if raw is None:
            self._error(411, "Content-Length required", note)
            return None
        raw = raw.strip()
        if not CONTENT_LENGTH_RE.fullmatch(raw):
            self._error(400, "bad Content-Length", note)
            return None
        length = int(raw)
        if length > limit:
            self._error(413, "body too large (limit %d bytes)" % limit, note)
            return None
        return length

    # -- methods --------------------------------------------------------------------------
    def do_GET(self):
        if not self._check_client():
            return
        cfg = self.server.config
        path = self.path
        if path.startswith("/") and path[1:] in SCRIPT_NAMES:
            target = cfg.setup_dir / path[1:]
            ctype = "text/plain; charset=utf-8"
        elif path == "/" + ZIP_NAME:
            target = cfg.release_dir / ZIP_NAME
            ctype = "application/zip"
        else:
            self._error(404, "not found")
            return
        try:
            f = open(target, "rb")
        except OSError:
            self._error(404, "not found")
            return
        sent = 0
        status = 200
        with f:
            try:
                size = Path(target).stat().st_size
                self.close_connection = True
                self.send_response(200)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(size))
                self.send_header("Connection", "close")
                self.end_headers()
                while True:
                    chunk = f.read(CHUNK)
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    sent += len(chunk)
            except OSError:
                status = 200  # response already started; just record what was sent
        self._log(status, sent)

    def do_PUT(self):
        if not self._check_client():
            return
        if self.path == "/token":
            self._put_token()
            return
        if not self.path.startswith("/upload/"):
            self._error(404, "not found")
            return
        name = self.path[len("/upload/"):]
        if not UPLOAD_NAME_RE.fullmatch(name):
            self._error(400, "bad upload name")
            return
        self._put_upload(name)

    def _reject_method(self):
        if not self._check_client():
            return
        self._error(405, "method not allowed")

    do_POST = do_DELETE = do_HEAD = do_PATCH = do_OPTIONS = _reject_method

    # -- uploads --------------------------------------------------------------------------
    def _put_upload(self, name):
        cfg = self.server.config
        length = self._content_length(cfg.upload_limit)
        if length is None:
            return
        stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
        filename = "%s-%s" % (stamp, name)
        try:
            cfg.discovery_dir.mkdir(parents=True, exist_ok=True)
            target = cfg.discovery_dir / filename
            f = open(target, "xb")
        except FileExistsError:
            self._error(409, "an upload with that name was just saved; retry in a second")
            return
        except OSError:
            self._error(500, "cannot create upload file")
            return
        received = 0
        ok = False
        try:
            with f:
                remaining = length
                while remaining > 0:
                    chunk = self.rfile.read(min(CHUNK, remaining))
                    if not chunk:
                        break
                    received += len(chunk)
                    remaining -= len(chunk)
                    if received > cfg.upload_limit:  # defensive; cannot exceed length
                        break
                    f.write(chunk)
            ok = received == length
        except OSError:
            ok = False
        if not ok:
            try:
                target.unlink()
            except OSError:
                pass
            self._error(400, "incomplete body", note=None)
            return
        body = json.dumps({"saved": filename, "bytes": received}).encode("utf-8")
        self._send(201, body, ctype="application/json", logged_bytes=received)

    # -- token ----------------------------------------------------------------------------
    def _put_token(self):
        cfg = self.server.config
        rejected = "token rejected"
        if not cfg.accept_token:
            self._error(403, "token upload not enabled (start serve.py with --accept-token)",
                        note=rejected)
            return
        with self.server.token_lock:
            if self.server.token_stored:
                self._error(403, "a token was already stored in this run", note=rejected)
                return
            length = self._content_length(TOKEN_BODY_LIMIT, note=rejected)
            if length is None:
                return
            try:
                body = self.rfile.read(length)
            except OSError:
                body = b""
            if len(body) != length:
                self._error(400, "incomplete body", note=rejected)
                return
            try:
                token = body.decode("ascii").strip()
            except UnicodeDecodeError:
                token = ""
            if not TOKEN_RE.fullmatch(token):
                self._error(400, "bad token format", note=rejected)
                return
            try:
                cfg.store_token(token)
            except Exception:
                self._error(500, "could not store the token in the Keychain",
                            note="token rejected (keychain store failed)")
                return
            self.server.token_stored = True
        self._send(204, note="token stored")


# --------------------------------------------------------------------------- startup

def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(CHUNK), b""):
            h.update(chunk)
    return h.hexdigest()


def startup_lines(bind, port, release_dir, accept_token=False):
    base = "http://%s:%d" % (bind, port)
    lines = [
        "ediabas-bridge serve.py listening on %s:%d (Ctrl-C to stop)" % (bind, port),
        "",
        "Discovery - paste into PowerShell in the VM:",
        "  $base='%s'; irm $base/discover.ps1 | iex" % base,
    ]
    zip_path = Path(release_dir) / ZIP_NAME
    if zip_path.is_file():
        sha = sha256_file(zip_path)
        lines += [
            "",
            "Install:",
            "  $base='%s'; $sha='%s'; irm $base/install.ps1 | iex" % (base, sha),
            "Update:",
            "  $base='%s'; $sha='%s'; irm $base/update.ps1 | iex" % (base, sha),
        ]
    else:
        lines += ["", "(release/%s not built; install/update lines not shown)" % ZIP_NAME]
    if accept_token:
        lines += ["", "Accepting one agent token on PUT /token for this run."]
    lines.append("")
    return lines


def main(argv=None):
    parser = argparse.ArgumentParser(description="ediabas-bridge deploy/discovery server")
    parser.add_argument("--bind", help="IPv4 address to bind (default: UTM bridge1xx address)")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="port (default 8000)")
    parser.add_argument("--accept-token", action="store_true",
                        help="accept one agent token on PUT /token and store it in the Keychain")
    args = parser.parse_args(argv)

    if not 1 <= args.port <= 65535:
        print("error: --port must be between 1 and 65535", file=sys.stderr)
        return 2

    if args.bind is not None:
        try:
            bind = validate_bind(args.bind)
        except ValueError as e:
            print("error: %s" % e, file=sys.stderr)
            return 2
    else:
        bind = detect_bridge_address()
        if bind is None:
            print("error: no UTM shared-network interface (bridge1xx) with an IPv4 address is "
                  "up. Start the VM first, then run serve.py again (or pass --bind).",
                  file=sys.stderr)
            return 1

    config = Config(bind=bind, port=args.port, accept_token=args.accept_token)
    try:
        server = make_server(config)
    except OSError as e:
        print("error: cannot listen on %s:%d: %s" % (bind, args.port, e), file=sys.stderr)
        return 1

    host, port = server.server_address[:2]
    for line in startup_lines(host, port, config.release_dir, config.accept_token):
        print(line)
    sys.stdout.flush()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nstopped")
    finally:
        server.server_close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
