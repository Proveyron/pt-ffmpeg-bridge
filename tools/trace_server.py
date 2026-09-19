"""Record DIPC traffic between Pro Tools and its QuickTime server (or pt-ffmpeg-bridge).

Attaches Frida to every ProToolsQuickTimeServer.exe process as it appears -- never to ProTools.exe,
which is copy-protected -- and logs pipe reads/writes and shared-memory mappings as JSON lines.
This is how the protocol in docs/PROTOCOL.md was recovered.

usage: pip install frida
       python tools/trace_server.py capture.jsonl [seconds]
then use Pro Tools normally (e.g. File > Import > Audio) while it runs.
"""
import frida, sys, time, json, threading

JS = r"""
const k32 = Process.getModuleByName('kernel32.dll');
const GetFileInformationByHandleEx = new NativeFunction(k32.findExportByName('GetFileInformationByHandleEx'), 'int', ['pointer', 'int', 'pointer', 'uint32']);
const names = {};
function nameOf(h) {
  const key = h.toString();
  if (key in names) return names[key];
  const buf = Memory.alloc(1024);
  let n = null;
  if (GetFileInformationByHandleEx(h, 2 /*FileNameInfo*/, buf, 1024)) {
    const len = buf.readU32();
    n = buf.add(4).readUtf16String(len / 2);
  }
  names[key] = n;
  return n;
}
function isPipe(h) { const n = nameOf(h); return n && n.indexOf('DIPC') >= 0; }
function hx(p, n) { try { return Array.from(new Uint8Array(p.readByteArray(Math.min(n, 1 << 16)))).map(b => ('0' + b.toString(16)).slice(-2)).join(''); } catch (e) { return 'ERR'; } }
function hook(name, cb) { Interceptor.attach(k32.findExportByName(name), cb); }
const pendingRead = {};
hook('CreateFileW', { onEnter(a) { this.n = a[0].readUtf16String(); }, onLeave(r) { if (this.n && this.n.indexOf('DIPC') >= 0) { names[r.toString()] = this.n; send({ ev: 'open', name: this.n }); } } });
hook('CreateNamedPipeW', { onEnter(a) { this.n = a[0].readUtf16String(); }, onLeave(r) { names[r.toString()] = this.n; send({ ev: 'createpipe', name: this.n }); } });
hook('WriteFile', { onEnter(a) { if (isPipe(a[0])) send({ ev: 'W', pipe: nameOf(a[0]), len: a[2].toInt32(), data: hx(a[1], a[2].toInt32()) }); } });
hook('ReadFile', {
  onEnter(a) { this.h = a[0]; this.buf = a[1]; this.nr = a[3]; },
  onLeave(r) {
    if (!isPipe(this.h)) return;
    pendingRead[this.h.toString()] = this.buf;
    if (r.toInt32() && !this.nr.isNull()) { const n = this.nr.readU32(); send({ ev: 'R', pipe: nameOf(this.h), len: n, data: hx(this.buf, n) }); }
  }
});
hook('GetOverlappedResult', {
  onEnter(a) { this.h = a[0]; this.nr = a[2]; },
  onLeave(r) {
    if (!r.toInt32() || !isPipe(this.h)) return;
    const n = this.nr.readU32(); const buf = pendingRead[this.h.toString()];
    if (buf && n > 0) { send({ ev: 'R', pipe: nameOf(this.h), len: n, data: hx(buf, n) }); delete pendingRead[this.h.toString()]; }
  }
});
hook('CreateFileMappingW', { onEnter(a) { this.n = a[5].isNull() ? null : a[5].readUtf16String(); this.sz = a[4].toUInt32(); }, onLeave(r) { send({ ev: 'createmap', name: this.n, size: this.sz, h: r.toString() }); } });
hook('OpenFileMappingW', { onEnter(a) { this.n = a[2].readUtf16String(); }, onLeave(r) { send({ ev: 'openmap', name: this.n, h: r.toString() }); } });
hook('MapViewOfFile', { onEnter(a) { this.h = a[0].toString(); this.sz = a[4].toUInt32(); }, onLeave(r) { send({ ev: 'mapview', h: this.h, size: this.sz, addr: r.toString() }); } });
send({ ev: 'attached' });
"""

out = open(sys.argv[1] if len(sys.argv) > 1 else 'capture.jsonl', 'a', encoding='utf-8')
lock = threading.Lock()


def make_handler(pid):
    def on_msg(m, data):
        with lock:
            if m['type'] == 'send':
                p = m['payload']
                p['pid'] = pid
                p['t'] = round(time.time(), 4)
                out.write(json.dumps(p) + '\n')
                out.flush()
                print(json.dumps(p)[:200], flush=True)
            else:
                print(pid, m, flush=True)
    return on_msg


dev = frida.get_local_device()
seen = {}
print('watching for ProToolsQuickTimeServer.exe ...', flush=True)
deadline = time.time() + float(sys.argv[2] if len(sys.argv) > 2 else 3600)
while time.time() < deadline:
    for p in dev.enumerate_processes():
        if p.name.lower() == 'protoolsquicktimeserver.exe' and p.pid not in seen:
            try:
                s = frida.attach(p.pid)
                sc = s.create_script(JS)
                sc.on('message', make_handler(p.pid))
                sc.load()
                seen[p.pid] = (s, sc)
                print('attached to', p.pid, flush=True)
            except Exception as e:
                print('attach failed', p.pid, e, flush=True)
                seen[p.pid] = None
    time.sleep(0.2)
