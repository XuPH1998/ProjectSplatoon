"""Functional process-restart and playing-match late-join loadout acceptance."""
import argparse
import json
import sys
import time
import uuid
from pathlib import Path
from specialweapon_network_acceptance import hidden_process, read_report


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--player', type=Path, default=Path('Temp/SpecialWeapons/Player/InkLan.exe'))
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--rtt', type=int, default=200)
    args = parser.parse_args()
    root = Path.cwd()
    directory = (args.output / ('run-' + uuid.uuid4().hex)).resolve()
    directory.mkdir(parents=True)
    player = args.player.resolve()
    started = time.monotonic()
    processes = []
    proxy = None
    result = {'passed': False, 'rtt_ms': args.rtt, 'directory': str(directory)}

    def launch(phase, port=20670):
        process = hidden_process([player, '-batchmode', '-force-d3d11', '-screen-width', 960,
                                  '-screen-height', 540, '-loadoutPhase', phase, '-loadoutRoot', directory,
                                  '-loadoutPort', port, '-logFile', directory / (phase + '.log')], root)
        processes.append(process)
        return process

    def wait_until(predicate, label, seconds=85):
        deadline = time.monotonic() + seconds
        while not predicate():
            (args.output / 'progress.json').write_text(json.dumps({'stage': label, 'elapsed_seconds': round(time.monotonic() - started),
                'processes': [{'pid': p.pid, 'exit_code': p.poll()} for p in processes]}), encoding='utf-8')
            if time.monotonic() > deadline:
                raise TimeoutError(label)
            time.sleep(.5)

    try:
        seed = launch('seed')
        wait_until(lambda: seed.poll() is not None, 'save preferences in first process')
        if seed.returncode != 0 or read_report(directory / 'seed.txt').get('passed') != 'True':
            raise RuntimeError('Seed process failed')
        host = launch('host')
        wait_until(lambda: (directory / 'host-playing').exists() or host.poll() is not None, 'start playing Host')
        if host.poll() is not None:
            raise RuntimeError('Host exited before entering match')
        proxy = hidden_process([sys.executable, root / 'Tools/subweapon_network_proxy.py', '--host', 20670,
                                '--listen', 20671, '--rtt', args.rtt, '--jitter', args.rtt / 10,
                                '--loss', 5 if args.rtt else 0, '--seconds', 110, '--output', directory / 'proxy.json',
                                '--stop-file', directory / 'stop-proxy'], root)
        client = launch('client', 20671)
        wait_until(lambda: host.poll() is not None and client.poll() is not None, 'new client restores saved loadout in playing match')
        result.update(host=read_report(directory / 'host.txt'), client=read_report(directory / 'client.txt'),
                      exit_codes=[seed.returncode, host.returncode, client.returncode],
                      distinct_processes=len({seed.pid, host.pid, client.pid}) == 3)
        result['passed'] = result['exit_codes'] == [0, 0, 0] and result['distinct_processes'] and all(
            result[role].get('passed') == 'True' for role in ('host', 'client'))
    except Exception as error:
        result['error'] = str(error)
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
        if proxy:
            (directory / 'stop-proxy').write_text('complete', encoding='utf-8')
            try:
                proxy.wait(timeout=10)
            except Exception:
                proxy.terminate()
                proxy.wait(timeout=10)
        if (directory / 'preferences-original.json').exists():
            restore = launch('restore')
            try:
                wait_until(lambda: restore.poll() is not None, 'restore original preferences')
            except Exception as error:
                result['restore_error'] = str(error)
            finally:
                if restore.poll() is None:
                    restore.terminate()
                    restore.wait(timeout=10)
            result['preferences_restored'] = restore.returncode == 0 and read_report(directory / 'restore.txt').get('passed') == 'True'
            result['passed'] &= result['preferences_restored']
        (args.output / 'results.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result), flush=True)
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
