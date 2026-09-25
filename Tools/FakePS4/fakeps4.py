"""A fake PS4 on 127.0.0.1 for testing DPI's Move end to end:
  FTP 2121 (pyftpdlib over ROOT), RPI HTTP 12800 (uninstall_game, get_task_progress),
  BinLoader 9090: takes the payload, reads the PC callback from the B4 marker, then
  behaves like payload_experimental: cmd 3 free space, cmd 4 install-from-local-file.
Usage: python fakeps4.py ROOT PAYLOAD_BIN LOGFILE
"""
import sys, os, socket, struct, threading, json, shutil, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT, PAYLOAD, LOG = sys.argv[1], sys.argv[2], sys.argv[3]
lock = threading.Lock()
tasks = {}          # id -> size (finished at once)
next_task = [100]

def log(*a):
    with lock:
        with open(LOG, "a", encoding="utf-8") as f:
            f.write(" ".join(str(x) for x in a) + "\n")

def local(p):       # console path -> ROOT path
    return os.path.join(ROOT, p.lstrip("/").replace("/", os.sep))

# ---------------- FTP
def ftp():
    from pyftpdlib.authorizers import DummyAuthorizer
    from pyftpdlib.handlers import FTPHandler
    from pyftpdlib.servers import FTPServer
    a = DummyAuthorizer(); a.add_anonymous(ROOT, perm="elradfmwMT")
    h = FTPHandler; h.authorizer = a; h.passive_ports = range(50300, 50400)
    FTPServer(("127.0.0.1", 2121), h).serve_forever()

# ---------------- RPI
class Rpi(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def reply(self, body):
        data = body.encode()
        self.send_response(200); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
    def do_GET(self):
        self.reply('{ "status": "fail", "error": "Unsupported method" }')
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)) or 0).decode()
        req = json.loads(body or "{}")
        if self.path.endswith("/uninstall_game"):
            tid = req["title_id"]
            for d in ("user/app", "user/patch", "mnt/ext0/user/app", "mnt/ext0/user/patch"):
                shutil.rmtree(os.path.join(ROOT, d.replace("/", os.sep), tid), ignore_errors=True)
            log("RPI uninstall_game", tid)
            self.reply('{ "status": "success" }')
        elif self.path.endswith("/get_task_progress"):
            size = tasks.get(req["task_id"])
            if size is None:
                self.reply('{ "status": "fail", "error": "no task" }'); return
            self.reply('{ "status": "success", "bits": 0x0, "error": 0, "length": 0x%x, "transferred": 0x%x, "length_total": 0x%x, "transferred_total": 0x%x, "num_index": 1, "num_total": 1, "rest_sec": 0, "rest_sec_total": 0, "preparing_percent": 100, "local_copy_percent": 100 }' % (size, size, size, size))
        else:
            self.reply('{ "status": "fail" }')

# ---------------- payload
def u32(s): return struct.unpack("<I", readn(s, 4))[0]
def readn(s, n):
    b = b""
    while len(b) < n:
        c = s.recv(n - len(b))
        if not c: raise EOFError
        b += c
    return b
def blob(s): return readn(s, u32(s))

def payload_loop(pc):
    while True:
        try:
            s = socket.create_connection(pc, timeout=30)
        except OSError:
            log("payload: PC gone, exiting"); return
        try:
            cmd = u32(s)
            if cmd == 0:
                log("payload: exit"); s.close(); return
            if cmd == 3:
                def space(p):
                    st = shutil.disk_usage(ROOT); return st.free, st.total
                fi, ti = space(""); fe, te = space("")
                s.sendall(struct.pack("<4Q", fi, ti, fe, te)); log("payload: free space"); s.close(); continue
            if cmd in (1, 2, 4):
                url = blob(s).decode(); name = blob(s).decode(); cid = blob(s).decode(); typ = blob(s).decode()
                size = struct.unpack("<Q", readn(s, 8))[0]; icon = blob(s)
                storage = struct.unpack("<i", readn(s, 4))[0] if cmd in (2, 4) else -1
                log("payload: cmd", cmd, "url", url, "type", typ, "size", size, "storage", storage)
                rv, task = 0, -1
                if url.startswith("/"):
                    src = local(url)
                    if os.path.exists(os.path.join(ROOT, ".fail")):
                        rv = 0x80990039 - 2**32          # "not enough space"
                        log("payload: refusing (test)")
                    elif not os.path.exists(src):
                        rv = 0x80990002 - 2**32
                    else:
                        tid = url.rstrip("/").split("/")[-2]          # /user/dpi_move/<TID>/<file>
                        drive = "mnt/ext0/" if storage == 1 else ""
                        kind = {"PS4GD": "app", "PS4GP": "patch", "PS4AC": "addcont"}.get(typ, "app")
                        if kind == "addcont":
                            label = os.path.basename(url)[4:-4]        # dlc_<LABEL>.pkg
                            dst = local(f"{drive}user/addcont/{tid}/{label}/ac.pkg")
                        else:
                            dst = local(f"{drive}user/{kind}/{tid}/{kind}.pkg")
                        os.makedirs(os.path.dirname(dst), exist_ok=True)
                        shutil.copyfile(src, dst)
                        task = next_task[0]; next_task[0] += 1; tasks[task] = size
                        log("payload: installed", url, "->", dst, "task", task)
                if cmd == 4:
                    s.sendall(struct.pack("<ii", rv, task))
                s.close(); continue
            log("payload: unknown cmd", cmd); s.close()
        except Exception as e:
            log("payload: error", repr(e)); s.close()

def binloader():
    original = open(PAYLOAD, "rb").read()
    marker = original.find(b"\xb4" * 6)
    srv = socket.socket(); srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    srv.bind(("127.0.0.1", 9090)); srv.listen(4)
    while True:
        c, _ = srv.accept(); data = b""
        c.settimeout(5)
        try:
            while True:
                b = c.recv(65536)
                if not b: break
                data += b
        except socket.timeout: pass
        c.close()
        ip = socket.inet_ntoa(data[marker:marker + 4]); port = struct.unpack(">H", data[marker + 4:marker + 6])[0]
        log("binloader: payload", len(data), "bytes, calls back", ip, port)
        threading.Thread(target=payload_loop, args=((ip, port),), daemon=True).start()

threading.Thread(target=ftp, daemon=True).start()
threading.Thread(target=binloader, daemon=True).start()
ThreadingHTTPServer(("127.0.0.1", 12800), Rpi).serve_forever()
