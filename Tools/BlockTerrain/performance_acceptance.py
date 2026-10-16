"""Run the approved block workloads serially with eight independent Players.

The visible host renders the normal 1080p main camera. Seven hidden headless network clients
provide real input and simulation without adding seven unrelated GPU workloads.
This is same-machine evidence, not physical multi-machine acceptance.
"""
import argparse
import hashlib
import json
import math
import os
import re
from pathlib import Path
import subprocess
import time
import uuid

from network_acceptance import (ROOT, background_player_environment, free_port, hidden_process,
                                read_json, require_background_player_desktop, write_json)

CASES = ("steady250k", "combat250k", "tower32m", "blast8", "cascade20k", "scattered100k")
DURATIONS = dict(zip(CASES, (600, 300, 60, 120, 120, 180)))
PROFILES = ("strict", "prototype-v1")
PROTOTYPE_DIAGNOSTIC_GATES = frozenset(("no33msSpikes", "supportQueueClear"))


def profile_durations(profile):
    if profile not in PROFILES:
        raise ValueError(f"Unknown acceptance profile: {profile}")
    durations = dict(DURATIONS)
    if profile == "prototype-v1":
        durations["steady250k"] = 180
    return durations


def profile_targets(profile):
    return {"requiredSeconds": profile_durations(profile),
            "ordinaryPublishP95Ms": 200 if profile == "prototype-v1" else 80,
            "eightBlastCompletionMs": 2000 if profile == "prototype-v1" else 250,
            "frameP95Ms": 15.5, "frameP99Ms": 16.67,
            "frameSpikeBlocking": profile == "strict",
            "supportQueueClearBlocking": profile == "strict",
            "supportQueueClearDiagnosticMs": 500}


def gate_satisfied(gate, profile):
    if not isinstance(gate, dict):
        return False
    if (profile == "prototype-v1" and gate.get("name") in PROTOTYPE_DIAGNOSTIC_GATES
            and gate.get("diagnosticOnly") is True):
        return gate.get("status") in ("pass", "fail", "unverified")
    return gate.get("status") == "pass"


def digest(path):
    if not path.is_file():
        return None
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def exception_headers(path):
    if not path.is_file():
        return None
    log = path.read_text(encoding="utf-8-sig", errors="replace")
    return re.findall(r"(?m)^(?:[\w.]+Exception:.*|Assertion failed.*|Fatal error.*)$", log)


def native_crash_headers(path):
    if not path.is_file():
        return None
    log = path.read_text(encoding="utf-8-sig", errors="replace")
    return re.findall(r"(?m)^[ \t]*(?:Native Crash Reporting|Crash!!!)[ \t]*$", log)


def visible_graphics_process(command, cwd):
    arguments = [str(part) for part in command]
    if "-batchmode" in arguments or "-nographics" in arguments:
        raise ValueError("Graphics host must render its normal Player window")
    require_background_player_desktop()
    options = {"cwd": cwd, "stdout": subprocess.DEVNULL, "stderr": subprocess.DEVNULL,
               "env": background_player_environment()}
    if os.name == "nt":
        startup = subprocess.STARTUPINFO()
        startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startup.wShowWindow = 4  # SW_SHOWNOACTIVATE; normal window on the isolated desktop.
        options["startupinfo"] = startup
    return subprocess.Popen(arguments, **options)


def approved_frame_spike_gate(report, acceptance_profile="strict"):
    """Use every sampled frame and the exact approved 33.3ms ceiling."""
    gate = {"name": "approvedFrameSpikeLimit", "status": "unverified", "limitMs": 33.3,
            "maximumMs": None, "samples": 0,
            "scope": "Conservative full-frame maximum; no terrain attribution waiver.",
            "diagnosticOnly": acceptance_profile == "prototype-v1"}
    metrics = report.get("metrics")
    if not isinstance(metrics, list):
        return gate
    rows = [row for row in metrics if isinstance(row, dict) and row.get("name") == "frameIntervalMs"]
    if len(rows) != 1:
        return gate
    frame = rows[0]
    samples = report.get("samples")
    maximum = frame.get("max")
    if (frame.get("available") is not True or type(samples) is not int or samples <= 0
            or type(frame.get("samples")) is not int or frame["samples"] != samples
            or type(maximum) not in (int, float) or not math.isfinite(maximum) or maximum <= 0):
        return gate
    gate.update(status="pass" if maximum <= gate["limitMs"] else "fail",
                maximumMs=maximum, samples=samples)
    return gate


def run_case(exe, directory, case, diagnostic_seconds=None, capture_main_camera=False, frame_timing_observation=False,
             acceptance_profile="strict", graphics_api="d3d11"):
    durations = profile_durations(acceptance_profile)
    require_background_player_desktop()
    directory.mkdir(parents=True, exist_ok=True)
    duration = diagnostic_seconds if diagnostic_seconds is not None else durations[case]
    port = free_port()
    report_path = directory / "host.performance.json"
    actors, commands, error = {}, [], None
    started = time.monotonic()
    try:
        for index in range(8):
            name = "host" if index == 0 else f"client-{index}"
            command = [exe, "-noSystemCursor", "-lanSmokeHost" if index == 0 else "-lanSmokeClient",
                       "-lanAddress", "127.0.0.1", "-lanPort", port,
                       "-inkSmokeCase", "observer", "-inkLabel", name,
                       "-inkCaptureAfter", "999999", "-logFile", directory / f"{name}.log"]
            if index == 0:
                command += [f"-force-{graphics_api}", "-screen-fullscreen", "0", "-screen-width", "1920",
                            "-screen-height", "1080", "-blockBenchmark", case,
                            "-blockBenchmarkDuration", duration, "-blockBenchmarkTimeout", "300",
                            "-blockBenchmarkOutput", report_path, "-blockBenchmarkQuit",
                            "-blockAcceptanceProfile", acceptance_profile]
                if capture_main_camera:
                    command += ["-blockBenchmarkCapture", directory / "main-camera.png"]
                if frame_timing_observation:
                    command += ["-blockBenchmarkFrameTimingObservation"]
            else:
                command += ["-batchmode", "-nographics", "-blockBenchmarkInput",
                            "combat" if case in ("steady250k", "combat250k") else "idle"]
            commands.append({"label": name, "arguments": [str(part) for part in command],
                             "windowMode": "normal-visible" if index == 0 else "hidden-headless"})
            actors[name] = (visible_graphics_process(command, exe.parent) if index == 0
                            else hidden_process(command, exe.parent))
            if index == 0:
                deadline = time.monotonic() + 75
                while time.monotonic() < deadline:
                    log = directory / "host.log"
                    if log.exists() and "[SMOKE] Connected" in log.read_text(encoding="utf-8-sig", errors="replace"):
                        break
                    if actors[name].poll() is not None:
                        raise RuntimeError(f"Host exited before connection: {actors[name].returncode}")
                    time.sleep(.25)
                else:
                    raise RuntimeError("Host connection timed out")
        deadline = started + 300 + duration + 30
        while time.monotonic() < deadline:
            report = read_json(report_path)
            write_json(directory / "progress.json", {"case": case, "acceptanceProfile": acceptance_profile,
                       "stage": report.get("stage", "starting"),
                       "elapsedSeconds": round(time.monotonic() - started, 1),
                       "sampledSeconds": report.get("seconds", 0), "blocks": report.get("finalBlocks", 0)})
            exited_clients = {name: actor.poll() for name, actor in actors.items()
                              if name != "host" and actor.poll() is not None}
            if exited_clients:
                raise RuntimeError(f"Independent clients exited early: {exited_clients}")
            if actors["host"].poll() is not None:
                break
            time.sleep(.5)
        else:
            raise RuntimeError("Host did not finish before the full workload deadline")
    except Exception as exception:
        error = str(exception)
    finally:
        host_exit = actors["host"].poll() if "host" in actors else None
        for actor in actors.values():
            if actor.poll() is None:
                actor.terminate()
        for actor in actors.values():
            try:
                actor.wait(timeout=10)
            except subprocess.TimeoutExpired:
                actor.kill()
                actor.wait(timeout=10)
    report = read_json(report_path)
    client_logs = {}
    for name in actors:
        if name == "host":
            continue
        headers = exception_headers(directory / f"{name}.log")
        native_headers = native_crash_headers(directory / f"{name}.log")
        client_logs[name] = {"available": headers is not None, "exceptions": len(headers) if headers is not None else None,
                             "firstExceptions": headers[:5] if headers is not None else [],
                             "nativeCrashHeaders": len(native_headers) if native_headers is not None else None,
                             "firstNativeCrashHeaders": native_headers[:5] if native_headers is not None else []}
    client_logs_clean = len(client_logs) == 7 and all(log["available"] and log["exceptions"] == 0
                                                   and log["nativeCrashHeaders"] == 0 for log in client_logs.values())
    gates = report.get("gates", [])
    approved_spike_gate = approved_frame_spike_gate(report, acceptance_profile)
    profile_matches = report.get("acceptanceProfile", "strict") == acceptance_profile
    if acceptance_profile == "prototype-v1":
        profile_matches = (profile_matches and report.get("requiredSeconds") == durations[case]
                           and report.get("ordinaryPublishP95LimitMs") == 200
                           and report.get("blastCompletionLimitMs") == 2000
                           and report.get("frameSpikeBlocking") is False
                           and report.get("supportQueueClearBlocking") is False)
    passed = (diagnostic_seconds is None and not frame_timing_observation and error is None and host_exit == 0 and client_logs_clean and report.get("complete") is True
              and report.get("passed") is True and report.get("scenario") == case
              and report.get("role") == "host" and len(gates) >= 10
              and profile_matches and all(gate_satisfied(gate, acceptance_profile) for gate in gates)
              and (approved_spike_gate["status"] == "pass" or acceptance_profile == "prototype-v1"
                   and approved_spike_gate["status"] == "fail")
              and report.get("seconds", 0) >= durations[case])
    risks = [gate for gate in gates if isinstance(gate, dict) and gate.get("diagnosticOnly") is True
             and gate.get("status") != "pass"]
    if approved_spike_gate["diagnosticOnly"] and approved_spike_gate["status"] != "pass":
        risks.append(approved_spike_gate)
    result = {"scenario": case, "passed": passed, "acceptanceProfile": acceptance_profile,
              "requestedGraphicsApi": graphics_api,
              "acceptanceTargets": profile_targets(acceptance_profile), "prototypeRisks": risks,
              "strictPerformancePassed": passed and acceptance_profile == "strict",
              "profileMatched": profile_matches, "diagnosticOnly": diagnostic_seconds is not None or frame_timing_observation,
              "frameTimingObservation": frame_timing_observation,
              "hostExitCode": host_exit, "harnessError": error, "commands": commands,
              "approvedFrameSpikeGate": approved_spike_gate,
              "clientLogExceptions": client_logs, "clientLogsClean": client_logs_clean,
              "durationSeconds": time.monotonic() - started, "report": report,
              "scope": "one visible normal 1080p host camera and seven independent hidden headless clients on the current computer"}
    write_json(directory / "case.json", result)
    print(f"{case}: passed={passed}; diagnostic={diagnostic_seconds is not None or frame_timing_observation}; {error or report.get('reason', '')}", flush=True)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--output", type=Path, default=ROOT / "Reports/BlockTerrain/Performance")
    parser.add_argument("--cases", nargs="+", choices=CASES, default=list(CASES))
    parser.add_argument("--acceptance-profile", choices=PROFILES, default="strict",
                        help="prototype-v1 uses the approved first playable release gates; strict retains the original gates")
    parser.add_argument("--graphics-api", choices=("d3d11", "d3d12"), default="d3d11",
                        help="Graphics API forced for the normal 1080p host camera")
    parser.add_argument("--diagnostic-seconds", type=float)
    parser.add_argument("--capture-main-camera", action="store_true", help="Capture the normal backbuffer during fixture warmup, before sampling")
    parser.add_argument("--frame-timing-observation", action="store_true", help="Record a separate bounded raw timing sidecar; this run remains diagnostic only")
    parser.add_argument("--stop-on-failure", action="store_true")
    args = parser.parse_args()
    if args.diagnostic_seconds is not None and (not math.isfinite(args.diagnostic_seconds) or args.diagnostic_seconds <= 0):
        parser.error("Diagnostic duration must be positive")
    exe = args.player.resolve(strict=True)
    output = args.output.resolve()
    if args.acceptance_profile != "strict":
        output /= args.acceptance_profile
    package = {"player": str(exe), "exeSha256": digest(exe),
               "runtimeSha256": digest(exe.parent / f"{exe.stem}_Data/Managed/Splatoon.dll"),
               "unityPlayerSha256": digest(exe.parent / "UnityPlayer.dll")}
    results = []
    for case in args.cases:
        results.append(run_case(exe, output / case / uuid.uuid4().hex[:8], case, args.diagnostic_seconds,
                                args.capture_main_camera, args.frame_timing_observation, args.acceptance_profile, args.graphics_api))
        write_json(output / "summary.json", {"package": package, "cases": results,
                   "requestedGraphicsApi": args.graphics_api,
                   "acceptanceProfile": args.acceptance_profile, "acceptanceTargets": profile_targets(args.acceptance_profile),
                   "strictPerformancePassed": args.acceptance_profile == "strict" and args.diagnostic_seconds is None
                       and not args.frame_timing_observation and len(results) == len(CASES)
                       and set(args.cases) == set(CASES) and all(result["passed"] for result in results),
                   "requiredWorkloadsComplete": set(args.cases) == set(CASES) and len(results) == len(CASES),
                   "performancePassed": args.diagnostic_seconds is None and not args.frame_timing_observation and len(results) == len(CASES)
                       and set(args.cases) == set(CASES) and all(result["passed"] for result in results),
                   "fullAcceptancePassed": False,
                   "remainingGates": ("core functional and six-case independent 2/8 Player impairment matrix; latest Windows package and report"
                                      if args.acceptance_profile == "prototype-v1" else
                                      "functional and full independent 2/8 Player impairment matrix; physical multi-machine and manual feel remain separate")})
        if args.stop_on_failure and not results[-1]["passed"]:
            break
    return 0 if all(result["passed"] for result in results) else 5


if __name__ == "__main__":
    raise SystemExit(main())
