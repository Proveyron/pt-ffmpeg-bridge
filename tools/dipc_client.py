"""Minimal DIPC client that plays Pro Tools' role against a QuickTime server.

It launches a server exe (Avid's original or our replacement), performs the named-pipe handshake,
runs the same call sequence Pro Tools uses to import audio, and writes the extracted PCM to a file.
Used to verify the protocol and to compare our server's output with QuickTime's.

usage: python tools/dipc_client.py <server.exe> <media-file> <out.raw> [--bps 2] [--le] [--rate 44100]
"""
import argparse
import ctypes
import os
import struct
import subprocess
import sys
import threading
import time
from ctypes import wintypes

import win32file
import win32pipe

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from extract_proto import DEFAULT_EXE, find_descriptor  # noqa: E402
from google.protobuf import descriptor_pool, message_factory  # noqa: E402

FLAG_WANTS_REPLY = 0x01000000
PIPE_PREFIX = '//./pipe/'


def djb2(name):
    h = 5381
    for c in name.encode('ascii'):
        h = (h * 33 + c) & 0xFFFFFFFF
    return h


REPLY_TAG = djb2('GenericFutureCompletion')


def load_messages():
    # The schema always comes from Avid's original exe (our replacement doesn't embed it).
    fd = find_descriptor(open(DEFAULT_EXE, 'rb').read())
    pool = descriptor_pool.DescriptorPool()
    pool.Add(fd)
    return {m.name: message_factory.GetMessageClass(pool.FindMessageTypeByName(f'{fd.package}.{m.name}'))
            for m in fd.message_type}


class Shm:
    """Read-only view of a server-created Local\\DIPC_Shm_%08X mapping."""
    k32 = ctypes.WinDLL('kernel32', use_last_error=True)
    k32.OpenFileMappingW.restype = wintypes.HANDLE
    k32.OpenFileMappingW.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.LPCWSTR]
    k32.MapViewOfFile.restype = ctypes.c_void_p
    k32.MapViewOfFile.argtypes = [wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t]

    def __init__(self, shm_id, size):
        self.h = self.k32.OpenFileMappingW(0x0004, False, 'Local\\DIPC_Shm_%08X' % shm_id)
        if not self.h:
            raise OSError(ctypes.get_last_error(), 'OpenFileMappingW')
        self.base = self.k32.MapViewOfFile(self.h, 0x0004, 0, 0, size)
        if not self.base:
            raise OSError(ctypes.get_last_error(), 'MapViewOfFile')

    def read(self, offset, n):
        return ctypes.string_at(self.base + offset, n)


class DipcClient:
    def __init__(self, server_exe, messages):
        self.msgs = messages
        self.pid = os.getpid()
        self.inbound = win32pipe.CreateNamedPipe(
            f'{PIPE_PREFIX}DIPC_active_{self.pid}', win32pipe.PIPE_ACCESS_INBOUND,
            win32pipe.PIPE_TYPE_MESSAGE | win32pipe.PIPE_READMODE_MESSAGE | 8, 255, 0, 0x10000, 30000, None)
        server_exe = os.path.abspath(server_exe)
        self.proc = subprocess.Popen([server_exe, str(self.pid)], cwd=os.path.dirname(server_exe))
        win32pipe.ConnectNamedPipe(self.inbound, None)
        self.outbound = None
        for _ in range(100):
            try:
                self.outbound = win32file.CreateFile(f'{PIPE_PREFIX}DIPC_passive_{self.proc.pid}',
                                                     win32file.GENERIC_WRITE, 0, None, win32file.OPEN_EXISTING, 0, None)
                break
            except Exception:
                time.sleep(0.05)
        if self.outbound is None:
            raise RuntimeError('server pipe never appeared')
        win32pipe.SetNamedPipeHandleState(self.outbound, win32pipe.PIPE_READMODE_MESSAGE, None, None)
        self.next_id = 1
        self.replies = {}
        self.cv = threading.Condition()
        threading.Thread(target=self._reader, daemon=True).start()
        self.shm = {}

    def _reader(self):
        while True:
            try:
                _, data = win32file.ReadFile(self.inbound, 0x10000)
            except Exception:
                return
            tag, _stamp, rid, _flags = struct.unpack_from('<IIII', data)
            payload = data[24:24 + struct.unpack_from('<I', data, 20)[0]] if len(data) >= 24 else b''
            if rid == 0:
                msg = self.msgs['ConsoleMessage']()
                msg.ParseFromString(payload)
                print('[server console]', msg.msg.decode('latin1', 'replace').strip())
                continue
            with self.cv:
                self.replies[rid] = (tag, payload)
                self.cv.notify_all()

    def _send(self, method, payload, wants_reply=True):
        rid = self.next_id if wants_reply else 0
        if wants_reply:
            self.next_id += 1
        stamp = time.perf_counter_ns() // 1000 & 0xFFFFFFFF
        flags = FLAG_WANTS_REPLY if wants_reply else 0
        if payload is None:  # parameterless requests carry a 4-byte tail instead of part/len
            frame = struct.pack('<IIIII', djb2(method), stamp, rid, flags, 0x7644CC87)
        else:
            frame = struct.pack('<IIIIII', djb2(method), stamp, rid, flags, 1, len(payload)) + payload
        win32file.WriteFile(self.outbound, frame)
        return rid

    def call(self, method, reply_type, timeout=30, **fields):
        cs = self.msgs.get('CS_' + method) or self.msgs['CS_RemoteInterfaceID']
        payload = None if fields.get('_noargs') else cs(**{k: v for k, v in fields.items() if not k.startswith('_')}).SerializeToString()
        rid = self._send(method, payload)
        with self.cv:
            if not self.cv.wait_for(lambda: rid in self.replies, timeout):
                raise TimeoutError(method)
            tag, data = self.replies.pop(rid)
        assert tag == REPLY_TAG, hex(tag)
        msg = self.msgs[reply_type]()
        msg.ParseFromString(data)
        return msg

    def free(self, desc):
        self._send('SH_RemoteFreeMem', self.msgs['MemoryDescriptor'](id=desc.id, address=desc.address, size=desc.size).SerializeToString(), wants_reply=False)

    def read_shm(self, desc, n):
        if desc.id not in self.shm:
            self.shm[desc.id] = Shm(desc.id, desc.size)
        return self.shm[desc.id].read(desc.address, n)

    def close(self):
        self.proc.kill()


def utf16_container(msgs, s):
    raw = s.encode('utf-16le') + b'\x00\x00'
    return msgs['StringContainer'](type=2, num_codeunits=len(s) + 1, string_size=len(raw), msg=raw)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('server')
    ap.add_argument('media')
    ap.add_argument('out')
    ap.add_argument('--bps', type=int, default=2, help='bytes per sample per channel (Pro Tools sends 2)')
    ap.add_argument('--le', action='store_true')
    ap.add_argument('--rate', type=float, default=0)
    ap.add_argument('--chunk', type=int, default=16128)
    args = ap.parse_args()

    msgs = load_messages()
    c = DipcClient(args.server, msgs)
    try:
        iface = c.call('CreateQuickTimeWrapperInterface', 'SC_CreateQuickTimeWrapperInterface', _noargs=True).remote_interface_id
        rid = dict(remote_interface_id=iface)
        r = c.call('NewMovieFromFilePath', 'SC_NewMovieFromFilePath', the_Movie_Path_Uni_Char_Ptr=utf16_container(msgs, os.path.abspath(args.media)), **rid)
        print('NewMovieFromFilePath acfResult', r.acfResult)
        if r.acfResult < 0:
            return 1
        # Same order Pro Tools 12.5 uses (captured): the audio reader is created before track queries.
        print('tracks', c.call('QTWGetMovieTrackCount', 'SC_QTWGetMovieTrackCount', **rid).movie_track_count)
        mer = c.call('DetermineMovieEditRateAndLength', 'SC_DetermineMovieEditRateAndLength',
                     forcedFrameRate=0.0, editorFrameRate=0.0, singleFrameDuration=0, **rid)
        print('edit rate', mer.MERNumerator, '/', mer.MERDenominator, 'length', mer.movieLength)
        track = 1
        print('track id', c.call('QTWGetTrackID', 'SC_QTWGetTrackID', trackNum=track, **rid).trackID)
        c.call('GetQTAudioReader', 'SC_Error', use_cache=False, **rid)
        print('audio?', c.call('IsTrackAnAudioTrack', 'SC_IsTrackAnAudioTrack', track=track, **rid).track_is_audio)
        info = c.call('GetAudioInfoForTrack', 'SC_GetAudioInfoForTrack', track=track, **rid)
        print('info', str(info).replace('\n', ' '))
        vbr = c.call('IsAudioTrackVBR', 'SC_IsAudioTrackVBR', trackNum=track, **rid).isVBR
        print('vbr', vbr)
        if not vbr:  # Pro Tools takes this branch for non-VBR tracks
            print('media samples', c.call('QTWGetMediaSampleCount', 'SC_QTWGetMediaSampleCount', track=track, **rid).medDurInSamples)
        dur = c.call('QTWGetMediaDuration', 'SC_QTWGetMediaDuration', track=track, **rid).duration
        scale = c.call('QTWGetMediaTimeScale', 'SC_QTWGetMediaTimeScale', track=track, **rid).scale
        print('duration', dur, 'timescale', scale)
        rate = args.rate or info.sampleRate
        p = c.call('SetParamsForProToolsImport', 'SC_SetParamsForProToolsImport', track=track,
                   bytes_per_sample=args.bps, is_little_endian=args.le, sample_rate=rate, **rid)
        print('params -> channels', p.num_channels, 'acf', p.acfResult)
        frame_bytes = args.bps * p.num_channels
        total = int(dur * rate / scale) + args.chunk
        start = 0
        with open(args.out, 'wb') as out:
            while start < total:
                e = c.call('ExtractAudio', 'SC_ExtractAudio', track=track, start_sample=start, samples_to_extract=args.chunk,
                           buffer_size=args.chunk * frame_bytes, stereo=True, channel=0, **rid)
                if e.acfresult < 0 or e.samples_extracted == 0:
                    print('extract stop:', str(e).replace(chr(10), ' '))
                    break
                out.write(c.read_shm(e.sh_mem, e.samples_extracted * frame_bytes))
                c.free(e.sh_mem)
                start += e.samples_extracted
        print('extracted samples', start, '->', args.out)
        c.call('DestroyQuickTimeInterface', 'SC_Error', **rid)
    finally:
        c.close()
    return 0


if __name__ == '__main__':
    sys.exit(main())
