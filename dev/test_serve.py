"""Tests for dev/serve.py.  Run from the repo root:  python3 -m unittest dev/test_serve.py"""

import hashlib
import http.client
import io
import json
import os
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import serve  # noqa: E402

GOOD_TOKEN = "A" * 20 + "b-_" + "9" * 20  # 43 chars
SECOND_TOKEN = "Z" * 43


class ServerTestCase(unittest.TestCase):
    accept_token = False
    upload_limit = 1000

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.setup_dir = root / "vm-setup"
        self.release_dir = root / "release"
        self.discovery_dir = root / "discovery"
        self.setup_dir.mkdir()
        self.release_dir.mkdir()
        for name in serve.SCRIPT_NAMES:
            (self.setup_dir / name).write_text("# %s\nWrite-Host 'hi'\n" % name, encoding="utf-8")
        (root / "PLAN.md").write_text("secret plan", encoding="utf-8")
        self.stored = []
        self.log = io.StringIO()
        self.config = serve.Config(
            bind="127.0.0.1", port=0, setup_dir=self.setup_dir, release_dir=self.release_dir,
            discovery_dir=self.discovery_dir, accept_token=self.accept_token,
            store_token=self.stored.append, out=self.log, upload_limit=self.upload_limit)
        self.server = serve.make_server(self.config)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever,
                                       kwargs={"poll_interval": 0.05}, daemon=True)
        self.thread.start()

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(5)
        self.tmp.cleanup()

    def request(self, method, path, body=None, headers=None):
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            conn.request(method, path, body=body, headers=headers or {})
            resp = conn.getresponse()
            return resp.status, resp.getheaders(), resp.read()
        finally:
            conn.close()

    def raw_put(self, path, content_length=None, body=b""):
        """PUT with full control over Content-Length (None = header omitted)."""
        conn = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        try:
            conn.putrequest("PUT", path, skip_accept_encoding=True)
            if content_length is not None:
                conn.putheader("Content-Length", str(content_length))
            conn.endheaders()
            if body:
                conn.send(body)
            resp = conn.getresponse()
            return resp.status, resp.read()
        finally:
            conn.close()

    def uploads(self):
        if not self.discovery_dir.exists():
            return []
        return sorted(p.name for p in self.discovery_dir.iterdir())


class GetRoutes(ServerTestCase):
    def test_each_script_route(self):
        for name in serve.SCRIPT_NAMES:
            status, headers, body = self.request("GET", "/" + name)
            self.assertEqual(status, 200, name)
            self.assertEqual(dict(headers)["Content-Type"], "text/plain; charset=utf-8")
            self.assertEqual(body, (self.setup_dir / name).read_bytes())

    def test_missing_script_is_404(self):
        (self.setup_dir / "update.ps1").unlink()
        self.assertEqual(self.request("GET", "/update.ps1")[0], 404)

    def test_zip_404_when_not_built(self):
        self.assertEqual(self.request("GET", "/EdiabasAgent.zip")[0], 404)

    def test_zip_served_when_built(self):
        data = os.urandom(5000)
        (self.release_dir / "EdiabasAgent.zip").write_bytes(data)
        status, headers, body = self.request("GET", "/EdiabasAgent.zip")
        self.assertEqual(status, 200)
        self.assertEqual(dict(headers)["Content-Type"], "application/zip")
        self.assertEqual(body, data)

    def test_unknown_paths_404(self):
        for path in ["/", "/index.html", "/vm-setup/discover.ps1", "/discover.ps1/",
                     "/discover.ps1?x=1", "/DISCOVER.PS1", "/release/EdiabasAgent.zip",
                     "/upload/x.txt", "/token"]:
            self.assertEqual(self.request("GET", path)[0], 404, path)

    def test_path_traversal_refused(self):
        for path in ["/../PLAN.md", "/%2e%2e/PLAN.md", "/upload/../x", "/..%2fPLAN.md",
                     "/./discover.ps1", "/discover.ps1/../../PLAN.md"]:
            status, _, body = self.request("GET", path)
            self.assertEqual(status, 404, path)
            self.assertNotIn(b"secret plan", body)

    def test_other_methods_refused(self):
        self.assertEqual(self.request("POST", "/upload/x.txt", body=b"x")[0], 405)
        self.assertEqual(self.request("DELETE", "/discover.ps1")[0], 405)

    def test_request_logged(self):
        self.request("GET", "/discover.ps1")
        line = self.log.getvalue().strip().splitlines()[-1]
        size = (self.setup_dir / "discover.ps1").stat().st_size
        self.assertRegex(line, r"^\S+Z 127\.0\.0\.1 GET /discover\.ps1 200 %d$" % size)


class Uploads(ServerTestCase):
    def test_round_trip(self):
        data = b"report line 1\r\nline 2\n"
        status, _, body = self.request("PUT", "/upload/discovery.txt", body=data)
        self.assertEqual(status, 201)
        reply = json.loads(body)
        self.assertEqual(reply["bytes"], len(data))
        self.assertRegex(reply["saved"], r"^[0-9]{8}-[0-9]{6}-discovery\.txt$")
        self.assertEqual(self.uploads(), [reply["saved"]])
        self.assertEqual((self.discovery_dir / reply["saved"]).read_bytes(), data)

    def test_discovery_dir_created_on_demand(self):
        self.assertFalse(self.discovery_dir.exists())
        self.assertEqual(self.request("PUT", "/upload/api.h", body=b"int x;")[0], 201)
        self.assertTrue(self.discovery_dir.is_dir())

    def test_bad_names(self):
        for name in ["", "a/b", "..%2fx", "a%20b", "x" * 81, "name;rm", "%2e%2e"]:
            status, _, _ = self.request("PUT", "/upload/" + name, body=b"x")
            self.assertIn(status, (400, 404), name)
        self.assertEqual(self.uploads(), [])

    def test_traversal_upload_refused(self):
        status, _, _ = self.request("PUT", "/upload/../x", body=b"x")
        self.assertEqual(status, 400)
        status, _, _ = self.request("PUT", "/../upload/x", body=b"x")
        self.assertEqual(status, 404)
        self.assertEqual(self.uploads(), [])
        self.assertFalse((Path(self.tmp.name) / "x").exists())

    def test_max_name_length_ok(self):
        self.assertEqual(self.request("PUT", "/upload/" + "n" * 80, body=b"x")[0], 201)

    def test_at_limit_accepted(self):
        status, _, _ = self.request("PUT", "/upload/big.bin", body=b"a" * self.upload_limit)
        self.assertEqual(status, 201)

    def test_over_limit_rejected(self):
        status, _ = self.raw_put("/upload/big.bin", content_length=self.upload_limit + 1)
        self.assertEqual(status, 413)
        self.assertEqual(self.uploads(), [])

    def test_default_limit_is_20mb(self):
        self.assertEqual(serve.UPLOAD_LIMIT, 20 * 1024 * 1024)
        self.assertEqual(serve.Config(bind="127.0.0.1").upload_limit, 20 * 1024 * 1024)

    def test_missing_content_length(self):
        status, _ = self.raw_put("/upload/x.txt", content_length=None)
        self.assertEqual(status, 411)
        self.assertEqual(self.uploads(), [])

    def test_bad_content_length(self):
        status, _ = self.raw_put("/upload/x.txt", content_length="-5")
        self.assertEqual(status, 400)

    def test_put_unknown_path(self):
        self.assertEqual(self.request("PUT", "/discover.ps1", body=b"x")[0], 404)
        self.assertEqual(self.request("PUT", "/upload", body=b"x")[0], 404)


class TokenDisabled(ServerTestCase):
    accept_token = False

    def test_token_forbidden_without_flag(self):
        status, _, _ = self.request("PUT", "/token", body=GOOD_TOKEN.encode())
        self.assertEqual(status, 403)
        self.assertEqual(self.stored, [])
        self.assertNotIn(GOOD_TOKEN, self.log.getvalue())


class TokenEnabled(ServerTestCase):
    accept_token = True

    def test_bad_token_400(self):
        for bad in ["short", GOOD_TOKEN + "x", GOOD_TOKEN[:-1] + "=", GOOD_TOKEN[:-1] + " "]:
            status, _, _ = self.request("PUT", "/token", body=bad.encode())
            self.assertEqual(status, 400, bad)
        self.assertEqual(self.stored, [])

    def test_good_token_then_second_refused(self):
        status, _, body = self.request("PUT", "/token", body=GOOD_TOKEN.encode())
        self.assertEqual(status, 204)
        self.assertEqual(body, b"")
        self.assertEqual(self.stored, [GOOD_TOKEN])
        status, _, _ = self.request("PUT", "/token", body=SECOND_TOKEN.encode())
        self.assertEqual(status, 403)
        self.assertEqual(self.stored, [GOOD_TOKEN])

    def test_trailing_newline_tolerated(self):
        status, _, _ = self.request("PUT", "/token", body=(GOOD_TOKEN + "\r\n").encode())
        self.assertEqual(status, 204)
        self.assertEqual(self.stored, [GOOD_TOKEN])

    def test_token_missing_length(self):
        status, _ = self.raw_put("/token", content_length=None)
        self.assertEqual(status, 411)
        self.assertEqual(self.stored, [])

    def test_token_never_logged_or_written(self):
        self.request("PUT", "/token", body=GOOD_TOKEN.encode())
        self.request("PUT", "/token", body=SECOND_TOKEN.encode())
        log = self.log.getvalue()
        self.assertNotIn(GOOD_TOKEN, log)
        self.assertNotIn(SECOND_TOKEN, log)
        self.assertIn("token stored", log)
        self.assertIn("token rejected", log)
        for p in Path(self.tmp.name).rglob("*"):
            if p.is_file():
                self.assertNotIn(GOOD_TOKEN.encode(), p.read_bytes())

    def test_store_failure_is_500_and_allows_retry(self):
        def boom(token):
            raise RuntimeError("no keychain")
        self.config.store_token = boom
        self.assertEqual(self.request("PUT", "/token", body=GOOD_TOKEN.encode())[0], 500)
        self.config.store_token = self.stored.append
        self.assertEqual(self.request("PUT", "/token", body=GOOD_TOKEN.encode())[0], 204)


class KeychainCommand(unittest.TestCase):
    def test_token_goes_to_stdin_not_argv(self):
        calls = []

        def fake_run(argv, **kwargs):
            calls.append((argv, kwargs))
            return mock.Mock(returncode=0, stdout="", stderr="")

        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
            serve.store_token_in_keychain(GOOD_TOKEN)
        argv, kwargs = calls[0]
        self.assertEqual(argv, ["/usr/bin/security", "-i"])
        self.assertEqual(kwargs["input"],
                         "add-generic-password -U -s ediabas-bridge -a agent -w %s\n" % GOOD_TOKEN)
        for argv, _ in calls:
            self.assertNotIn(GOOD_TOKEN, " ".join(argv))

    def test_failure_raises(self):
        with mock.patch.object(serve.subprocess, "run",
                               return_value=mock.Mock(returncode=1, stdout="", stderr="")):
            with self.assertRaises(RuntimeError):
                serve.store_token_in_keychain(GOOD_TOKEN)


class BindAndClients(unittest.TestCase):
    def test_wildcard_refused(self):
        for bad in ["0.0.0.0", "", "  ", "*", "::", "0", "localhost", "255.255.255.255",
                    "224.0.0.1", None, "192.168.64.1/24"]:
            with self.assertRaises(ValueError, msg=repr(bad)):
                serve.validate_bind(bad)

    def test_make_server_refuses_wildcard(self):
        with self.assertRaises(ValueError):
            serve.make_server(serve.Config(bind="0.0.0.0", port=0))

    def test_main_refuses_wildcard(self):
        with mock.patch("sys.stderr", new=io.StringIO()):
            self.assertNotEqual(serve.main(["--bind", "0.0.0.0", "--port", "0"]), 0)
            self.assertNotEqual(serve.main(["--bind", "", "--port", "0"]), 0)

    def test_main_no_bridge(self):
        with mock.patch.object(serve, "detect_bridge_address", return_value=None), \
                mock.patch("sys.stderr", new=io.StringIO()) as err:
            self.assertEqual(serve.main([]), 1)
        self.assertIn("Start the VM first", err.getvalue())

    def test_valid_bind(self):
        self.assertEqual(serve.validate_bind(" 192.168.64.1 "), "192.168.64.1")

    def test_find_bridge_address(self):
        text = (
            "lo0: flags=8049<UP,LOOPBACK,RUNNING,MULTICAST> mtu 16384\n"
            "\tinet 127.0.0.1 netmask 0xff000000\n"
            "en0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500\n"
            "\tinet 192.168.1.20 netmask 0xffffff00 broadcast 192.168.1.255\n"
            "bridge0: flags=8863<UP,BROADCAST,SMART,RUNNING,SIMPLEX,MULTICAST> mtu 1500\n"
            "\tinet 10.9.9.9 netmask 0xffffff00\n"
            "bridge100: flags=8a63<UP,BROADCAST,SMART,RUNNING,ALLMULTI,SIMPLEX,MULTICAST> mtu 1500\n"
            "\toptions=3<RXCSUM,TXCSUM>\n"
            "\tinet 192.168.64.1 netmask 0xffffff00 broadcast 192.168.64.255\n"
            "\tinet6 fe80::1%bridge100 prefixlen 64 scopeid 0x16\n"
        )
        self.assertEqual(serve.find_bridge_address(text), "192.168.64.1")

    def test_find_bridge_address_none(self):
        text = (
            "bridge0: flags=8863<UP,BROADCAST> mtu 1500\n"
            "\tinet 10.9.9.9 netmask 0xffffff00\n"
            "bridge101: flags=8822<BROADCAST,SMART> mtu 1500\n"
            "\tinet 192.168.65.1 netmask 0xffffff00\n"
            "bridge102: flags=8863<UP,BROADCAST> mtu 1500\n"
            "\tether 00:00:00:00:00:00\n"
        )
        self.assertIsNone(serve.find_bridge_address(text))

    def test_client_allowed(self):
        self.assertTrue(serve.client_allowed("192.168.64.1", "192.168.64.5"))
        self.assertTrue(serve.client_allowed("192.168.64.1", "::ffff:192.168.64.5"))
        self.assertFalse(serve.client_allowed("192.168.64.1", "192.168.65.5"))
        self.assertFalse(serve.client_allowed("192.168.64.1", "10.0.0.1"))
        self.assertFalse(serve.client_allowed("192.168.64.1", "127.0.0.1"))
        self.assertTrue(serve.client_allowed("127.0.0.1", "127.0.0.1"))
        self.assertFalse(serve.client_allowed("127.0.0.1", "192.168.64.5"))
        self.assertFalse(serve.client_allowed("192.168.64.1", "garbage"))


class ClientRejection(ServerTestCase):
    def test_foreign_client_403(self):
        with mock.patch.object(serve, "client_allowed", return_value=False):
            status, _, _ = self.request("GET", "/discover.ps1")
            self.assertEqual(status, 403)
            status, _, _ = self.request("PUT", "/upload/x.txt", body=b"x")
            self.assertEqual(status, 403)
        self.assertEqual(self.uploads(), [])


class Startup(unittest.TestCase):
    def test_lines_without_zip(self):
        with tempfile.TemporaryDirectory() as d:
            lines = serve.startup_lines("192.168.64.1", 8000, d)
        self.assertIn("  $base='http://192.168.64.1:8000'; irm $base/discover.ps1 | iex", lines)
        self.assertFalse(any("install.ps1" in l for l in lines))

    def test_lines_with_zip(self):
        with tempfile.TemporaryDirectory() as d:
            data = b"PK fake zip"
            (Path(d) / "EdiabasAgent.zip").write_bytes(data)
            sha = hashlib.sha256(data).hexdigest()
            lines = serve.startup_lines("192.168.64.1", 8000, d)
        self.assertIn("  $base='http://192.168.64.1:8000'; irm $base/discover.ps1 | iex", lines)
        self.assertIn("  $base='http://192.168.64.1:8000'; $sha='%s'; irm $base/install.ps1 | iex"
                      % sha, lines)
        self.assertIn("  $base='http://192.168.64.1:8000'; $sha='%s'; irm $base/update.ps1 | iex"
                      % sha, lines)

    def test_default_paths_from_repo_root(self):
        c = serve.Config(bind="127.0.0.1")
        root = Path(__file__).resolve().parent.parent
        self.assertEqual(c.setup_dir, root / "vm-setup")
        self.assertEqual(c.release_dir, root / "release")
        self.assertEqual(c.discovery_dir, root / "discovery")


if __name__ == "__main__":
    unittest.main()
