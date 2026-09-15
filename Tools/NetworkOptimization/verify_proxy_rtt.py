"""Measure actual UDP echo round trips through the same impairment proxy (no game RPCs)."""
import argparse
import contextlib
import json
from pathlib import Path
import socket
import statistics
import threading
import time
from types import SimpleNamespace
from udp_proxy import run


def measure(output, rtt):
    output.mkdir(parents=True, exist_ok=True)
    echo = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    echo.bind(("127.0.0.1", 0)); echo.settimeout(.1)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as reservation:
        reservation.bind(("127.0.0.1", 0)); front = reservation.getsockname()[1]
    stopped = threading.Event()
    def serve():
        while not stopped.is_set():
            try:
                data, peer = echo.recvfrom(65535)
                echo.sendto(data, peer)
            except socket.timeout:
                continue
    server = threading.Thread(target=serve)
    args = SimpleNamespace(listen=front, target=echo.getsockname()[1], rtt=rtt, jitter=10 if rtt else 0,
                           loss=0, seed=20260915, seconds=8, output=str(output / f"proxy-{rtt}.json"))
    failures = []
    def proxy_run():
        try:
            run(args)
        except Exception as error:
            failures.append(repr(error))
    samples = []
    server.start()
    try:
        with (output / f"proxy-{rtt}.log").open("w", encoding="utf-8") as log, contextlib.redirect_stdout(log):
            proxy = threading.Thread(target=proxy_run)
            proxy.start()
            try:
                time.sleep(.1)
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as client:
                    client.settimeout(1)
                    for index in range(20):
                        payload = str(index).encode().ljust(64, b" ")
                        started = time.perf_counter()
                        client.sendto(payload, ("127.0.0.1", front))
                        reply, _ = client.recvfrom(65535)
                        elapsed = (time.perf_counter() - started) * 1000
                        if reply != payload:
                            raise RuntimeError("Echo identity mismatch")
                        samples.append(elapsed)
                        time.sleep(.025)
            finally:
                proxy.join()
    finally:
        stopped.set(); server.join(); echo.close()
    if failures:
        raise RuntimeError(failures)
    return {"injectedRttMs": rtt, "jitterPerDirectionMs": args.jitter, "samples": len(samples),
            "meanMs": statistics.mean(samples), "minMs": min(samples), "p95Ms": sorted(samples)[18],
            "maxMs": max(samples), "rawMs": samples,
            "proxyClock": time.get_clock_info("monotonic").implementation,
            "measurementClock": time.get_clock_info("perf_counter").implementation}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    output = parser.parse_args().output.resolve()
    result = [measure(output, rtt) for rtt in (0, 50, 100, 200)]
    (output / "rtt-calibration.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps([{key: value for key, value in row.items() if key != "rawMs"} for row in result]))
