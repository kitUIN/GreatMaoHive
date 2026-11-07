"""Tests for the standalone installer's download, extraction and recovery boundaries."""
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import types
import urllib.request
import unittest
from unittest.mock import patch
import zipfile


source = Path(__file__).with_name("install-linux.sh").read_text()
installer = types.ModuleType("installer")
exec(compile(source.split("# BEGIN INSTALLER PYTHON\n", 1)[1].split("# END INSTALLER PYTHON", 1)[0],
             "install-linux.sh", "exec"), installer.__dict__)


class DependencySetupTests(unittest.TestCase):
    def bootstrap(self, package, ready=False):
        # Exercise the real shell branch without installing packages on the test host.
        setup = source.split("# BEGIN DEPENDENCY SETUP\n", 1)[1].split("# END DEPENDENCY SETUP", 1)[0]
        stubs = '''
set -euo pipefail
python3() { return "$DEPENDENCIES_MISSING"; }
apt-get() {
    printf 'apt-get'; printf ' %s' "$@"; printf '\\n'
    if [[ "$1" == install ]]; then DEPENDENCIES_MISSING=0; fi
}
apt-cache() {
    if [[ "$2" == "$AVAILABLE_SSL" ]]; then
        printf '  Candidate: 3.0.0\\n'
    elif [[ "$2" == libssl3 ]]; then
        printf '  Candidate: (none)\\n'
    fi
}
'''
        return subprocess.run(["bash", "-c", stubs + setup], capture_output=True, text=True,
                              env=dict(os.environ, AVAILABLE_SSL=package,
                                       DEPENDENCIES_MISSING="0" if ready else "1"))

    def test_apt_selects_available_openssl_package(self):
        for package in ("libssl3t64", "libssl3", "libssl1.1"):
            with self.subTest(package=package):
                result = self.bootstrap(package)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn("apt-get install -y python3 ca-certificates " + package + " zlib1g", result.stdout)

    def test_missing_openssl_reports_error_without_installing(self):
        result = self.bootstrap("")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("OpenSSL 1.1 or 3 is required", result.stderr)
        self.assertNotIn("apt-get install", result.stdout)

    def test_existing_dependencies_skip_package_manager(self):
        result = self.bootstrap("", ready=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout, "")


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.hive_config = self.root / "hive.json"
        self.hive_env = self.root / "hive.env"
        for name, path in (("LOCAL_HIVE_CONFIG", self.hive_config), ("LOCAL_HIVE_ENV", self.hive_env)):
            replacement = patch.object(installer, name, path)
            replacement.start()
            self.addCleanup(replacement.stop)

    def local_hive(self, address="http://127.0.0.1:7443", node_id="bee-01"):
        self.hive_config.write_text(json.dumps({"Urls": address, "Hive": {
            "AllowInsecureTransport": True, "Bees": [{"Id": node_id, "Token": "local-secret-" * 4}]}}))

    def enter_only_bee(self):
        output = io.StringIO()
        with patch("builtins.input", return_value=""), patch.object(installer.getpass, "getpass", return_value="") as secret, \
                patch("sys.stdout", output):
            config = installer.interactive_config("bee")
        installer.validate_config(config, "bee")
        self.assertNotIn(config["Bee"]["Token"], output.getvalue())
        self.assertNotIn(config["Bee"]["Token"], secret.call_args.args[0])
        return config["Bee"]

    def test_enter_only_without_hive_creates_valid_unique_credentials(self):
        bee = self.enter_only_bee()
        self.assertEqual(bee["Id"], "bee-01")
        self.assertEqual(bee["HiveUrl"], "wss://localhost:7443/agent")
        self.assertEqual(len(bee["Token"]), 64)
        self.assertNotEqual(bee["Token"], self.enter_only_bee()["Token"])
        self.assertFalse(bee.get("AllowInsecureTransport", False))

    def test_enter_only_reuses_matching_local_hive_and_plaintext_opt_in(self):
        self.local_hive()
        bee = self.enter_only_bee()
        self.assertEqual(bee["Token"], "local-secret-" * 4)
        self.assertEqual(bee["HiveUrl"], "ws://127.0.0.1:7443/agent")
        self.assertTrue(bee["AllowInsecureTransport"])

    def test_local_https_wildcard_becomes_localhost_without_disabling_tls(self):
        self.local_hive("https://0.0.0.0:7443")
        bee = self.enter_only_bee()
        self.assertEqual(bee["HiveUrl"], "wss://localhost:7443/agent")
        self.assertFalse(bee.get("AllowInsecureTransport", False))

    def test_overridden_url_does_not_receive_local_hive_token(self):
        self.local_hive()
        with patch("builtins.input", side_effect=["", "wss://other.example/agent", "", "", ""]), \
                patch.object(installer.getpass, "getpass", return_value=""), patch("sys.stdout", io.StringIO()):
            config = installer.interactive_config("bee")
        installer.validate_config(config, "bee")
        self.assertNotEqual(config["Bee"]["Token"], "local-secret-" * 4)

    def test_local_hive_with_other_id_or_environment_overrides_is_not_reused(self):
        self.local_hive(node_id="another-bee")
        self.assertNotEqual(self.enter_only_bee()["Token"], "local-secret-" * 4)
        self.local_hive()
        self.hive_env.write_text("Hive__Bees__0__Token=overridden\n")
        self.assertNotEqual(self.enter_only_bee()["Token"], "local-secret-" * 4)

    def test_malformed_local_hive_falls_back_to_deployable_defaults(self):
        self.hive_config.write_text("not json")
        self.assertEqual(self.enter_only_bee()["HiveUrl"], "wss://localhost:7443/agent")

    def package(self, extra=None):
        archive = self.root / "GreatMaoNetBee-linux-x64.zip"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("GreatMaoNetBee", "binary")
            package.writestr("GreatMaoNetUpdater", "updater")
            package.writestr("libe_sqlite3.so", "sqlite")
            package.writestr("appsettings.json", "{}")
            package.writestr("GreatMaoNetBee.dbg", "debug")
            if extra:
                package.writestr(*extra)
        checksum = self.root / (archive.name + ".sha256")
        checksum.write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name + "\n")
        return archive, checksum

    def extract(self, extra=None):
        archive, checksum = self.package(extra)
        return installer.extract_package(archive, checksum, self.root / "payload", "GreatMaoNetBee")

    def test_complete_package_and_executable_modes(self):
        self.assertEqual(set(self.extract()), {"GreatMaoNetBee", "GreatMaoNetUpdater", "libe_sqlite3.so", "appsettings.json"})
        self.assertEqual(stat.S_IMODE((self.root / "payload/GreatMaoNetBee").stat().st_mode), 0o755)

    def test_checksum_failure_does_not_extract(self):
        archive, checksum = self.package()
        archive.write_bytes(archive.read_bytes() + b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
            installer.extract_package(archive, checksum, self.root / "payload", "GreatMaoNetBee")
        self.assertFalse((self.root / "payload").exists())

    def test_unsafe_paths_and_duplicate_entries(self):
        for name in ("../escape", "/absolute", "nested/file", "back\\slash", ".hidden", "C:drive", "greatmaonetbee"):
            with self.subTest(name=name):
                archive, checksum = self.package((name, "bad"))
                with tempfile.TemporaryDirectory(dir=self.root) as staging:
                    with self.assertRaisesRegex(ValueError, "Unsafe"):
                        installer.extract_package(archive, checksum, Path(staging) / "payload", "GreatMaoNetBee")

    def test_symlinks_are_rejected(self):
        entry = zipfile.ZipInfo("link.so")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        with self.assertRaisesRegex(ValueError, "Unsafe"):
            self.extract((entry, "/etc/passwd"))

    def test_missing_updater_is_rejected(self):
        archive = self.root / "GreatMaoNetBee-linux-x64.zip"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("GreatMaoNetBee", "binary")
        checksum = self.root / "checksum"
        checksum.write_text(hashlib.sha256(archive.read_bytes()).hexdigest() + "  " + archive.name)
        with self.assertRaisesRegex(ValueError, "both"):
            installer.extract_package(archive, checksum, self.root / "payload", "GreatMaoNetBee")

    def test_release_requires_stable_tag_and_exact_asset_urls(self):
        release = {"tag_name": "v0.1.1", "draft": False, "prerelease": False, "assets": []}
        for suffix in (".zip", ".zip.sha256"):
            name = "GreatMaoNetBee-linux-x64" + suffix
            release["assets"].append({"name": name, "size": 100, "id": 123,
                "url": "https://api.github.com/repos/kitUIN/GreatMaoHive/releases/assets/123",
                "browser_download_url": "https://github.com/kitUIN/GreatMaoHive/releases/download/v0.1.1/" + name})
        self.assertEqual(installer.release_assets(release, "GreatMaoNetBee")[0], "0.1.1")
        release["assets"][0]["browser_download_url"] = "https://example.com/evil.zip"
        with self.assertRaises(ValueError):
            installer.release_assets(release, "GreatMaoNetBee")
        for field, value in (("draft", True), ("prerelease", True), ("tag_name", "v1.0.0-beta")):
            with self.subTest(field=field), self.assertRaises(ValueError):
                installer.release_assets(dict(release, **{field: value}), "GreatMaoNetBee")

    def test_asset_redirect_does_not_forward_github_credentials(self):
        request = urllib.request.Request("https://api.github.com/repos/kitUIN/GreatMaoHive/releases/assets/123",
                                         headers={"Authorization": "Bearer test-secret"})
        redirected = installer.SafeRedirect().redirect_request(request, None, 302, "Found", {},
                                                               "https://release-assets.githubusercontent.com/file")
        self.assertIsNone(redirected.get_header("Authorization"))
        with self.assertRaises(ValueError):
            installer.SafeRedirect().redirect_request(request, None, 302, "Found", {}, "http://example.com/file")

    def test_config_credentials_and_plaintext_opt_in(self):
        config = {"Bee": {"Id": "bee-01", "Token": "b" * 32, "HiveUrl": "wss://hive.example/agent"}}
        installer.validate_config(config, "bee")
        config["Bee"]["HiveUrl"] = "ws://127.0.0.1:7443/agent"
        with self.assertRaises(ValueError):
            installer.validate_config(config, "bee")
        config["Bee"]["AllowInsecureTransport"] = True
        installer.validate_config(config, "bee")
        hive = {"Urls": "http://127.0.0.1:7443", "Hive": {"AllowInsecureTransport": True,
                "ReadApiKey": "a" * 32, "Bees": [{"Id": "bee-01", "Token": "b" * 32}]}}
        installer.validate_config(hive, "hive")
        hive["Hive"]["Bees"][0]["Token"] = "a" * 32
        with self.assertRaisesRegex(ValueError, "different"):
            installer.validate_config(hive, "hive")

    def test_units_keep_repository_updater_hooks_and_paths(self):
        for role in ("hive", "bee"):
            unit = installer.service_unit(role, "GreatMaoNet" + role.title())
            reference = Path(__file__).parents[1] / "deploy/systemd" / ("greatmaonet" + role + ".service")
            for line in reference.read_text().splitlines():
                if line.startswith(("Exec", "WorkingDirectory", "ReadWritePaths", "Environment=", "User=", "Group=", "Type=")):
                    self.assertIn(line, unit)

    def test_start_failure_restores_files_permissions_and_previous_service(self):
        old = self.root / "installed"
        old.write_text("old binary")
        old.chmod(0o750)
        new = self.root / "downloaded"
        new.write_text("new binary")
        database = self.root / "hive.db"
        database.write_text("user data")
        starts = []

        def command(*args, check=True):
            if args[1] == "start":
                starts.append(args)
                if len(starts) == 1:
                    raise subprocess.CalledProcessError(1, args)
            return subprocess.CompletedProcess(args, 0, stdout="loaded\n", stderr="")

        with patch.object(installer, "run", side_effect=command):
            with self.assertRaises(subprocess.CalledProcessError):
                installer.install_files([(new, old, os.getuid(), os.getgid(), 0o755)], "example.service", self.root)
        self.assertEqual(old.read_text(), "old binary")
        self.assertEqual(stat.S_IMODE(old.stat().st_mode), 0o750)
        self.assertEqual(database.read_text(), "user data")
        self.assertEqual(len(starts), 2)

    def test_copy_failure_leaves_destination_and_cleans_temporary_file(self):
        destination = self.root / "installed"
        destination.write_text("old")
        with self.assertRaises(FileNotFoundError):
            installer.replace_file(self.root / "missing", destination, os.getuid(), os.getgid(), 0o755)
        self.assertEqual(destination.read_text(), "old")
        self.assertEqual(list(self.root.glob(".install-*")), [])

    def test_failed_first_start_removes_new_files_and_disables_service(self):
        source = self.root / "downloaded"
        source.write_text("binary")
        destination = self.root / "installed"
        commands = []

        def command(*args, check=True):
            commands.append(args)
            if args[1] == "start":
                raise subprocess.CalledProcessError(1, args)
            result = 1 if args[1] in ("is-active", "is-enabled") else 0
            return subprocess.CompletedProcess(args, result, stdout="not-found\n", stderr="")

        with patch.object(installer, "run", side_effect=command):
            with self.assertRaises(subprocess.CalledProcessError):
                installer.install_files([(source, destination, os.getuid(), os.getgid(), 0o755)], "example.service", self.root)
        self.assertFalse(destination.exists())
        self.assertIn(("systemctl", "disable", "example.service"), commands)


if __name__ == "__main__":
    unittest.main()
