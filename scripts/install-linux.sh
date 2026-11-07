#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" == --help || "${1:-}" == -h ]]; then
    cat <<'HELP'
Usage: sudo bash install-linux.sh [hive|bee] [--config /path/appsettings.Local.json]

Downloads the latest stable kitUIN/GreatMaoHive release and installs a systemd service.
Without --config, first installation asks for configuration interactively.
Re-running preserves existing configuration and databases. --config is only for first setup.
The repository is public; no GitHub account or token is required.
Requires Linux x86_64, glibc, systemd and root. Python/runtime dependencies are installed
with apt-get or dnf when missing. No .NET SDK/runtime is required.
HELP
    exit 0
fi
[[ $(uname -s) == Linux && $(uname -m) == x86_64 ]] || { echo 'Only Linux x86_64 is supported.' >&2; exit 1; }
[[ $EUID == 0 ]] || { echo 'Run with sudo bash install-linux.sh ...' >&2; exit 1; }
[[ -d /run/systemd/system ]] && command -v systemctl >/dev/null || { echo 'A running systemd is required.' >&2; exit 1; }
# BEGIN DEPENDENCY SETUP
runtime_dependencies_available() {
    python3 - <<'DEPENDENCIES'
import ctypes
import ssl
import zlib
for name in ("libssl.so.3", "libssl.so.1.1"):
    try:
        ctypes.CDLL(name)
        break
    except OSError:
        pass
else:
    raise SystemExit(1)
DEPENDENCIES
}
if ! runtime_dependencies_available >/dev/null 2>&1; then
    if command -v apt-get >/dev/null; then
        apt-get update
        openssl_package=
        for candidate in libssl3t64 libssl3 libssl1.1; do
            if LC_ALL=C apt-cache policy "$candidate" | awk '$1 == "Candidate:" && $2 != "(none)" { found = 1 } END { exit !found }'; then
                openssl_package=$candidate
                break
            fi
        done
        if [[ -z "$openssl_package" ]]; then
            echo 'OpenSSL 1.1 or 3 is required, but the configured apt repositories provide none of libssl3t64, libssl3 or libssl1.1.' >&2
            exit 1
        fi
        apt-get install -y python3 ca-certificates "$openssl_package" zlib1g
    elif command -v dnf >/dev/null; then
        dnf install -y python3 ca-certificates openssl-libs zlib shadow-utils
    else
        echo 'Install Python 3, CA certificates, OpenSSL 1.1 or 3 and zlib, then retry.' >&2
        exit 1
    fi
    if ! runtime_dependencies_available >/dev/null 2>&1; then
        echo 'Python 3, zlib or a usable OpenSSL 1.1/3 library is still missing after package installation.' >&2
        exit 1
    fi
fi
# END DEPENDENCY SETUP

python3 - "$@" <<'PYTHON'
# BEGIN INSTALLER PYTHON
import argparse
import contextlib
import fcntl
import getpass
import grp
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import pwd
import re
import secrets
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.error
import urllib.request
import zipfile

REPOSITORY = "kitUIN/GreatMaoHive"
MAX_PACKAGE = 512 * 1024 * 1024


def run(*args, check=True):
    return subprocess.run(args, check=check, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=120)


class SafeRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        if urllib.parse.urlsplit(new_url).scheme != "https":
            raise ValueError("Download redirected to a non-HTTPS address")
        redirected = super().redirect_request(request, fp, code, message, headers, new_url)
        if redirected is not None and urllib.parse.urlsplit(request.full_url).netloc != urllib.parse.urlsplit(new_url).netloc:
            redirected.remove_header("Authorization")
        return redirected


def download(url, target, limit, expected_size=None):
    for attempt in range(3):
        try:
            headers = {"User-Agent": "GreatMaoNet-installer"}
            if urllib.parse.urlsplit(url).netloc == "api.github.com":
                headers["Accept"] = "application/vnd.github+json"
                headers["X-GitHub-Api-Version"] = "2022-11-28"
            request = urllib.request.Request(url, headers=headers)
            with urllib.request.build_opener(SafeRedirect()).open(request, timeout=60) as response, target.open("wb") as output:
                if urllib.parse.urlsplit(response.url).scheme != "https":
                    raise ValueError("Download redirected to a non-HTTPS address")
                size = 0
                while True:
                    chunk = response.read(65536)
                    if not chunk:
                        break
                    size += len(chunk)
                    if size > limit:
                        raise ValueError("Download exceeds size limit")
                    output.write(chunk)
            if expected_size is not None and size != expected_size:
                raise ValueError("Incomplete release download")
            return
        except (OSError, ValueError):
            if attempt == 2:
                raise
            time.sleep(2)


def release_assets(release, application):
    tag = release.get("tag_name", "")
    if release.get("draft") or release.get("prerelease") or not re.fullmatch(r"v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", tag):
        raise ValueError("Expected a stable vMAJOR.MINOR.PATCH release")
    assets = []
    for name, limit in [(application + "-linux-x64.zip", MAX_PACKAGE), (application + "-linux-x64.zip.sha256", 1024)]:
        matches = [asset for asset in release.get("assets", []) if asset.get("name") == name]
        if len(matches) != 1:
            raise ValueError("Release missing a unique asset: " + name)
        asset = matches[0]
        expected = "https://github.com/{}/releases/download/{}/{}".format(REPOSITORY, tag, name)
        expected_api = "https://api.github.com/repos/{}/releases/assets/{}".format(REPOSITORY, asset.get("id"))
        if (asset.get("browser_download_url") != expected or not 0 < asset.get("size", 0) <= limit or
                type(asset.get("id")) is not int or asset.get("url") != expected_api):
            raise ValueError("Invalid release asset: " + name)
        assets.append(asset)
    return tag[1:], assets


def extract_package(archive, checksum, destination, application):
    parts = checksum.read_text().strip().split()
    if len(parts) != 2 or parts[1] != archive.name or not re.fullmatch(r"[a-fA-F0-9]{64}", parts[0]):
        raise ValueError("Invalid SHA-256 checksum file")
    digest = hashlib.sha256()
    with archive.open("rb") as source:
        for chunk in iter(lambda: source.read(65536), b""):
            digest.update(chunk)
    if digest.hexdigest() != parts[0].lower():
        raise ValueError("Release SHA-256 mismatch; nothing was installed")
    destination.mkdir()
    names, total, installed = set(), 0, []
    with zipfile.ZipFile(archive) as package:
        for entry in package.infolist():
            name = entry.filename
            total += entry.file_size
            if (not name or name.startswith(".") or name.endswith((".", " ")) or
                    any(character in name for character in ("/", "\\", ":", "\n", "\r")) or
                    stat.S_ISLNK(entry.external_attr >> 16) or name.lower() in names or
                    total > MAX_PACKAGE * 2 or len(names) >= 200):
                raise ValueError("Unsafe release archive entry")
            names.add(name.lower())
            if name not in (application, "GreatMaoNetUpdater", "appsettings.json") and not name.endswith((".so", ".dll", ".deps.json", ".runtimeconfig.json")):
                continue
            with package.open(entry) as source, (destination / name).open("xb") as output:
                shutil.copyfileobj(source, output)
            (destination / name).chmod(0o755 if name in (application, "GreatMaoNetUpdater") else 0o644)
            installed.append(name)
    if not {application, "GreatMaoNetUpdater"}.issubset(installed):
        raise ValueError("Release must contain both the application and updater")
    return installed


def ask(label, default="", secret=False):
    prompt = label + (" [{}]".format(default) if default else "") + ": "
    value = getpass.getpass(prompt) if secret else input(prompt)
    return value.strip() or default


def validate_config(config, role):
    section = config.get(role.title(), {})
    if role == "bee":
        nodes = [section]
        url = urllib.parse.urlsplit(section.get("HiveUrl", ""))
        if not url.hostname or url.scheme not in (("wss", "ws") if section.get("AllowInsecureTransport") is True else ("wss",)):
            raise ValueError("Bee.HiveUrl must use wss (ws requires AllowInsecureTransport=true)")
        region, ip = section.get("Region"), section.get("Ip")
        if region and len(region.strip()) > 256:
            raise ValueError("Bee.Region must be at most 256 characters")
        if ip:
            ipaddress.ip_address(ip)
    else:
        nodes = section.get("Bees", [])
        key = section.get("ReadApiKey", "")
        if len(key) < 32:
            raise ValueError("Hive.ReadApiKey must contain at least 32 characters")
        if not config.get("Urls"):
            raise ValueError("Hive configuration must specify Urls")
        for address in config["Urls"].split(";"):
            scheme = urllib.parse.urlsplit(address).scheme
            if scheme != "https" and not (scheme == "http" and section.get("AllowInsecureTransport") is True):
                raise ValueError("HTTP requires Hive.AllowInsecureTransport=true")
    if not nodes or any(not isinstance(node.get("Id"), str) or not node["Id"].strip() or len(node["Id"]) > 128 or len(node.get("Token", "")) < 32 for node in nodes):
        raise ValueError("Each Bee needs an ID (1..128 characters) and Token (at least 32 characters)")
    if len({node["Id"] for node in nodes}) != len(nodes):
        raise ValueError("Bee IDs must be unique")
    tokens = [node["Token"] for node in nodes] + ([key] if role == "hive" else [])
    if len(set(tokens)) != len(tokens):
        raise ValueError("API key and all Bee tokens must be different")


def interactive_config(role):
    node_id = ask("Bee ID", "bee-01")
    if role == "bee":
        url = ask("Hive WebSocket URL (wss://host:7443/agent)")
        config = {"Bee": {"Id": node_id, "Name": ask("Bee name", os.uname().nodename),
                          "HiveUrl": url, "Token": ask("Bee Token configured on Hive", secret=True)}}
        if url.startswith("ws://"):
            config["Bee"]["AllowInsecureTransport"] = ask("Allow plaintext ws transport? Type yes", "no") == "yes"
        config["Bee"]["Region"] = ask("Region (optional)") or None
        config["Bee"]["Ip"] = ask("IP (optional)") or None
    else:
        config = {"Hive": {"ReadApiKey": secrets.token_hex(32), "Bees": [{"Id": node_id, "Token": secrets.token_hex(32)}]}}
        mode = ask("Hive transport: https or local-proxy", "https")
        if mode == "https":
            config["Urls"] = ask("HTTPS listen address", "https://0.0.0.0:7443")
            certificate = Path(ask("Existing PFX certificate absolute path"))
            if not certificate.is_absolute() or not certificate.is_file():
                raise ValueError("An existing absolute PFX certificate path is required")
            config["Kestrel"] = {"Certificates": {"Default": {"Path": str(certificate), "Password": ask("PFX password", secret=True)}}}
        elif mode == "local-proxy":
            config["Urls"] = "http://127.0.0.1:7443"
            config["Hive"]["AllowInsecureTransport"] = True
            print("Listening on localhost only. Configure your HTTPS reverse proxy with WebSocket support separately.")
        else:
            raise ValueError("Transport must be https or local-proxy")
    return config


def service_unit(role, application):
    # Keep these paths and updater hooks aligned with deploy/systemd/*.service.
    return """[Unit]
Description={application} network monitoring
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=greatmao
Group=greatmao
WorkingDirectory=/opt/greatmao/{role}
ExecStart=/opt/greatmao/{role}/{application}
ExecStopPost=/opt/greatmao/{role}/GreatMaoNetUpdater /opt/greatmao/{role} /var/lib/greatmao/{role}/updates
Environment=GREATMAO_SYSTEMD_UPDATER=1
Environment=Updates__DownloadDirectory=/var/lib/greatmao/{role}/updates
EnvironmentFile=-/etc/greatmao/{role}.env
Restart=always
RestartSec=5
TimeoutStopSec=30
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/greatmao/{role}
ReadWritePaths=/opt/greatmao/{role}

[Install]
WantedBy=multi-user.target
""".format(role=role, application=application)


def replace_file(source, destination, uid, gid, mode):
    if destination.is_symlink():
        raise ValueError("Refusing to replace a symbolic link: " + str(destination))
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=destination.parent, prefix=".install-", delete=False) as output:
            temporary = Path(output.name)
            with source.open("rb") as input_file:
                shutil.copyfileobj(input_file, output)
        os.chown(temporary, uid, gid)
        temporary.chmod(mode)
        os.replace(temporary, destination)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def install_files(changes, service, work):
    backups = work / "backup"
    backups.mkdir()
    changed = []
    active = run("systemctl", "is-active", "--quiet", service, check=False).returncode == 0
    enabled = run("systemctl", "is-enabled", "--quiet", service, check=False).returncode == 0
    # ExecStopPost must complete before files are replaced.
    if run("systemctl", "show", "-p", "LoadState", "--value", service).stdout.strip() != "not-found":
        run("systemctl", "stop", service)
    try:
        for source, destination, uid, gid, mode in changes:
            backup = backups / str(len(changed))
            if destination.is_symlink():
                raise ValueError("Refusing a symbolic link: " + str(destination))
            existed = destination.exists()
            if existed:
                shutil.copy2(destination, backup)
                info = destination.stat()
                os.chown(backup, info.st_uid, info.st_gid)
            changed.append((destination, backup if existed else None))
            replace_file(source, destination, uid, gid, mode)
        run("systemctl", "daemon-reload")
        run("systemctl", "enable", service)
        run("systemctl", "start", service)
        # Type=notify waits for application startup; also detect immediate restart loops.
        time.sleep(3)
        run("systemctl", "is-active", "--quiet", service)
        if run("systemctl", "show", "-p", "NRestarts", "--value", service).stdout.strip() != "0":
            raise RuntimeError("Service restarted during installation")
    except BaseException:
        run("systemctl", "stop", service, check=False)
        if not enabled:
            run("systemctl", "disable", service, check=False)
        for destination, backup in reversed(changed):
            if backup:
                info = backup.stat()
                replace_file(backup, destination, info.st_uid, info.st_gid, stat.S_IMODE(info.st_mode))
            else:
                destination.unlink(missing_ok=True)
        run("systemctl", "daemon-reload", check=False)
        if active:
            run("systemctl", "start", service, check=False)
        print("Installation failed; changed files were restored. Check journalctl -u " + service, file=sys.stderr)
        raise


def main():
    parser = argparse.ArgumentParser(description="Install latest GreatMaoNet release as a systemd service")
    parser.add_argument("role", choices=("hive", "bee"), nargs="?")
    parser.add_argument("--config", type=Path, help="Initial appsettings.Local.json (existing configuration is never overwritten)")
    args = parser.parse_args()
    os.umask(0o077)
    with contextlib.ExitStack() as stack:
        lock = stack.enter_context(open("/run/lock/greatmao-install.lock", "w"))
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if not args.role:
            sys.stdin = stack.enter_context(open("/dev/tty"))
            args.role = ask("Install hive or bee", "bee").lower()
            if args.role not in ("hive", "bee"):
                raise ValueError("Role must be hive or bee")
        role = args.role
        application = "GreatMaoNet" + role.title()
        service = "greatmaonet" + role + ".service"
        install = Path("/opt/greatmao") / role
        data = Path("/var/lib/greatmao") / role
        config_path = install / "appsettings.Local.json"
        legacy_env = Path("/etc/greatmao") / (role + ".env")
        existing_config = config_path.exists() or legacy_env.exists()
        if args.config and existing_config:
            raise ValueError("Existing configuration found; omit --config to preserve it. Edit it separately to change settings.")
        config = None
        if not existing_config:
            if args.config:
                config = json.loads(args.config.read_text(encoding="utf-8-sig"))
            else:
                try:
                    sys.stdin = stack.enter_context(open("/dev/tty"))
                except OSError:
                    raise ValueError("First installation needs a terminal or --config /path/appsettings.Local.json") from None
                config = interactive_config(role)
            validate_config(config, role)
            config[role.title()].setdefault("DatabasePath", str(data / (role + ".db")))
        work = Path(stack.enter_context(tempfile.TemporaryDirectory(prefix="greatmao-install-")))
        metadata = work / "release.json"
        print("Fetching latest stable release...", flush=True)
        download("https://api.github.com/repos/" + REPOSITORY + "/releases/latest", metadata, 1024 * 1024)
        version, assets = release_assets(json.loads(metadata.read_text()), application)
        print("Downloading {} {} (linux-x64)...".format(application, version), flush=True)
        for asset in assets:
            download(asset["browser_download_url"], work / asset["name"],
                     MAX_PACKAGE if asset["name"].endswith(".zip") else 1024, asset["size"])
        payload = work / "payload"
        files = extract_package(work / assets[0]["name"], work / assets[1]["name"], payload, application)
        try:
            actual = run(str(payload / application), "--version").stdout.strip()
        except (OSError, subprocess.CalledProcessError):
            raise RuntimeError("Release cannot run on this Linux distribution. Check glibc/OpenSSL requirements; existing service was not stopped.") from None
        if actual != version:
            raise ValueError("Binary version does not match release")
        if (install / application).exists():
            current = run(str(install / application), "--version").stdout.strip()
            if tuple(map(int, current.split("."))) > tuple(map(int, version.split("."))):
                raise ValueError("Installed version is newer; refusing downgrade")
        if run("getent", "group", "greatmao", check=False).returncode:
            run("groupadd", "--system", "greatmao")
        if run("id", "-u", "greatmao", check=False).returncode:
            run("useradd", "--system", "--gid", "greatmao", "--no-create-home", "--home-dir", "/nonexistent", "--shell", "/usr/sbin/nologin", "greatmao")
        uid, gid = pwd.getpwnam("greatmao").pw_uid, grp.getgrnam("greatmao").gr_gid
        for parent in (Path("/opt/greatmao"), Path("/var/lib/greatmao"), Path("/etc/greatmao")):
            if parent.is_symlink():
                raise ValueError("Installation directories must not be symlinks")
            parent.mkdir(exist_ok=True)
            os.chown(parent, 0, gid)
            parent.chmod(0o750)
        for directory in (install, data, data / "updates"):
            if directory.is_symlink():
                raise ValueError("Installation directories must not be symlinks")
            directory.mkdir(parents=True, exist_ok=True)
            os.chown(directory, uid, gid)
            directory.chmod(0o750)
        # Cooperate with the application's FileShare.None lock and refuse incomplete updates.
        update_lock = stack.enter_context((data / "updates" / "update.lock").open("a"))
        os.chown(update_lock.name, uid, gid)
        fcntl.flock(update_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if (data / "updates" / "pending.json").exists():
            raise ValueError("A pending automatic update exists; inspect updater.log before installing")
        changes = []
        for name in files:
            destination = install / name
            if name == "appsettings.json" and destination.exists():
                continue
            changes.append((payload / name, destination, uid, gid, (payload / name).stat().st_mode & 0o777))
        if config is not None:
            # Copy PFX out of /home/root directories hidden by ProtectHome, without exposing its password.
            certificate = config.get("Kestrel", {}).get("Certificates", {}).get("Default", {})
            if certificate.get("Path"):
                source = Path(certificate["Path"])
                if not source.is_absolute() or not source.is_file():
                    raise ValueError("Certificate Path must be an existing absolute file path")
                cert_directory = Path("/etc/greatmao/certificates")
                cert_directory.mkdir(parents=True, exist_ok=True)
                cert_directory.chmod(0o750)
                os.chown(cert_directory, 0, gid)
                target = cert_directory / (role + ".pfx")
                certificate["Path"] = str(target)
                changes.append((source, target, 0, gid, 0o640))
            staged_config = work / "appsettings.Local.json"
            staged_config.write_text(json.dumps(config, ensure_ascii=False, indent=2) + "\n")
            changes.append((staged_config, config_path, 0, gid, 0o640))
        staged_unit = work / service
        staged_unit.write_text(service_unit(role, application))
        changes.append((staged_unit, Path("/etc/systemd/system") / service, 0, 0, 0o644))
        install_files(changes, service, work)
        print("Installed {} {}. Service enabled and running.".format(application, version))
        print("Config: {}{}".format(config_path, " (existing environment overrides preserved)" if legacy_env.exists() else ""))
        print("Status: systemctl status " + service)
        print("Logs: journalctl -u " + service + " -f")
        if role == "hive" and config is not None:
            print("ReadApiKey and Bee Token are in {} (sudo required); use that Bee ID/Token when installing Bee.".format(config_path))
        if role == "bee":
            print("Service running does not confirm Hive connectivity; look for 'Connected to Hive' in its logs.")
        print("Automatic updates check the public repository on startup and every 6 hours by default; existing update settings are preserved.")


if __name__ == "__main__":
    try:
        main()
    except urllib.error.HTTPError as error:
        print("GitHub download failed (HTTP {}). Check network access and release availability; if rate-limited, retry later. No GitHub token is required.".format(error.code), file=sys.stderr)
        sys.exit(1)
    except (Exception, KeyboardInterrupt) as error:
        # Do not dump configuration, environment, or tokens on failure.
        print("Install failed: " + str(error), file=sys.stderr)
        sys.exit(1)
# END INSTALLER PYTHON
PYTHON
