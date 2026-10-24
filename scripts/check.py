"""Quiet entry point for the existing build and test targets."""

from pathlib import Path
import subprocess
import sys


root = Path(__file__).resolve().parent.parent
mode = sys.argv[1]
log = root / "artifacts" / "logs" / (mode + ".log")
log.parent.mkdir(parents=True, exist_ok=True)
solution = sys.argv[2] if len(sys.argv) > 2 else "SteamWorkshopAgent.slnx"
commands = [["dotnet", mode, solution, "-c", "Release"]]
if mode == "test":
    commands.append([
        sys.executable,
        "scripts/test-terminal-foreground.py",
        "dotnet",
        "src/SteamWorkshopAgent/bin/Release/net10.0/SteamWorkshopAgent.dll",
    ])
with log.open("w") as output:
    for command in commands:
        try:
            result = subprocess.run(command, cwd=root, stdout=output, stderr=subprocess.STDOUT)
        except OSError as error:
            output.write(str(error) + "\n")
            result = None
        if result is None or result.returncode != 0:
            output.flush()
            print(f"failed: {command[0]} {command[1]}", file=sys.stderr)
            print("\n".join(log.read_text().splitlines()[-20:]), file=sys.stderr)
            print(f"Full log: {log}", file=sys.stderr)
            sys.exit(result.returncode if result and result.returncode > 0 else 1)
print("ok")
