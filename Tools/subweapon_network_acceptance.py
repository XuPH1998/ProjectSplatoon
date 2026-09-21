"""Run independent Windows players through reproducible local UDP impairment."""
import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path


def hidden_process(args, cwd):
    settings = dict(cwd=cwd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = subprocess.SW_HIDE
        settings.update(startupinfo=startup, creationflags=subprocess.CREATE_NO_WINDOW)
    return subprocess.Popen([str(a) for a in args], **settings)


def read_report(path):
    if not path.exists():
        return {"passed": "False", "error": "No player report"}
    return dict(line.split("=", 1) for line in path.read_text(encoding="utf-8-sig").splitlines() if "=" in line)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--player", type=Path, default=Path("Temp/SubWeapons/Player/InkLan.exe"))
    parser.add_argument("--output", type=Path, default=Path("Reports/SubWeapons/VisualFix/Network"))
    parser.add_argument("--scenarios", nargs="*", type=int, default=[0, 100, 200])
    args = parser.parse_args()
    root = Path.cwd()
    results = []
    args.output.mkdir(parents=True, exist_ok=True)
    for index, rtt in enumerate(args.scenarios):
        directory = (args.output / f"rtt-{rtt}").resolve()
        directory.mkdir(parents=True, exist_ok=True)
        stop = directory / "stop-proxy"
        if stop.exists():
            stop.unlink()
        host_port, proxy_port = 20630 + index * 2, 20631 + index * 2
        loss, jitter = (0, 0) if rtt == 0 else (5, rtt / 10)
        proxy = hidden_process([sys.executable, root / "Tools/subweapon_network_proxy.py", "--host", host_port,
            "--listen", proxy_port, "--rtt", rtt, "--jitter", jitter, "--loss", loss,
            "--seconds", 205, "--output", directory / "proxy.json", "--stop-file", stop], root)
        players = []
        started = time.monotonic()
        try:
            for role, port in [("host", host_port), ("client", proxy_port)]:
                report = directory / f"{role}.txt"
                if report.exists():
                    report.unlink()
                players.append(hidden_process([args.player.resolve(), "-batchmode", "-force-d3d11",
                    "-screen-width", 960, "-screen-height", 540, "-subWeaponRole", role,
                    "-subWeaponPort", port, "-subWeaponOutput", report,
                    "-logFile", directory / f"{role}.log"], root))
            while any(p.poll() is None for p in players) and time.monotonic() - started < 195:
                (args.output / "progress.json").write_text(json.dumps({"rtt": rtt, "loss": loss,
                    "elapsed_seconds": round(time.monotonic() - started),
                    "exit_codes": [p.poll() for p in players]}), encoding="utf-8")
                time.sleep(.5)
        finally:
            for player in players:
                if player.poll() is None:
                    player.terminate()
                    player.wait(timeout=10)
            stop.write_text("finished", encoding="utf-8")
            try:
                proxy.wait(timeout=10)
            except subprocess.TimeoutExpired:
                proxy.terminate()
                proxy.wait(timeout=10)
        host, client = read_report(directory / "host.txt"), read_report(directory / "client.txt")
        same_paint = host.get("finalPaintHash") == client.get("finalPaintHash") and host.get("finalPaintSequence") == client.get("finalPaintSequence")
        passed = host.get("passed") == client.get("passed") == "True" and same_paint
        results.append({"rtt_ms": rtt, "jitter_ms": jitter, "loss_percent": loss, "passed": passed,
                        "same_paint": same_paint, "host": host, "client": client})
        (args.output / "results.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
        print(f"RTT {rtt}: passed={passed}, matching paint={same_paint}", flush=True)
    return 0 if all(r["passed"] for r in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
