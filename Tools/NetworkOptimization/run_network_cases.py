"""Run isolated development players through the UDP impairment proxy."""
import argparse
import json
import os
import re
from pathlib import Path
import socket
import subprocess
import sys
import time


def free_port():
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.bind(("127.0.0.1", 0)); return sock.getsockname()[1]


def run_case(exe, output, rtt, loss, players=2, late=False, seconds=50, scenario="inkperf", reconnect=False, playing=False):
    output.mkdir(parents=True, exist_ok=True)
    upstream, front = free_port(), free_port()
    handles, processes = [], []
    hidden = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    def start(args, name):
        log = (output / (name + ".stdout.txt")).open("w", encoding="utf-8")
        handles.append(log)
        process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, creationflags=hidden, cwd=exe.parent)
        processes.append((name, process)); return process
    try:
        start([sys.executable, str(Path(__file__).with_name("udp_proxy.py")), "--listen", str(front), "--target", str(upstream),
               "--rtt", str(rtt), "--jitter", "10" if rtt else "0", "--loss", str(loss),
               "--seconds", str(seconds + 12), "--output", str(output / "proxy.json")], "proxy")
        common = [str(exe), "-batchmode", "-screen-width", "640", "-screen-height", "360", "-force-d3d11",
                  "-inkSmokeCase", scenario, "-lanAddress", "127.0.0.1", "-networkProbe"]
        round_args = ["-inkPerfRound"] if playing else []
        start(common + [str(seconds), "-lanSmokeHost", "-lanPort", str(upstream), "-inkLabel", "host",
                        "-networkProbeOutput", str(output / "host.json"), "-logFile", str(output / "host.log")] + round_args, "host")
        begin = time.monotonic()
        # Join after the host has actually entered the room, not merely after process launch.
        host_ready = False
        while time.monotonic() - begin < 45:
            log_path = output / "host.log"
            if log_path.exists() and "[SMOKE] Connected" in log_path.read_text(encoding="utf-8", errors="replace"):
                host_ready = True
                break
            time.sleep(.25)
        if not host_ready:
            raise RuntimeError("Host did not enter the room within 45 seconds.")
        ready_at = time.monotonic()
        late_evidence = None
        for i in range(1, players):
            if late and i == players - 1:
                # Require actual combat progress; process startup can consume the old 20s delay.
                while time.monotonic() - begin < seconds - 20:
                    log = (output / "host.log").read_text(encoding="utf-8", errors="replace")
                    samples = re.findall(r"phase=(\w+) round=(\d+) players=(\d+)[^\r\n]*paintSeq=(\d+)", log)
                    if samples:
                        phase, round_id, count, sequence = samples[-1]
                        count, sequence = int(count), int(sequence)
                        if time.monotonic() - ready_at >= 20 and count == players - 1 and sequence >= 1500 and (not playing or phase == "Playing"):
                            late_evidence = {"phase": phase, "round": int(round_id), "players": count, "paintSequence": sequence, "secondsAfterHostReady": time.monotonic() - ready_at}
                            break
                    time.sleep(.25)
                if late_evidence is None:
                    raise RuntimeError("The room did not reach seven active players and 1500 paint stamps before late join.")
            duration = max(12, seconds - (time.monotonic() - begin) - 3)
            name = f"client-{i}"
            cycle = ["-lanLeaveAfter", "35", "-lanCycles", "1"] if reconnect else []
            start(common + [str(duration), "-lanSmokeClient", "-lanPort", str(front), "-inkLabel", name,
                            "-networkProbeOutput", str(output / f"{name}.json"), "-logFile", str(output / f"{name}.log")] + cycle, name)
            time.sleep(.4)
        deadline = begin + seconds + 35
        while any(process.poll() is None for _, process in processes) and time.monotonic() < deadline:
            time.sleep(.5)
        codes = {}
        for name, process in processes:
            if process.poll() is None:
                process.terminate(); process.wait(timeout=10)
            codes[name] = process.returncode
        result = {"rtt_ms": rtt, "loss_percent": loss, "players": players, "late_join": late,
                  "scenario": scenario, "reconnect": reconnect, "playing": playing, "late_join_evidence": late_evidence, "exit_codes": codes}
        result["reports"] = {}
        for name, _ in processes:
            path = output / f"{name}.json"
            if path.exists():
                result["reports"][name] = json.loads(path.read_text(encoding="utf-8-sig"))
        result["passed"] = all(code == 0 for code in codes.values()) and all(name in result["reports"] and result["reports"][name].get("connected") and
                               result["reports"][name].get("initialSyncComplete") and result["reports"][name].get("errors") == 0
                               and result["reports"][name].get("players", 0) >= players
                               for name, _ in processes if name != "proxy")
        if scenario == "inkperf":
            shots = result["reports"].get("host", {}).get("authoritativeShots", [])
            result["all_players_fired"] = len(shots) >= players and all(player["shots"] > 0 for player in shots)
            result["passed"] = result["passed"] and result["all_players_fired"]
        if scenario == "prediction":
            result["traversal_observed"] = all(all(result["reports"][name].get(key, 0) > 0 for key in
                ("humanSamples", "neutralSwimSamples", "friendlySwimSamples", "deadSamples"))
                for name, _ in processes if name != "proxy" and name in result["reports"])
            client = result["reports"].get("client-1", {})
            result["live_echo_observed"] = client.get("rttSamples", 0) >= 10 and client.get("inputAckSamples", 0) > 0
            result["passed"] = result["passed"] and result["traversal_observed"] and result["live_echo_observed"]
        if reconnect:
            result["reconnected"] = all("[SMOKE] Reconnected after cleanup" in (output / f"client-{i}.log").read_text(encoding="utf-8", errors="replace")
                                        for i in range(1, players))
            result["passed"] = result["passed"] and result["reconnected"]
        if scenario == "combat":
            result["completed_round_and_reset"] = "[SMOKE] Returned to room and started next round" in (output / "host.log").read_text(encoding="utf-8", errors="replace")
            result["passed"] = result["passed"] and result["completed_round_and_reset"]
        (output / "case.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps({"case": str(output), "passed": result["passed"], "exit_codes": codes}), flush=True)
        return result
    finally:
        for _, process in processes:
            if process.poll() is None:
                process.terminate()
        for handle in handles:
            handle.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exe", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--matrix", choices=("smoke", "representative", "full", "eight", "eight-playing", "reconnect", "combat", "prediction"), default="smoke")
    parser.add_argument("--seconds", type=int, default=50)
    args = parser.parse_args()
    cases = [(0, 0, 2, False)]
    if args.matrix == "representative":
        cases = [(0, 0, 2, False), (50, 0, 2, False), (100, 1, 2, False), (200, 3, 2, False)]
    elif args.matrix == "full":
        cases = [(rtt, loss, 2, False) for rtt in (50, 100, 200) for loss in (0, 1, 3)]
    elif args.matrix in ("eight", "eight-playing"):
        cases = [(50, 1, 8, True)]
    elif args.matrix in ("reconnect", "combat"):
        cases = [(100, 1, 2, False)]
    elif args.matrix == "prediction":
        cases = [(0, 0, 2, False), (50, 0, 2, False), (100, 0, 2, False)]
    results = [run_case(args.exe.resolve(), args.output.resolve() / f"rtt{rtt}-loss{loss}-p{players}", rtt, loss, players, late, args.seconds,
                       scenario=args.matrix if args.matrix in ("combat", "prediction") else "inkperf", reconnect=args.matrix == "reconnect", playing=args.matrix == "eight-playing")
               for rtt, loss, players, late in cases]
    (args.output.resolve() / "summary.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    sys.exit(0 if all(result["passed"] for result in results) else 1)
