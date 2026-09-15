"""Local UDP impairment proxy. Delays/drops datagrams before UTP reliability runs.

Each front-end client gets its own upstream socket, preserving transport identities.
Only loopback listeners are supported; this is a development verification tool.
"""
import argparse
import heapq
import json
import random
import selectors
import socket
import time
from pathlib import Path


def run(args):
    random_source = random.Random(args.seed)
    selector = selectors.DefaultSelector()
    front = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    front.bind(("127.0.0.1", args.listen))
    front.setblocking(False)
    selector.register(front, selectors.EVENT_READ, None)
    clients, scheduled = {}, []
    sequence = 0
    peer_resets = 0
    counters = {"up": {"received": 0, "bytes": 0, "dropped": 0, "forwarded": 0},
                "down": {"received": 0, "bytes": 0, "dropped": 0, "forwarded": 0}}
    started = time.monotonic()
    print(json.dumps({"ready": True, "listen": args.listen, "target": args.target,
                      "rtt_ms": args.rtt, "jitter_ms_per_direction": args.jitter, "loss_percent": args.loss}), flush=True)
    try:
        while not args.seconds or time.monotonic() - started < args.seconds:
            now = time.monotonic()
            while scheduled and scheduled[0][0] <= now:
                _, _, sock, destination, payload, direction = heapq.heappop(scheduled)
                sock.sendto(payload, destination)
                counters[direction]["forwarded"] += 1
            wait = min(.02, max(0, scheduled[0][0] - now)) if scheduled else .02
            for key, _ in selector.select(wait):
                try:
                    payload, source = key.fileobj.recvfrom(65535)
                except ConnectionResetError as error:
                    if getattr(error, "winerror", None) != 10054:
                        raise
                    # Windows reports ICMP port-unreachable here after a player exits.
                    peer_resets += 1
                    continue
                if key.fileobj is front:
                    if source not in clients:
                        upstream = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
                        upstream.bind(("127.0.0.1", 0)); upstream.setblocking(False)
                        clients[source] = upstream
                        selector.register(upstream, selectors.EVENT_READ, source)
                    sock, destination, direction = clients[source], ("127.0.0.1", args.target), "up"
                else:
                    sock, destination, direction = front, key.data, "down"
                counters[direction]["received"] += 1
                counters[direction]["bytes"] += len(payload)
                if random_source.random() * 100 < args.loss:
                    counters[direction]["dropped"] += 1
                    continue
                delay = max(0, args.rtt / 2 + random_source.uniform(-args.jitter, args.jitter)) / 1000
                sequence += 1
                heapq.heappush(scheduled, (time.monotonic() + delay, sequence, sock, destination, payload, direction))
    except KeyboardInterrupt:
        pass
    finally:
        for sock in clients.values():
            sock.close()
        front.close(); selector.close()
        result = {"seconds": time.monotonic() - started, "clients": len(clients), "pending": len(scheduled),
                  "rtt_ms": args.rtt, "jitter_ms": args.jitter, "loss_percent": args.loss, "seed": args.seed,
                  "peer_resets": peer_resets, **counters}
        if args.output:
            path = Path(args.output); path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result), flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--listen", type=int, required=True)
    parser.add_argument("--target", type=int, required=True)
    parser.add_argument("--rtt", type=float, default=0)
    parser.add_argument("--jitter", type=float, default=0)
    parser.add_argument("--loss", type=float, default=0)
    parser.add_argument("--seed", type=int, default=20260915)
    parser.add_argument("--seconds", type=float, default=0)
    parser.add_argument("--output")
    options = parser.parse_args()
    if not (0 <= options.loss <= 100 and options.rtt >= 0 and options.jitter >= 0):
        parser.error("Delay/jitter must be nonnegative; loss must be between 0 and 100.")
    run(options)
