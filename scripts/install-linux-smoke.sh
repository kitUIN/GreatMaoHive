#!/usr/bin/env bash
# Run only on a disposable CI machine: installs real systemd services and release binaries.
set -euo pipefail
bee_setup=${1:-local-hive}
[[ $bee_setup == local-hive || $bee_setup == generated ]] || exit 1
[[ $EUID == 0 ]] || { echo 'This smoke test requires root.' >&2; exit 1; }
for path in /opt/greatmao /var/lib/greatmao /etc/greatmao \
    /etc/systemd/system/greatmaonethive.service /etc/systemd/system/greatmaonetbee.service; do
    [[ ! -e "$path" ]] || { echo "Refusing to test over an existing installation: $path" >&2; exit 1; }
done
work=$(mktemp -d)
cleanup() {
    if [[ $? != 0 ]]; then
        journalctl --no-pager -n 80 -u greatmaonetbee -u greatmaonethive || true
    fi
    systemctl disable --now greatmaonetbee greatmaonethive >/dev/null 2>&1 || true
    rm -rf -- "$work"
}
trap cleanup EXIT
if [[ $bee_setup == generated ]]; then
    # A fresh machine must deploy successfully without a Hive URL or token.
    python3 scripts/install-bee-enter-smoke.py
    systemctl is-active --quiet greatmaonetbee
fi
python3 - "$work" "$bee_setup" <<'PY'
import json
from pathlib import Path
import secrets
import sys
work = Path(sys.argv[1])
key, token = secrets.token_hex(32), secrets.token_hex(32)
if sys.argv[2] == 'generated':
    bee_path = Path('/opt/greatmao/bee/appsettings.Local.json')
    config = json.loads(bee_path.read_text())
    assert config['Bee']['HiveUrl'] == 'wss://localhost:7443/agent'
    token = config['Bee']['Token']
    assert len(token) == 64
    # Complete registration after deployment, just as an operator would.
    config['Bee']['HiveUrl'] = 'ws://127.0.0.1:17443/agent'
    config['Bee']['AllowInsecureTransport'] = True
    bee_path.write_text(json.dumps(config))
(work / 'hive.json').write_text(json.dumps({'Urls': 'http://127.0.0.1:17443', 'Hive': {
    'AllowInsecureTransport': True, 'ReadApiKey': key, 'Bees': [{'Id': 'bee-01', 'Token': token}]}}))
PY
bash scripts/install-linux.sh hive --config "$work/hive.json"
if [[ $bee_setup == local-hive ]]; then
    # Pressing Enter must reuse the local Hive listener and matching credential.
    python3 scripts/install-bee-enter-smoke.py
else
    systemctl restart greatmaonetbee
fi
sha256sum /opt/greatmao/{hive,bee}/appsettings.Local.json > "$work/config.sha256"
printf 'preserve hive data\n' > /var/lib/greatmao/hive/installer-sentinel
printf 'preserve bee data\n' > /var/lib/greatmao/bee/installer-sentinel
# Re-running must not prompt, replace configuration, or erase data.
bash scripts/install-linux.sh hive </dev/null
bash scripts/install-linux.sh bee </dev/null
sha256sum --check "$work/config.sha256"
test "$(cat /var/lib/greatmao/hive/installer-sentinel)" = 'preserve hive data'
test "$(cat /var/lib/greatmao/bee/installer-sentinel)" = 'preserve bee data'
systemctl is-enabled --quiet greatmaonethive greatmaonetbee
systemctl is-active --quiet greatmaonethive greatmaonetbee
test -s /var/lib/greatmao/hive/hive.db
test -s /var/lib/greatmao/bee/bee.db
python3 - "$work" <<'PY'
import json
from pathlib import Path
import sys
import time
import urllib.request
config = json.loads((Path(sys.argv[1]) / 'hive.json').read_text())
request = urllib.request.Request('http://127.0.0.1:17443/api/v1/bees',
    headers={'Authorization': 'Bearer ' + config['Hive']['ReadApiKey']})
for attempt in range(30):
    with urllib.request.urlopen(request, timeout=5) as response:
        bees = json.load(response)
    if any(bee['id'] == 'bee-01' and bee['online'] for bee in bees):
        break
    time.sleep(2)
else:
    raise RuntimeError('Installed Bee never connected to Hive')
for role in ('hive', 'bee'):
    config = Path('/opt/greatmao') / role / 'appsettings.Local.json'
    assert config.stat().st_mode & 0o777 == 0o640, 'Credential file permissions changed'
print('Installer smoke test passed: both services enabled, Bee online, repeat install preserves configuration and data.')
PY
