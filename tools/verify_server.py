"""End-to-end check of a server exe: drive it exactly like Pro Tools does (tools/dipc_client.py) and
compare the PCM it hands over shared memory with a direct ffmpeg decode of the same file.

usage: python tools/verify_server.py <server.exe> [media files...]   (default: testmedia/*)
"""
import glob
import os
import re
import subprocess
import sys
import tempfile

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
CLIENT = os.path.join(HERE, 'dipc_client.py')

# (bytes per sample, little endian, output rate or 0 = source rate)
CASES = [(2, False, 0), (3, False, 0), (2, False, 48000)]


def run_client(server, media, out, bps, le, rate):
    cmd = [sys.executable, CLIENT, server, media, out, '--bps', str(bps)] + (['--le'] if le else []) + (['--rate', str(rate)] if rate else [])
    p = subprocess.run(cmd, capture_output=True, text=True, timeout=300)
    text = p.stdout + p.stderr
    if 'acfResult -' in text and 'NewMovieFromFilePath' in text:
        return None, text
    ch = re.search(r'params -> channels (\d+) acf (-?\d+)', text)
    if p.returncode != 0 or not ch or ch.group(2) != '0':
        raise RuntimeError(text)
    return int(ch.group(1)), text


def reference(media, bps, le, rate, channels, dither):
    fmt = {2: 's16', 3: 's24'}[bps] + ('le' if le else 'be')
    cmd = ['ffmpeg', '-v', 'error', '-i', media, '-map', '0:a:0', '-ac', str(channels)]
    opts = [f'osr={rate}', 'resampler=soxr', 'precision=28'] if rate else []
    if dither:
        opts += ['osf=s16', 'dither_method=triangular']
    if opts:
        cmd += ['-af', 'aresample=' + ':'.join(opts)]
    if rate:
        cmd += ['-ar', str(rate)]
    cmd += ['-f', fmt, '-']
    return subprocess.run(cmd, capture_output=True, check=True).stdout


# Pro Tools film order, as input-channel indices (mirrors src/Bridge.Decode/ProToolsChannelOrder.cs).
PT_ORDER = {('3.0', 3): [0, 2, 1], ('5.0', 5): [0, 2, 1, 3, 4], ('5.0(side)', 5): [0, 2, 1, 3, 4],
            ('5.1', 6): [0, 2, 1, 4, 5, 3], ('5.1(side)', 6): [0, 2, 1, 4, 5, 3], ('7.1', 8): [0, 2, 1, 6, 7, 4, 5, 3]}


def pt_order(media, channels):
    layout = subprocess.run(['ffprobe', '-v', 'error', '-select_streams', 'a:0', '-show_entries', 'stream=channel_layout',
                             '-of', 'csv=p=0', media], capture_output=True, text=True).stdout.strip()
    layout = layout or {6: '5.1', 8: '7.1'}.get(channels, '')
    return PT_ORDER.get((layout, channels))


def to_int(raw, bps, le, channels):
    if bps == 2:
        a = np.frombuffer(raw, ('<' if le else '>') + 'i2').astype(np.int64)
    else:
        b = np.frombuffer(raw, np.uint8).reshape(-1, 3).astype(np.int64)
        if not le:
            b = b[:, ::-1]
        a = b[:, 0] | b[:, 1] << 8 | b[:, 2] << 16
        a = np.where(a & 0x800000, a - (1 << 24), a)
    return a.reshape(-1, channels)


def main():
    server = sys.argv[1]
    files = sys.argv[2:] or sorted(glob.glob(os.path.join(HERE, '..', 'testmedia', '*')))
    failures = 0
    with tempfile.TemporaryDirectory() as tmp:
        for media in files:
            name = os.path.basename(media)
            for bps, le, rate in CASES:
                label = f'{name:28s} {bps * 8}-bit {"LE" if le else "BE"} {rate or "native"}'
                out = os.path.join(tmp, 'out.raw')
                try:
                    channels, log = run_client(server, os.path.abspath(media), out, bps, le, rate)
                except Exception as e:
                    print(f'FAIL {label}: client error\n{e}')
                    failures += 1
                    continue
                if channels is None:
                    print(f'SKIP {label}: server reports unreadable')
                    continue
                got = to_int(open(out, 'rb').read(), bps, le, channels)
                # The server dithers when reducing precision; compare against an undithered decode with tolerance.
                ref = to_int(reference(media, bps, le, rate, channels, dither=False), bps, le, channels)
                order = pt_order(media, channels)
                if order:
                    ref = ref[:, order]
                n = len(ref)
                if len(got) < n:
                    print(f'FAIL {label}: got {len(got)} frames, reference has {n}')
                    failures += 1
                    continue
                diff = np.abs(got[:n] - ref)
                tail = np.abs(got[n:]).max() if len(got) > n else 0
                tol = 0 if bps == 3 and not rate else 2
                ok = diff.max() <= tol * (256 if bps == 3 and rate else 1) and tail == 0
                print(f'{"ok  " if ok else "FAIL"} {label}: {channels}ch {n} frames (got {len(got)}), max diff {diff.max()} LSB'
                      + (f', PT order {order}' if order else ''))
                failures += not ok
    print('FAILURES:', failures)
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
