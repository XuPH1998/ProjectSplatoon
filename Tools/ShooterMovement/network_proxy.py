"""Local UDP impairment proxy for real NGO editor sessions; no game-state hooks.

Each client receives a separate upstream UDP socket so the host retains distinct peers.
Delay applies equally in each direction. Loss is an independent per-datagram percentage.
"""
import argparse
import asyncio
import random
import time
from pathlib import Path


async def run(args):
    loop = asyncio.get_running_loop()
    rng = random.Random(613)
    peers = {}
    pending = set()
    counters = dict(forwarded=0, dropped=0)

    def forward(transport, data, address=None):
        if rng.random() < args.loss / 100:
            counters['dropped'] += 1
            return
        counters['forwarded'] += 1
        loop.call_later(args.rtt / 2000, transport.sendto, data, address)

    class Upstream(asyncio.DatagramProtocol):
        def __init__(self, client):
            self.client = client

        def datagram_received(self, data, addr):
            forward(downstream, data, self.client)

    async def new_peer(client, data):
        try:
            transport, _ = await loop.create_datagram_endpoint(
                lambda: Upstream(client), remote_addr=('127.0.0.1', args.target))
            peers[client] = transport
            forward(transport, data)
        finally:
            pending.discard(client)

    class Downstream(asyncio.DatagramProtocol):
        def datagram_received(self, data, addr):
            if addr in peers:
                forward(peers[addr], data)
            elif addr not in pending:
                pending.add(addr)
                asyncio.create_task(new_peer(addr, data))

    downstream, _ = await loop.create_datagram_endpoint(
        Downstream, local_addr=('127.0.0.1', args.listen))
    print(f'UDP proxy ready listen={args.listen} target={args.target} addedRTT={args.rtt}ms loss={args.loss}%', flush=True)
    try:
        deadline = time.monotonic() + args.duration
        while time.monotonic() < deadline and not (args.stop_file and Path(args.stop_file).exists()):
            await asyncio.sleep(1)
    finally:
        downstream.close()
        for transport in peers.values():
            transport.close()
        print(dict(peers=len(peers), **counters), flush=True)


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--listen', type=int, default=18014)
    p.add_argument('--target', type=int, default=18013)
    p.add_argument('--rtt', type=int, default=50)
    p.add_argument('--loss', type=float, default=1)
    p.add_argument('--duration', type=int, default=180)
    p.add_argument('--stop-file')
    asyncio.run(run(p.parse_args()))
