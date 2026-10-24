"""Run the real status path under a disposable controlling terminal on macOS."""

import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile


def check(command):
    import pty

    # Do not inherit buffered output into the forked terminal owner.
    sys.stdout.flush()
    sys.stderr.flush()
    owner, terminal = pty.fork()
    if owner == 0:
        child = None
        try:
            # Providers can ignore job-control signals. A helper must still leave
            # their controlling terminal alone while reading shell configuration.
            signal.signal(signal.SIGTTIN, signal.SIG_IGN)
            signal.signal(signal.SIGTTOU, signal.SIG_IGN)
            with tempfile.TemporaryDirectory(prefix="steam-shell-test-") as folder:
                Path(folder, ".zshrc").write_text("export STEAMCMD_USER=foreground-test-user\n")
                environment = dict(os.environ, SHELL="/bin/zsh", ZDOTDIR=folder)
                environment.pop("STEAMCMD_USER", None)
                before = os.tcgetpgrp(0)
                child = subprocess.Popen(
                    [*command, "status"],
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    env=environment,
                    process_group=0,
                )
                output, _ = child.communicate(timeout=12)
                result = {
                    "exit": child.returncode,
                    "usernameRead": b"foreground-test-user" in output,
                    "foregroundPreserved": os.tcgetpgrp(0) == before,
                }
            os.write(1, json.dumps(result).encode() + b"\n")
            os._exit(0)
        except BaseException as error:
            if child is not None and child.poll() is None:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait()
            os.write(1, (type(error).__name__ + "\n").encode())
            os._exit(1)

    output = bytearray()
    try:
        while True:
            try:
                piece = os.read(terminal, 4096)
            except OSError as error:
                if error.errno != 5:
                    raise
                break
            if not piece:
                break
            output.extend(piece)
    finally:
        os.close(terminal)
        _, status = os.waitpid(owner, 0)
    if status != 0:
        raise RuntimeError("The terminal fixture failed: " + output.decode().strip())
    result = json.loads(output)
    if result != {"exit": 0, "usernameRead": True, "foregroundPreserved": True}:
        raise AssertionError("Status changed terminal ownership or lost shell discovery: " + str(result))


if __name__ == "__main__":
    if sys.platform == "darwin":
        check(sys.argv[1:])
