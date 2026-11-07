"""Exercise the real installer's terminal prompts by pressing only Enter."""
import errno
import os
from pathlib import Path
import pty
import re
import select
import signal
import sys
import time


script = str(Path(__file__).with_name("install-linux.sh").resolve())
pid, terminal = pty.fork()
if pid == 0:
    os.execvp("bash", ["bash", script, "bee"])

pending, count, status = b"", 0, None
deadline = time.monotonic() + 300
prompt = re.compile(rb"(?:^|\n)(?:Bee ID|Hive WebSocket URL|Bee name|Bee Token|Allow plaintext|Region|IP)[^\r\n]*: $")
try:
    while time.monotonic() < deadline:
        readable, _, _ = select.select([terminal], [], [], 0.5)
        if readable:
            try:
                data = os.read(terminal, 65536)
            except OSError as error:
                if error.errno != errno.EIO:
                    raise
                break
            if not data:
                break
            sys.stdout.buffer.write(data)
            sys.stdout.buffer.flush()
            pending = (pending + data)[-8192:]
            if prompt.search(pending):
                os.write(terminal, b"\n")
                count += 1
                pending = b""
        ended, result = os.waitpid(pid, os.WNOHANG)
        if ended:
            status = result
            break
    else:
        raise TimeoutError("Enter-only Bee installation timed out")
    if status is None:
        _, status = os.waitpid(pid, 0)
    if not os.WIFEXITED(status) or os.WEXITSTATUS(status) != 0:
        raise RuntimeError("Enter-only Bee installation failed")
    assert count in (6, 7), "Expected every first-install prompt, got {}".format(count)
    print("Enter-only Bee installation passed ({} prompts).".format(count))
finally:
    if status is None:
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        os.waitpid(pid, 0)
    os.close(terminal)
