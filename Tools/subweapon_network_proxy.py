"""Local UDP impairment fixture for the opt-in Unity subweapon acceptance players."""
import argparse
import heapq
import json
import random
import select
import socket
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--listen", type=int, required=True)
    parser.add_argument("--host", type=int, required=True)
    parser.add_argument("--rtt", type=float, default=0)
    parser.add_argument("--jitter", type=float, default=0)
    parser.add_argument("--loss", type=float, default=0)
    parser.add_argument("--seconds", type=float, default=185)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--stop-file", type=Path)
    args = parser.parse_args()
    rng = random.Random(211309)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(("127.0.0.1", args.listen))
    if hasattr(socket, "SIO_UDP_CONNRESET"):
        # Players can close their UDP port before the peer's final disconnect packet.
        sock.ioctl(socket.SIO_UDP_CONNRESET, False)
    sock.setblocking(False)
    server, client = ("127.0.0.1", args.host), None
    started = time.monotonic()
    pending = []
    stats = {"configured_rtt_ms": args.rtt, "jitter_per_direction_ms": args.jitter,
             "configured_loss_percent": args.loss, "received": 0, "forwarded": 0,
             "dropped": 0, "bytes_forwarded": 0, "delay_sum_ms": 0, "delay_max_ms": 0}
    args.output.parent.mkdir(parents=True, exist_ok=True)
    try:
        while time.monotonic() - started < args.seconds:
            if args.stop_file and args.stop_file.exists():
                break
            now = time.monotonic()
            while pending and pending[0][0] <= now:
                due, sequence, queued, data, destination = heapq.heappop(pending)
                sock.sendto(data, destination)
                delay = (now - queued) * 1000
                stats["forwarded"] += 1
                stats["bytes_forwarded"] += len(data)
                stats["delay_sum_ms"] += delay
                stats["delay_max_ms"] = max(stats["delay_max_ms"], delay)
            timeout = min(.02, max(0, pending[0][0] - now)) if pending else .02
            if not select.select([sock], [], [], timeout)[0]:
                continue
            data, source = sock.recvfrom(65535)
            if source == server:
                destination = client
            else:
                client, destination = source, server
            if destination is None:
                continue
            stats["received"] += 1
            if rng.random() * 100 < args.loss:
                stats["dropped"] += 1
                continue
            delay = max(0, args.rtt / 2 + rng.uniform(-args.jitter, args.jitter)) / 1000
            queued = time.monotonic()
            heapq.heappush(pending, (queued + delay, stats["received"], queued, data, destination))
    finally:
        sock.close()
        stats["elapsed_seconds"] = time.monotonic() - started
        stats["mean_applied_one_way_delay_ms"] = stats["delay_sum_ms"] / max(1, stats["forwarded"])
        stats["actual_drop_percent"] = stats["dropped"] * 100 / max(1, stats["received"])
        args.output.write_text(json.dumps(stats, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
