"""Tests for dev/serve.py.  Run from the repo root:  python3 -m unittest dev/test_serve.py"""

import hashlib
import http.client
import io
import json
import os
import plistlib
import sys
import tempfile
import threading
import time
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
    upload_count_limit = 50
    upload_total_limit = 100000
    client = "127.0.0.1"

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
            bind="127.0.0.1", client=self.client, port=0, setup_dir=self.setup_dir,
            release_dir=self.release_dir, discovery_dir=self.discovery_dir,
            accept_token=self.accept_token, store_token=self.stored.append, out=self.log,
            upload_limit=self.upload_limit, upload_count_limit=self.upload_count_limit,
            upload_total_limit=self.upload_total_limit)
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
        # A file is logged after its body is sent, so the client can finish first: wait for it.
        deadline = time.monotonic() + 2
        while not self.log.getvalue().strip() and time.monotonic() < deadline:
            time.sleep(0.01)
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

    def test_default_run_caps(self):
        c = serve.Config(bind="127.0.0.1")
        self.assertEqual(c.upload_count_limit, serve.UPLOAD_COUNT_LIMIT)
        self.assertEqual(c.upload_total_limit, serve.UPLOAD_TOTAL_LIMIT)
        self.assertLessEqual(serve.UPLOAD_COUNT_LIMIT, 20)
        self.assertLessEqual(serve.UPLOAD_TOTAL_LIMIT, 64 * 1024 * 1024)


class UploadCaps(ServerTestCase):
    upload_count_limit = 3
    upload_total_limit = 2500

    def test_count_cap(self):
        for i in range(3):
            self.assertEqual(self.request("PUT", "/upload/f%d.txt" % i, body=b"x")[0], 201)
        status, _, body = self.request("PUT", "/upload/f3.txt", body=b"x")
        self.assertEqual(status, 413)
        self.assertIn(b"restart serve.py", body)
        self.assertEqual(len(self.uploads()), 3)

    def test_total_bytes_cap_checked_before_writing(self):
        self.assertEqual(self.request("PUT", "/upload/a.bin", body=b"a" * 1000)[0], 201)
        self.assertEqual(self.request("PUT", "/upload/b.bin", body=b"b" * 1000)[0], 201)
        status, _ = self.raw_put("/upload/c.bin", content_length=501)
        self.assertEqual(status, 413)
        self.assertEqual(len(self.uploads()), 2)
        # what is left still fits
        self.assertEqual(self.request("PUT", "/upload/d.bin", body=b"d" * 500)[0], 201)

    def short_put(self, path, declared, body):
        """PUT that declares `declared` bytes, sends fewer, then closes its sending side."""
        import socket
        with socket.create_connection(("127.0.0.1", self.port), timeout=10) as sock:
            sock.sendall(b"PUT %s HTTP/1.1\r\nHost: x\r\nContent-Length: %d\r\n\r\n"
                         % (path.encode(), declared) + body)
            sock.shutdown(socket.SHUT_WR)
            reply = b""
            while chunk := sock.recv(4096):
                reply += chunk
        return int(reply.split(b" ", 2)[1])

    def test_failed_upload_still_counts(self):
        # An upload that never completes uses its share of the cap: no endless retries.
        self.assertEqual(self.short_put("/upload/a.bin", 1000, b"short"), 400)
        self.assertEqual(self.short_put("/upload/b.bin", 1000, b"short"), 400)
        self.assertEqual(self.raw_put("/upload/c.bin", content_length=501)[0], 413)
        self.assertEqual(self.uploads(), [])


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

    def test_store_failure_is_500_and_no_second_token_is_taken(self):
        # Exactly one token per run: after a failed store, restart serve.py to try again.
        def boom(token):
            raise RuntimeError("no keychain")
        self.config.store_token = boom
        self.assertEqual(self.request("PUT", "/token", body=GOOD_TOKEN.encode())[0], 500)
        self.config.store_token = self.stored.append
        status, _, body = self.request("PUT", "/token", body=GOOD_TOKEN.encode())
        self.assertEqual(status, 403)
        self.assertEqual(self.stored, [])

    def test_malformed_token_does_not_use_up_the_run(self):
        self.assertEqual(self.request("PUT", "/token", body=b"short")[0], 400)
        self.assertEqual(self.request("PUT", "/token", body=GOOD_TOKEN.encode())[0], 204)
        self.assertEqual(self.stored, [GOOD_TOKEN])


class KeychainCommand(unittest.TestCase):
    @staticmethod
    def fake_security(stored_value, add_rc=0, find_rc=0):
        calls = []

        def fake_run(argv, **kwargs):
            calls.append((argv, kwargs))
            if argv[1:2] == ["-i"]:
                return mock.Mock(returncode=add_rc, stdout="", stderr="")
            return mock.Mock(returncode=find_rc, stdout=stored_value, stderr="")
        return calls, fake_run

    def test_token_goes_to_stdin_not_argv(self):
        calls, fake_run = self.fake_security(GOOD_TOKEN + "\n")
        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
            serve.store_token_in_keychain(GOOD_TOKEN)
        argv, kwargs = calls[0]
        self.assertEqual(argv, ["/usr/bin/security", "-i"])
        self.assertEqual(kwargs["input"],
                         "add-generic-password -U -s ediabas-bridge -a agent -w %s\n" % GOOD_TOKEN)
        for argv, _ in calls:
            self.assertNotIn(GOOD_TOKEN, " ".join(argv))

    def test_reads_the_value_back(self):
        calls, fake_run = self.fake_security(GOOD_TOKEN + "\n")
        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
            serve.store_token_in_keychain(GOOD_TOKEN)
        self.assertEqual(calls[1][0], ["/usr/bin/security", "find-generic-password",
                                       "-s", "ediabas-bridge", "-a", "agent", "-w"])

    def test_old_value_still_there_raises(self):
        # security -i exited 0 but the add did not happen; an older item is still stored.
        _, fake_run = self.fake_security(SECOND_TOKEN + "\n")
        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
            with self.assertRaises(RuntimeError) as ctx:
                serve.store_token_in_keychain(GOOD_TOKEN)
        self.assertNotIn(GOOD_TOKEN, str(ctx.exception))
        self.assertNotIn(SECOND_TOKEN, str(ctx.exception))

    def test_item_missing_raises(self):
        _, fake_run = self.fake_security("", find_rc=44)
        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
            with self.assertRaises(RuntimeError):
                serve.store_token_in_keychain(GOOD_TOKEN)

    def test_failure_raises(self):
        _, fake_run = self.fake_security(GOOD_TOKEN, add_rc=1)
        with mock.patch.object(serve.subprocess, "run", side_effect=fake_run):
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
            serve.make_server(serve.Config(bind="0.0.0.0", client="192.168.64.5", port=0))

    def test_make_server_needs_the_vm_address(self):
        with self.assertRaises(ValueError):
            serve.make_server(serve.Config(bind="127.0.0.1", port=0))

    def test_main_refuses_wildcard(self):
        with mock.patch("sys.stderr", new=io.StringIO()):
            self.assertNotEqual(serve.main(["--bind", "0.0.0.0", "--port", "0"]), 0)
            self.assertNotEqual(serve.main(["--bind", "", "--port", "0"]), 0)

    def test_main_no_bridge(self):
        with mock.patch.object(serve, "detect_bridge_address", return_value=None), \
                mock.patch("sys.stderr", new=io.StringIO()) as err:
            self.assertEqual(serve.main([]), 1)
        self.assertIn("Start the VM first", err.getvalue())

    def run_main(self, argv, leases="", env=None, local=None, utm_mac="02:00:00:00:00:01",
                 vm_name="Windows"):
        """main() up to the point it would serve; returns (code, config or None, stdout, stderr).

        Runs against a temporary UTM Documents folder (with a config.plist for `vm_name` holding
        `utm_mac`, none when utm_mac is None) and config.local.json (`local`: a dict, a string
        written as is, or None for no file); EDIABAS_VM_* from the real environment are dropped.
        """
        made = []
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        documents = Path(tmp.name) / "Documents"
        if utm_mac is not None:
            vm_dir = documents / ("%s.utm" % vm_name)
            vm_dir.mkdir(parents=True)
            with open(vm_dir / "config.plist", "wb") as f:
                plistlib.dump({"Backend": "QEMU", "Network": [
                    {"Hardware": "virtio-net-pci", "MacAddress": utm_mac, "Mode": "Shared"}]}, f)
        local_path = Path(tmp.name) / "config.local.json"
        if local is not None:
            local_path.write_text(local if isinstance(local, str) else json.dumps(local),
                                  encoding="utf-8")

        class Stop(serve.Server):
            def __init__(self, config):
                made.append(config)
                self.server_address = (config.bind, 8000)

            def serve_forever(self):
                raise KeyboardInterrupt

            def server_close(self):
                pass

        def make(config):
            config.client = serve.validate_client(config.bind, config.client)
            return Stop(config)

        with mock.patch.object(serve, "detect_bridge_address", return_value="192.168.64.1"), \
                mock.patch.object(serve, "read_leases", return_value=leases), \
                mock.patch.object(serve, "make_server", side_effect=make), \
                mock.patch.object(serve, "UTM_DOCUMENTS", documents), \
                mock.patch.object(serve, "LOCAL_CONFIG_PATH", local_path), \
                mock.patch.dict(os.environ, {}, clear=False), \
                mock.patch("sys.stdout", new=io.StringIO()) as out, \
                mock.patch("sys.stderr", new=io.StringIO()) as err:
            os.environ.pop("EDIABAS_VM_MAC", None)
            os.environ.pop("EDIABAS_VM_NAME", None)
            os.environ.update(env or {})
            code = serve.main(argv)
        return code, (made[0] if made else None), out.getvalue(), err.getvalue()

    LEASES = (
        "{\n\tname=other\n\tip_address=192.168.64.9\n\thw_address=1,aa:bb:cc:0:0:1\n"
        "\tidentifier=1,aa:bb:cc:0:0:1\n\tlease=0x7f000000\n}\n"
        "{\n\tname=DESKTOP\n\tip_address=192.168.64.3\n\thw_address=1,2:0:0:0:0:1\n"
        "\tidentifier=1,2:0:0:0:0:1\n\tlease=0x6b01baf4\n}\n"
        "{\n\tname=DESKTOP\n\tip_address=192.168.64.4\n\thw_address=1,2:0:0:0:0:1\n"
        "\tlease=0x6b01baf5\n}\n"
    )

    def test_main_takes_the_vm_from_its_latest_lease(self):
        code, cfg, out, _ = self.run_main([], leases=self.LEASES)
        self.assertEqual(code, 0)
        self.assertEqual(cfg.client, "192.168.64.4")
        self.assertIn("192.168.64.4", out)

    def test_main_vm_mac_from_the_environment(self):
        code, cfg, _, _ = self.run_main([], leases=self.LEASES,
                                        env={"EDIABAS_VM_MAC": "AA:BB:CC:00:00:01"})
        self.assertEqual(code, 0)
        self.assertEqual(cfg.client, "192.168.64.9")

    def test_main_vm_mac_precedence(self):
        other = "AA:BB:CC:00:00:01"  # the "other" lease, 192.168.64.9
        vm = "02:00:00:00:00:01"  # the VM's leases, latest 192.168.64.4
        cases = [
            # (env, config.local.json, MAC in UTM's config) -> expected client
            ({}, None, vm, "192.168.64.4"),
            ({}, {"vmMac": other}, vm, "192.168.64.9"),  # local file beats UTM
            ({"EDIABAS_VM_MAC": vm}, {"vmMac": other}, other, "192.168.64.4"),  # env beats both
            ({}, {"vmMac": ""}, vm, "192.168.64.4"),  # a bad value is ignored
        ]
        for env, local, utm_mac, want in cases:
            code, cfg, _, _ = self.run_main([], leases=self.LEASES, env=env, local=local,
                                            utm_mac=utm_mac)
            self.assertEqual(code, 0, (env, local))
            self.assertEqual(cfg.client, want, (env, local))

    def test_main_vm_name_picks_the_utm_config(self):
        for env, local in [({"EDIABAS_VM_NAME": "Win11"}, None),
                           ({}, {"vmName": "Win11"}),
                           ({"EDIABAS_VM_NAME": "Win11"}, {"vmName": "Windows"})]:
            code, cfg, _, err = self.run_main([], leases=self.LEASES, env=env, local=local,
                                              utm_mac="AA:BB:CC:00:00:01", vm_name="Win11")
            self.assertEqual(code, 0, err)
            self.assertEqual(cfg.client, "192.168.64.9")

    def test_main_without_a_mac_says_how_to_set_it(self):
        code, cfg, _, err = self.run_main([], leases=self.LEASES, utm_mac=None)
        self.assertEqual(code, 1)
        self.assertIsNone(cfg)
        self.assertIn("MAC address is not set and could not be read from", err)
        self.assertIn("Windows.utm", err)
        self.assertIn("set EDIABAS_VM_MAC", err)
        self.assertIn("EDIABAS_VM_NAME if the VM in UTM is not called 'Windows'", err)
        self.assertIn("--client", err)

    def test_main_bad_local_config_warns_and_carries_on(self):
        code, cfg, _, err = self.run_main([], leases=self.LEASES, local="{ not json")
        self.assertEqual(code, 0)
        self.assertEqual(cfg.client, "192.168.64.4")
        self.assertIn("warning:", err)
        self.assertIn("is not valid JSON", err)

    def test_main_without_a_lease_refuses_to_start(self):
        code, cfg, _, err = self.run_main([], leases="")
        self.assertEqual(code, 1)
        self.assertIsNone(cfg)
        self.assertIn("--client", err)
        self.assertIn("02:00:00:00:00:01", err)
        self.assertIn("set EDIABAS_VM_MAC", err)

    def test_main_client_flag_wins(self):
        code, cfg, _, _ = self.run_main(["--client", "192.168.64.7"], leases=self.LEASES)
        self.assertEqual(code, 0)
        self.assertEqual(cfg.client, "192.168.64.7")

    def test_main_refuses_a_client_off_the_bridge(self):
        for bad in ["192.168.65.7", "10.0.0.1", "127.0.0.1", "192.168.64.1", "0.0.0.0",
                    "192.168.64.255", "x"]:
            code, cfg, _, err = self.run_main(["--client", bad])
            self.assertEqual(code, 2, bad)
            self.assertIsNone(cfg, bad)

    def test_local_config_keys_match_the_mcp_server(self):
        config_ts = (Path(__file__).resolve().parent.parent / "mac-mcp" / "src" / "config.ts"
                     ).read_text(encoding="utf-8")
        self.assertIn("const LOCAL_KEYS = [%s] as const;"
                      % ", ".join('"%s"' % k for k in serve.LOCAL_KEYS), config_ts)
        self.assertIn('vmName: "%s"' % serve.VM_NAME_DEFAULT, config_ts)

    def test_read_local_config(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "config.local.json"
            self.assertEqual(serve.read_local_config(path), ({}, []))
            path.write_text(json.dumps({"_comment": "x", "vmName": " Win11 ", "vmMac": "02:00:00:00:00:01",
                                        "agentPort": 9000, "voltageSources": [{"ecu": "A", "job": "B"}]}),
                            encoding="utf-8")
            self.assertEqual(serve.read_local_config(path),
                             ({"vmName": "Win11", "vmMac": "02:00:00:00:00:01", "agentPort": 9000}, []))
            path.write_text(json.dumps({"vmname": "x", "vmMac": 5, "agentPort": True}), encoding="utf-8")
            local, warnings = serve.read_local_config(path)
            self.assertEqual(local, {})
            self.assertEqual(len(warnings), 3)
            self.assertIn('unknown key "vmname"', warnings[0])
            self.assertIn("vmMac must be a non-empty string", warnings[1])
            self.assertIn("agentPort must be a whole number", warnings[2])
            path.write_text("[1]", encoding="utf-8")
            self.assertIn("must hold one JSON object", serve.read_local_config(path)[1][0])
            path.write_text("{", encoding="utf-8")
            self.assertIn("is not valid JSON", serve.read_local_config(path)[1][0])

    def test_mac_from_utm_config(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / "config.plist"
            self.assertIsNone(serve.mac_from_utm_config(path))  # missing file
            for data, want in [
                ({"Network": [{"MacAddress": "02:00:00:00:00:01"}, {"MacAddress": "02:00:00:00:00:02"}]},
                 "02:00:00:00:00:01"),
                ({"Network": []}, None),
                ({"Network": [{}]}, None),
                ({"Network": [{"MacAddress": "garbage"}]}, None),
                ({}, None),
            ]:
                with open(path, "wb") as f:
                    plistlib.dump(data, f)
                self.assertEqual(serve.mac_from_utm_config(path), want, data)
            path.write_bytes(b"not a plist")
            self.assertIsNone(serve.mac_from_utm_config(path))
        self.assertEqual(serve.utm_config_path("Win11", "/D"), Path("/D/Win11.utm/config.plist"))

    def test_lease_parsing(self):
        self.assertEqual(serve.vm_lease_address(self.LEASES, "02:00:00:00:00:01"), "192.168.64.4")
        self.assertEqual(serve.vm_lease_address(self.LEASES, "02-00-00-00-00-01"), "192.168.64.4")
        self.assertIsNone(serve.vm_lease_address(self.LEASES, "02:00:00:00:00:02"))
        self.assertIsNone(serve.vm_lease_address(self.LEASES, "garbage"))
        self.assertIsNone(serve.vm_lease_address("", "02:00:00:00:00:01"))

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

    def test_client_allowed_is_one_address(self):
        self.assertTrue(serve.client_allowed("192.168.64.5", "192.168.64.5"))
        self.assertTrue(serve.client_allowed("192.168.64.5", "::ffff:192.168.64.5"))
        self.assertFalse(serve.client_allowed("192.168.64.5", "192.168.64.6"))
        self.assertFalse(serve.client_allowed("192.168.64.5", "192.168.65.5"))
        self.assertFalse(serve.client_allowed("192.168.64.5", "127.0.0.1"))
        self.assertFalse(serve.client_allowed("192.168.64.5", "garbage"))
        self.assertFalse(serve.client_allowed(None, "192.168.64.5"))

    def test_validate_client(self):
        self.assertEqual(serve.validate_client("192.168.64.1", " 192.168.64.5 "), "192.168.64.5")
        self.assertEqual(serve.validate_client("127.0.0.1", "127.0.0.2"), "127.0.0.2")
        self.assertEqual(serve.validate_client("127.0.0.1", "127.0.0.1"), "127.0.0.1")
        for bad in [None, "", "0.0.0.0", "192.168.64.1", "192.168.64.255", "192.168.65.5",
                    "127.0.0.1", "224.0.0.1", "::1", "192.168.64.0/24", "vm"]:
            with self.assertRaises(ValueError, msg=repr(bad)):
                serve.validate_client("192.168.64.1", bad)
        with self.assertRaises(ValueError):
            serve.validate_client("127.0.0.1", "192.168.64.5")


class ClientRejection(ServerTestCase):
    # The server accepts only 127.0.0.2; these requests come from 127.0.0.1, like another VM
    # on the same network would.
    client = "127.0.0.2"
    accept_token = True

    def test_other_address_refused_everywhere(self):
        status, _, _ = self.request("GET", "/discover.ps1")
        self.assertEqual(status, 403)
        status, _, _ = self.request("GET", "/install.ps1")
        self.assertEqual(status, 403)
        status, _, _ = self.request("PUT", "/upload/x.txt", body=b"x")
        self.assertEqual(status, 403)
        status, _, _ = self.request("PUT", "/token", body=GOOD_TOKEN.encode())
        self.assertEqual(status, 403)
        self.assertEqual(self.uploads(), [])
        self.assertEqual(self.stored, [])


class Startup(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.release = Path(self.tmp.name) / "release"
        self.setup = Path(self.tmp.name) / "vm-setup"
        self.release.mkdir()
        self.setup.mkdir()
        self.script_sha = {}
        for name in serve.SCRIPT_NAMES:
            data = ("# %s\r\nWrite-Host 'hi'\r\n" % name).encode("ascii")
            (self.setup / name).write_bytes(data)
            self.script_sha[name] = hashlib.sha256(data).hexdigest()

    def tearDown(self):
        self.tmp.cleanup()

    def lines(self, **kw):
        return serve.startup_lines("192.168.64.1", 8000, self.release, setup_dir=self.setup,
                                   client="192.168.64.3", **kw)

    def line_for(self, lines, script):
        found = [l for l in lines if ("/%s" % script) in l]
        self.assertEqual(len(found), 1, script)
        return found[0]

    def test_lines_without_zip(self):
        lines = self.lines()
        self.line_for(lines, "discover.ps1")
        self.line_for(lines, "uninstall.ps1")
        self.assertFalse(any("install.ps1" in l and "uninstall" not in l for l in lines))
        self.assertFalse(any("update.ps1" in l for l in lines))

    def test_exact_install_and_update_lines(self):
        data = b"PK fake zip"
        (self.release / "EdiabasAgent.zip").write_bytes(data)
        sha = hashlib.sha256(data).hexdigest()
        lines = self.lines()
        for script in ("install.ps1", "update.ps1"):
            expected = (
                "  $base='http://192.168.64.1:8000'; $sha='%s'; "
                "$ebFile=\"$env:TEMP\\bimmer-bridge-%s\"; "
                "Remove-Item -LiteralPath $ebFile -Force -ErrorAction SilentlyContinue; "
                "$ebOk=$false; "
                "try { iwr \"$base/%s\" -UseBasicParsing -OutFile $ebFile -ErrorAction Stop; $ebOk=$true } "
                "catch { Write-Host 'download failed: the Mac did not serve %s (is serve.py running?)' -ForegroundColor Red }; "
                "if ($ebOk) { "
                "$ebBytes=[IO.File]::ReadAllBytes($ebFile); "
                "if (([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($ebBytes)) -replace '-','') -eq '%s') "
                "{ iex ([Text.Encoding]::UTF8.GetString($ebBytes)) } "
                "else { Write-Host '%s is not the file serve.py checked; it was NOT run. Restart serve.py and paste the line it prints.' -ForegroundColor Red } }"
                % (sha, script, script, script, self.script_sha[script], script))
            self.assertEqual(self.line_for(lines, script), expected)

    def test_every_line_pins_its_script_and_never_pipes_into_iex(self):
        (self.release / "EdiabasAgent.zip").write_bytes(b"zip")
        lines = self.lines()
        for script in serve.SCRIPT_NAMES:
            line = self.line_for(lines, script)
            self.assertIn("-eq '%s')" % self.script_sha[script], line)
            self.assertNotIn("| iex", line)
            self.assertNotIn("irm", line)
            line.encode("ascii")
        text = "\n".join(lines)
        self.assertIn("$Purge=$true; ", text)
        self.assertNotIn("KeepData", text)

    def test_install_line_sends_the_token_only_with_accept_token(self):
        # install.ps1 sends the token on a first install, or when $SendToken is true; serve.py
        # sets it only when it will take the token, so a re-install does not send it for nothing.
        (self.release / "EdiabasAgent.zip").write_bytes(b"zip")
        plain = self.lines()
        self.assertNotIn("SendToken", "\n".join(plain))
        accepting = self.lines(accept_token=True)
        self.assertTrue(self.line_for(accepting, "install.ps1").startswith(
            "  $SendToken=$true; $base='http://192.168.64.1:8000'; "))
        for script in ("discover.ps1", "update.ps1", "uninstall.ps1"):
            self.assertNotIn("SendToken", self.line_for(accepting, script))

    def test_install_script_never_prints_the_token(self):
        text = (serve.REPO_ROOT / "vm-setup" / "install.ps1").read_text(encoding="ascii")
        self.assertNotIn("add-generic-password", text)
        self.assertNotIn("-w '", text)
        self.assertIn("'SendToken'", text)

    def test_options_do_not_stick_in_the_window(self):
        # The pasted line sets its options in the global session; each script removes the ones
        # it read, so a later paste in the same window does not inherit them.
        setup = serve.REPO_ROOT / "vm-setup"
        for script, names in (("install.ps1", ("Elevated", "SendToken")),
                              ("uninstall.ps1", ("KeepData", "Purge"))):
            text = (setup / script).read_text(encoding="ascii")
            self.assertIn("Remove-Variable -Name $Name -Scope Global", text, script)
            for name in names:
                self.assertIn("Get-OneShotVar '%s'" % name, text, script)
                self.assertNotIn("Get-OptionalVar '%s'" % name, text, script)

    def test_left_over_staging_folders_are_removed_first(self):
        # A closed window skips the finally that removes the staging folder.
        setup = serve.REPO_ROOT / "vm-setup"
        for script in ("install.ps1", "update.ps1", "uninstall.ps1"):
            text = (setup / script).read_text(encoding="ascii")
            self.assertIn("function Remove-StaleStaging", text, script)
            self.assertIn("'^EdiabasBridge-staging-[0-9a-f]{32}$'", text, script)
            self.assertIn("[System.IO.Directory]::Delete($dir.FullName, $true)", text, script)
            body = text[text.index("    try {\n"):]
            call = body.index("        Remove-StaleStaging\n")
            self.assertGreater(call, body.index("        Assert-WindowsPowerShell\n"), script)
            if "New-AdminOnlyDirectory\n" in body:
                self.assertLess(call, body.index("New-AdminOnlyDirectory\n"), script)

    def test_install_and_update_share_one_block(self):
        # The staging folder, the verified copy and stopping the agent are the same code in
        # both scripts; a fix to one must reach the other.
        blocks = []
        for script in ("install.ps1", "update.ps1"):
            text = (serve.REPO_ROOT / "vm-setup" / script).read_text(encoding="ascii")
            self.assertEqual(text.count("    # shared:begin\n"), 1, script)
            self.assertEqual(text.count("    # shared:end\n"), 1, script)
            block = text.split("    # shared:begin\n", 1)[1].split("    # shared:end\n", 1)[0]
            for name in ("New-AdminOnlyDirectory", "Remove-StaleStaging", "Copy-VerifiedExe",
                         "Stop-Agent", "Set-FirewallRule", "Get-DefaultRoute", "Invoke-Http",
                         "Wait-AgentAnswer"):
                self.assertIn("function %s" % name, block, script)
            blocks.append(block)
        self.assertEqual(blocks[0], blocks[1])

    def test_firewall_rule_is_made_in_the_shared_block_only(self):
        # Install and Update both pin the rule to the current gateway (F10).
        for script in ("install.ps1", "update.ps1"):
            text = (serve.REPO_ROOT / "vm-setup" / script).read_text(encoding="ascii")
            block = text.split("    # shared:begin\n", 1)[1].split("    # shared:end\n", 1)[0]
            rest = text.replace(block, "")
            self.assertIn("function Set-FirewallRule([int]$Port)", block, script)
            self.assertIn("-RemoteAddress $gateway", block, script)
            self.assertNotIn("New-NetFirewallRule", rest, script)
            self.assertIn("        Set-FirewallRule $port\n", rest, script)

    def test_a_bad_copy_is_not_deleted_by_the_copy(self):
        # Copy-VerifiedExe reports a hash mismatch with $false; the caller restores the backup.
        text = (serve.REPO_ROOT / "vm-setup" / "update.ps1").read_text(encoding="ascii")
        copy = text.split("function Copy-VerifiedExe", 1)[1].split("\n    }\n", 1)[0]
        self.assertNotIn("Remove-Item", copy)
        self.assertNotIn("throw", copy)

    def test_update_backs_up_rolls_back_and_merges(self):
        # F10, F13: the update order, the backup, the rollback and the conflict-name merge.
        text = (serve.REPO_ROOT / "vm-setup" / "update.ps1").read_text(encoding="ascii")
        body = text[text.index("    $tmp = $null\n"):]
        order = ["Disable-ScheduledTask", "        Stop-Agent\n", "$BackupPath $oldHash",
                 "Copy-VerifiedExe $newExe $ExePath $exeHash", "Merge-ConflictDefaults\n",
                 "Set-FirewallRule $port", "Enable-ScheduledTask", "Start-ScheduledTask",
                 "Wait-AgentAnswer"]
        at = [body.index(step) for step in order]
        self.assertEqual(at, sorted(at))
        self.assertIn("'EdiabasAgent.exe.bak'", text)
        self.assertIn("Copy-VerifiedExe $BackupPath $ExePath $OldHash", text)
        self.assertIn("'Rolled back to ' + $oldVersion", text)
        self.assertIn("Wait-AgentAnswer $url $token 60", body)
        finally_part = body[body.rindex("    } finally {\n"):]
        self.assertIn("Enable-ScheduledTask", finally_part)
        for name in ("'INPALOAD'", "'TOOL32'", "'NCSEXPER'", "'WINKFPT'", "'BMW Coding Tool'",
                     "'WINKFP'", "'ISTAGUI'"):
            self.assertIn(name, text)
        self.assertIn("ConvertTo-Json -Depth 10", text)
        self.assertIn("New-Object System.Text.UTF8Encoding($false)", text)

    def test_update_keeps_and_restores_the_config_backup(self):
        # Review fix: a merge keeps config.json.bak, checks the new text before the swap, and a
        # rollback puts config.json back when the merge wrote it.
        text = (serve.REPO_ROOT / "vm-setup" / "update.ps1").read_text(encoding="ascii")
        self.assertIn("$ConfigBackupPath = Join-Path $Root 'config.json.bak'", text)
        self.assertIn("[System.IO.File]::Replace($newPath, $ConfigPath, $ConfigBackupPath)", text)
        self.assertNotIn("Replace($newPath, $ConfigPath, $null)", text)
        self.assertNotIn("Add-Member", text)
        merge = text.split("function Merge-ConflictDefaults", 1)[1].split("\n    }\n", 1)[0]
        self.assertLess(merge.index("'reviewedJobs has '"), merge.index("WriteAllText"))
        self.assertLess(merge.index("'the key '"), merge.index("WriteAllText"))
        self.assertLess(merge.index("'conflictProcesses is not an array'"), merge.index("WriteAllText"))
        self.assertIn("$configMerged = Merge-ConflictDefaults", text)
        rollback = text[text.index("Rolling back."):text.index("'Rolled back to '")]
        self.assertIn("if ($configMerged)", rollback)
        self.assertIn("[System.IO.File]::Copy($ConfigBackupPath, $ConfigPath, $true)", rollback)
        self.assertIn("'Rolled back to ' + $oldVersion + $configNote", text)

    def test_update_rechecks_the_restored_agent_and_survives_a_failed_merge(self):
        # Final review: after a rollback /health is asked once more, and a restored agent that
        # does not answer either is named; an exception in the merge never stops the update.
        text = (serve.REPO_ROOT / "vm-setup" / "update.ps1").read_text(encoding="ascii")
        after = text[text.index("'Rolled back to ' + $oldVersion"):]
        after = after[:after.index("Write-Step ('Updated to bimmer-bridge agent '")]
        self.assertIn("$restored = Wait-AgentAnswer $url $token 60", after)
        self.assertIn("if (Test-AgentAnswered $restored) {", after)
        self.assertIn("does not answer at ' + $url + ' either", after)
        self.assertIn("does not answer either. Check ' + $ConfigPath", after)
        helper = text.split("function Test-AgentAnswered($result)", 1)[1].split("\n    }\n", 1)[0]
        self.assertIn("$result.Status -eq 200", helper)
        merge_call = text[text.index("Write-Step 'Checking conflictProcesses in config.json'"):text.index("Set-FirewallRule $port")]
        self.assertLess(merge_call.index("try {"), merge_call.index("$configMerged = Merge-ConflictDefaults"))
        catch = merge_call[merge_call.index("} catch {"):]
        self.assertIn("$configMerged = $false", catch)
        self.assertIn("config.json left untouched", catch)
        self.assertNotIn("throw", catch)

    def test_a_403_counts_as_up_only_with_the_forbidden_body(self):
        for script in ("install.ps1", "update.ps1"):
            text = (serve.REPO_ROOT / "vm-setup" / script).read_text(encoding="ascii")
            for line in text.splitlines():
                if "Status -eq 403" in line:
                    self.assertIn("[string]$result.Body -like '*forbidden*'", line, script)

    def test_update_warns_about_a_task_it_enabled_and_a_missing_rule(self):
        text = (serve.REPO_ROOT / "vm-setup" / "update.ps1").read_text(encoding="ascii")
        self.assertIn("$wasDisabled = ([string]$task.State -eq 'Disabled')", text)
        self.assertEqual(text.count("if ($wasDisabled) {"), 2)
        self.assertIn("is in place, but the firewall rule", text)
        self.assertIn("Run the Install line again.", text)

    def test_says_which_address_it_accepts(self):
        self.assertTrue(any("192.168.64.3" in l and "only" in l for l in self.lines()))

    def test_no_line_for_a_missing_script(self):
        (self.setup / "update.ps1").unlink()
        (self.release / "EdiabasAgent.zip").write_bytes(b"zip")
        self.assertFalse(any("/update.ps1" in l for l in self.lines()))

    def test_printed_hash_is_what_the_server_serves(self):
        cfg = serve.Config(bind="127.0.0.1", client="127.0.0.1", port=0, setup_dir=self.setup,
                           release_dir=self.release, out=io.StringIO())
        server = serve.make_server(cfg)
        t = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05},
                             daemon=True)
        t.start()
        try:
            for script in serve.SCRIPT_NAMES:
                conn = http.client.HTTPConnection("127.0.0.1", server.server_address[1], timeout=10)
                conn.request("GET", "/" + script)
                body = conn.getresponse().read()
                conn.close()
                self.assertEqual(hashlib.sha256(body).hexdigest(), self.script_sha[script])
        finally:
            server.shutdown()
            server.server_close()

    def test_real_scripts_are_ascii(self):
        # The pasted line decodes the script as UTF-8; the scripts stay ASCII so no BOM or
        # code page question arises.
        for name in serve.SCRIPT_NAMES:
            (serve.REPO_ROOT / "vm-setup" / name).read_bytes().decode("ascii")

    def test_default_paths_from_repo_root(self):
        c = serve.Config(bind="127.0.0.1")
        root = Path(__file__).resolve().parent.parent
        self.assertEqual(c.setup_dir, root / "vm-setup")
        self.assertEqual(c.release_dir, root / "release")
        self.assertEqual(c.discovery_dir, root / "discovery")


if __name__ == "__main__":
    unittest.main()
