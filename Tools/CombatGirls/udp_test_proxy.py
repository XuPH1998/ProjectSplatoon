"""Local-only UDP relay for reproducible Editor transport acceptance (never used in gameplay)."""
import argparse, asyncio, json, random
from pathlib import Path

async def run(args):
    loop=asyncio.get_running_loop();rng=random.Random(91309);transports=[]
    stats={'forwarded':0,'dropped':0,'delayMs':args.delay_ms,'jitterMs':args.jitter_ms,'loss':args.loss,'ports':args.ports}
    class Relay(asyncio.DatagramProtocol):
        def __init__(self):self.client=None;self.down=None;self.up=None
        def send(self,transport,data,address):
            if rng.random()<args.loss:stats['dropped']+=1;return
            stats['forwarded']+=1
            loop.call_later(max(0,(args.delay_ms+rng.uniform(-args.jitter_ms,args.jitter_ms))/1000),transport.sendto,data,address)
        def datagram_received(self,data,address):
            self.client=address;self.send(self.up,data,('127.0.0.1',args.target))
    class Response(asyncio.DatagramProtocol):
        def __init__(self,relay):self.relay=relay
        def datagram_received(self,data,address):
            if self.relay.client:self.relay.send(self.relay.down,data,self.relay.client)
    for port in args.ports:
        relay=Relay()
        relay.down,_=await loop.create_datagram_endpoint(lambda:relay,local_addr=('127.0.0.1',port))
        relay.up,_=await loop.create_datagram_endpoint(lambda:Response(relay),local_addr=('127.0.0.1',0))
        transports.extend((relay.down,relay.up))
    print('Local UDP test relay ready',flush=True)
    try:await asyncio.sleep(args.duration)
    finally:
        for transport in transports:transport.close()
        Path(args.output).write_text(json.dumps(stats,indent=2),'utf-8')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--ports',nargs='+',type=int,required=True);p.add_argument('--target',type=int,required=True)
    p.add_argument('--delay-ms',type=float,default=50);p.add_argument('--jitter-ms',type=float,default=10)
    p.add_argument('--loss',type=float,default=.01);p.add_argument('--duration',type=float,default=240)
    p.add_argument('--output',required=True)
    asyncio.run(run(p.parse_args()))
