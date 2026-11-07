"""Run the shipped native binaries on Debian 11, including TLS and SQLite."""
import ctypes
import json
import os
from pathlib import Path
import secrets
import ssl
import subprocess
import tempfile
import time
import urllib.request


root = Path(__file__).resolve().parents[1]
source = (root / "scripts/install-linux.sh").read_text()
dependencies = source.split("# BEGIN DEPENDENCY SETUP\n", 1)[1].split("# END DEPENDENCY SETUP", 1)[0]
subprocess.run(["bash", "-ec", dependencies], check=True)
assert ssl.OPENSSL_VERSION.startswith("OpenSSL 1.1."), ssl.OPENSSL_VERSION

with tempfile.TemporaryDirectory(prefix="greatmao-compat-") as temporary:
    work = Path(temporary)
    certificate, key, pfx = (work / name for name in ("cert.pem", "key.pem", "cert.pfx"))
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                    "-subj", "/CN=localhost", "-addext", "subjectAltName=DNS:localhost",
                    "-keyout", str(key), "-out", str(certificate)], check=True, capture_output=True)
    subprocess.run(["openssl", "pkcs12", "-export", "-out", str(pfx), "-inkey", str(key),
                    "-in", str(certificate), "-passout", "pass:compat-test"], check=True)
    read_key, token = secrets.token_hex(32), secrets.token_hex(32)
    processes, logs = [], []
    import socket
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
    base = "https://localhost:" + str(port)
    context = ssl.create_default_context(cafile=str(certificate))
    try:
        for role in ("Hive", "Bee"):
            application = "GreatMaoNet" + role
            directory = root / "artifacts/publish" / application / "linux-x64"
            executable = directory / application
            executable.chmod(0o755)
            subprocess.run([str(executable), "--version"], check=True)
            updater = directory / "GreatMaoNetUpdater"
            updater.chmod(0o755)
            subprocess.run([str(updater), "--process-identity", str(os.getpid())], check=True)
            sqlite = ctypes.CDLL(str(directory / "libe_sqlite3.so"))
            sqlite.sqlite3_libversion.restype = ctypes.c_char_p
            assert sqlite.sqlite3_libversion() == b"3.53.4"
            environment = dict(os.environ, Updates__Enabled="false", SSL_CERT_FILE=str(certificate))
            if role == "Hive":
                environment.update(URLS=base, Hive__DatabasePath=str(work / "hive.db"),
                                   Hive__ReadApiKey=read_key, Hive__Bees__0__Id="compat-bee",
                                   Hive__Bees__0__Token=token,
                                   Kestrel__Certificates__Default__Path=str(pfx),
                                   Kestrel__Certificates__Default__Password="compat-test")
            else:
                environment.update(Bee__HiveUrl=base.replace("https:", "wss:") + "/agent",
                                   Bee__Id="compat-bee", Bee__Token=token,
                                   Bee__DatabasePath=str(work / "bee.db"))
            log = (work / (role + ".log")).open("w+")
            logs.append(log)
            processes.append(subprocess.Popen([str(executable)], cwd=directory, env=environment,
                                              stdout=log, stderr=subprocess.STDOUT))
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            if any(process.poll() is not None for process in processes):
                raise RuntimeError("A native process exited before connecting")
            try:
                request = urllib.request.Request(base + "/api/v1/bees/compat-bee",
                                                 headers={"Authorization": "Bearer " + read_key})
                with urllib.request.urlopen(request, context=context, timeout=3) as response:
                    bee = json.load(response)
                if bee["online"] and bee.get("totals") and bee.get("lastSampledAt"):
                    break
            except (OSError, ValueError):
                pass
            time.sleep(0.5)
        else:
            raise RuntimeError("Bee did not connect over WSS with persisted samples")
        assert (work / "hive.db").stat().st_size > 0
        assert (work / "bee.db").stat().st_size > 0
        print("Debian 11 passed: installer dependencies, both native applications, updater, SQLite 3.53.4 and verified HTTPS/WSS.")
    except BaseException:
        for log in logs:
            log.flush()
            log.seek(0)
            print(log.read())
        raise
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
        for process in processes:
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
        for log in logs:
            log.close()
