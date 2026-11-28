# SPDX-License-Identifier: GPL-2.0-only
"""NekoDrop UI bridge for the separately staged Quick Share CLI backend."""
import argparse
import ipaddress
import json
import re
import shutil
import socket
import sys
import tempfile
import threading
import time
import uuid
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import ifaddr
from quickshare import discover, framing, mdns, receive, send, server

PORT = 53318
MAX_FILES, MAX_TOTAL, MAX_CONTROL = 64, 128 << 30, 1 << 20
WIRE = receive.off_pb2.PayloadTransferFrame
PRINT_LOCK = threading.Lock()


def emit(event, **data):
    with PRINT_LOCK:
        print(json.dumps(dict(event=event, protocol="quickshare", **data)), flush=True)


def safe_name(name):
    name = receive.sanitize_offered_name(name)
    name = re.sub(r'[<>:"/\\|?*\x00-\x1f\x7f]', "_", name).strip(" .")
    name = name[:140].rstrip(" .") or "file"
    stem = name.split(".", 1)[0].rstrip(" ")
    if re.fullmatch(r"(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])", stem, re.IGNORECASE):
        name = "_" + name
    return name


class BoundedReceive(receive.ReceiveSession):
    introduced = False
    rejected = False

    def _handle_introduction(self, offer):
        if self.introduced:
            raise receive.ReceiveError("a second introduction is not allowed")
        self.introduced = True
        files = list(offer.file_metadata)
        if (not files or offer.text_metadata or len(files) > MAX_FILES or
                any(f.size < 0 for f in files) or sum(f.size for f in files) > MAX_TOTAL or
                len({f.payload_id for f in files}) != len(files) or
                sum(f.size for f in files) > shutil.disk_usage(self.output_dir).free):
            self.rejected = True
            self._send_frames(receive.build_connection_response_frame(False))
            return
        used = set()
        for meta in files:
            base = name = safe_name(meta.name)
            n = 0
            while name.casefold() in used or (name + ".part").casefold() in used:
                n += 1
                name = f"{n}-{base}"
            used.update((name.casefold(), (name + ".part").casefold()))
            meta.name = name
        super()._handle_introduction(offer)
        self.rejected = not self._offer_accepted

    def _handle_payload_transfer(self, pt):
        h, c = pt.payload_header, pt.payload_chunk
        if pt.packet_type != WIRE.PacketType.CONTROL:
            if h.type == WIRE.PayloadHeader.PayloadType.BYTES:
                size = sum(map(len, self._bytes_reassembly.values()))
                if size + len(c.body) > MAX_CONTROL or (h.id not in self._bytes_reassembly and len(self._bytes_reassembly) >= 8):
                    raise receive.ReceiveError("control message budget exceeded")
            elif h.type == WIRE.PayloadHeader.PayloadType.FILE:
                incoming = self.files_by_payload_id.get(h.id)
                if (incoming is None or h.HasField("total_size") and h.total_size != incoming.size or c.offset != incoming.bytes_written or
                        len(c.body) > incoming.size - incoming.bytes_written):
                    raise receive.ReceiveError("unexpected file range")
                if c.flags & WIRE.PayloadChunk.Flags.LAST_CHUNK and c.offset + len(c.body) != incoming.size:
                    raise receive.ReceiveError("partial file")
        return super()._handle_payload_transfer(pt)


def receive_on(sock, target, offer, progress):
    with sock, tempfile.TemporaryDirectory(dir=target, prefix=".quickshare-") as tmp:
        channel, pin = server._run_handshake(sock)
        session = BoundedReceive(channel=channel, pin=pin, output_dir=Path(tmp),
                                 on_offer=offer, on_progress=progress,
                                 channel_send=lambda data: framing.send_frame(sock, data))
        try:
            session.start()
            while not session.rejected:
                complete = session.handle_encrypted_frame(framing.recv_frame(sock))
                if complete == receive.SENDER_CANCELED:
                    return None
                if complete is not None:
                    saved = []
                    for file in complete:
                        dest = receive.unique_destination(target, safe_name(file.name))
                        file.rename(dest)  # same volume; Windows won't overwrite a file that appeared meanwhile
                        saved.append(dest)
                    return saved
            return None
        finally:
            for incoming in session.files_by_payload_id.values():
                incoming.close()


def lan_for(address):
    ip = ipaddress.IPv4Address(address)
    if ip.is_loopback or ip.is_unspecified or ip.is_multicast or ip.is_link_local:
        raise ValueError("unusable local address")
    for adapter in ifaddr.get_adapters():
        for item in adapter.ips:
            if item.is_IPv4 and item.ip == address and 0 < item.network_prefix <= 32:
                return ipaddress.IPv4Network((ip, item.network_prefix), strict=False)
    raise ValueError("local interface not found")


class Worker:
    def __init__(self, name, address, folder):
        self.name, self.address, self.folder = name, address, folder
        self.lan = lan_for(address)
        self.stop = threading.Event()
        self.looking = threading.Event()
        self.lock = threading.Lock()
        self.peers = {}
        self.rx = None
        self.tx = None

    def permitted(self, address):
        try:
            ip = ipaddress.IPv4Address(address)
            return ip in self.lan and ip not in (self.lan.broadcast_address, self.lan.network_address) and address != self.address
        except ValueError:
            return False

    def discover(self):
        while not self.stop.is_set():
            if not self.looking.wait(0.5):
                continue
            try:
                found = discover.browse(3.0, address=self.address)
            except (OSError, ValueError):
                self.stop.wait(2)
                continue
            now = time.monotonic()
            with self.lock:
                for dev in found:
                    if not self.permitted(dev.address) or not 0 < dev.port <= 65535:
                        continue
                    key = "qs:" + dev.endpoint_id.hex()
                    self.peers[key] = (dev, now)
                    if self.looking.is_set():
                        name = "".join(ch for ch in dev.device_name[:90] if ch.isprintable())
                        emit("peer", id=key, name=name or "Quick Share", model="Quick Share")
                for key in [k for k,v in self.peers.items() if now - v[1] > 15]:
                    del self.peers[key]
                    emit("peer_gone", id=key)
            self.stop.wait(1)

    def sender(self, job, peer, files):
        last_progress = 0.0
        def progress(value):
            nonlocal last_progress
            now = time.monotonic()
            if now - last_progress >= 0.18 or value.bytes_sent == value.total_bytes:
                last_progress = now
                emit("send", id=job["id"], state="sending", done=value.bytes_sent, total=value.total_bytes)

        def opened(sock):
            with self.lock:
                job["socket"] = sock
                if job["cancelled"]:
                    sock.close()
        try:
            status = send.send_files(peer.address, peer.port, mdns.stable_endpoint_id(),
                mdns.build_endpoint_info(self.name, mdns.ShareTargetType.LAPTOP), files,
                on_pin=lambda p: emit("pin", id=job["id"], pin=p),
                on_progress=progress,
                on_socket=opened, source_address=self.address)
            state = ("sent" if status == send.wf_pb2.ConnectionResponseFrame.Status.ACCEPT else
                     "cancelled" if status in (send.RECEIVER_CANCELED, send.USER_CANCELED) else "declined")
        except Exception as err:
            state = "failed"
            print("Quick Share send: " + type(err).__name__, file=sys.stderr)
        finally:
            with self.lock:
                state = "cancelled" if job["cancelled"] else state
                self.tx = None
            emit("send", id=job["id"], state=state, reason="Quick Share" if state == "failed" else "")

    def incoming(self, sock, address):
        job = dict(id="qr:" + uuid.uuid4().hex, socket=sock, decision=threading.Event(), accept=False, cancelled=False)
        with self.lock:
            self.rx = job
            sender = next((d.device_name for d,_ in self.peers.values() if d.address==address), "Quick Share")
        sender = "".join(ch for ch in sender[:90] if ch.isprintable()) or "Quick Share"
        sizes, offered = {}, False
        def offer(files, pin, _):
            nonlocal offered
            offered = True
            sizes.update((f.payload_id, f.size) for f in files)
            emit("asked", id=job["id"], sender=sender, model="Quick Share", waits=True,
                 pin=pin, files=[f.name for f in files])
            job["decision"].wait(45)
            return job["accept"] and not job["cancelled"]
        progress_by_id = {}
        last_progress = 0.0
        def progress(f):
            nonlocal last_progress
            progress_by_id[f.payload_id] = f.bytes_written
            now = time.monotonic()
            if now-last_progress >= 0.18:
                last_progress = now
                emit("incoming", id=job["id"], done=sum(progress_by_id.values()), total=sum(sizes.values()))
        try:
            sock.settimeout(20)
            files = receive_on(sock, self.folder, offer, progress)
            if files is not None:
                emit("stored", id=job["id"], folder=str(self.folder), names=[f.name for f in files])
            elif offered:
                emit("lost", id=job["id"], reason="cancelled" if job["cancelled"] or not job["accept"] else "Quick Share")
        except Exception as err:
            if offered:
                emit("lost", id=job["id"], reason="cancelled" if job["cancelled"] else "Quick Share")
                print("Quick Share receive: " + type(err).__name__, file=sys.stderr)
        finally:
            sock.close()
            with self.lock:
                self.rx = None

    @staticmethod
    def cancel(job):
        job["cancelled"] = True
        if "decision" in job:
            job["decision"].set()
        sock = job.get("socket")
        if sock is not None:
            try:
                sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            sock.close()

    def commands(self):
        try:
            for line in sys.stdin:
                try:
                    cmd = json.loads(line)
                    action = cmd.get("do")
                    key = cmd.get("id")
                    if action == "look":
                        (self.looking.set if cmd.get("on") else self.looking.clear)()
                    elif action == "answer":
                        with self.lock:
                            if self.rx and self.rx["id"] == key and not self.rx["decision"].is_set():
                                self.rx["accept"] = cmd.get("accept") is True
                                self.rx["decision"].set()
                    elif action == "cancel":
                        with self.lock:
                            for job in (self.rx,self.tx):
                                if job and job["id"] == key:
                                    self.cancel(job)
                    elif action == "send" and isinstance(key,str):
                        files = [Path(x) for x in cmd.get("files", [])]
                        with self.lock:
                            match = self.peers.get(cmd.get("peer"))
                            if (self.tx is not None or match is None or time.monotonic()-match[1] > 15 or
                                    not 0 < len(files) <= MAX_FILES or not all(f.is_file() for f in files)):
                                emit("send", id=key, state="failed", reason="Quick Share")
                                continue
                            self.tx = dict(id=key, cancelled=False)
                            threading.Thread(target=self.sender, args=(self.tx,match[0],files), daemon=True).start()
                    elif action == "quit":
                        break
                except (TypeError, ValueError, OSError):
                    continue
        finally:
            self.stop.set()
            self.looking.set()
            with self.lock:
                for job in (self.rx,self.tx):
                    if job is not None:
                        self.cancel(job)

    def run(self):
        self.folder.mkdir(parents=True, exist_ok=True)
        with socket.socket(socket.AF_INET,socket.SOCK_STREAM) as listener:
            if hasattr(socket,"SO_EXCLUSIVEADDRUSE"):
                listener.setsockopt(socket.SOL_SOCKET,socket.SO_EXCLUSIVEADDRUSE,1)
            listener.bind((self.address,PORT))
            listener.listen(5)
            listener.settimeout(0.5)
            adv = server._Advertiser(self.name,PORT,self.address)
            adv.start()
            try:
                threading.Thread(target=self.commands,daemon=True).start()
                threading.Thread(target=self.discover,daemon=True).start()
                while not self.stop.is_set():
                    try:
                        sock, (address, _) = listener.accept()
                    except TimeoutError:
                        continue
                    if self.permitted(address):
                        self.incoming(sock,address)
                    else:
                        sock.close()
            finally:
                adv.stop()


def self_test():
    assert safe_name("picture.png") == "picture.png"
    assert safe_name("CON") == "_CON"
    assert safe_name("file. ") == "file"
    assert safe_name("a:b") == "a_b"
    assert safe_name("ConOut$.txt") == "_ConOut$.txt"
    with tempfile.TemporaryDirectory() as d:
        base = Path(d)
        f = base / "sample.txt"
        f.write_bytes(b"nekodrop quick share fixture" * 8192)
        extra = base / "extra"
        extra.mkdir()
        duplicate = extra / f.name
        duplicate.write_bytes(b"second file with the same name")
        target = base / "received"
        target.mkdir()
        existing = target / f.name
        existing.write_bytes(b"existing file")

        def transfer(accept):
            out, errors, received_pins, sent_pins = [], [], [], []
            def offer(files, pin, _):
                received_pins.append(pin)
                assert len(files) == 2
                return accept

            with socket.socket() as listener:
                listener.bind(("127.0.0.1", 0))
                listener.listen(1)
                listener.settimeout(10)
                def listen():
                    try:
                        conn, _ = listener.accept()
                        conn.settimeout(10)
                        out.extend(receive_on(conn, target, offer, lambda f: None) or [])
                    except Exception as err:
                        errors.append(err)
                thread = threading.Thread(target=listen, daemon=True)
                thread.start()
                status = send.send_files("127.0.0.1", listener.getsockname()[1], mdns.stable_endpoint_id(),
                    mdns.build_endpoint_info("NekoDrop test", mdns.ShareTargetType.LAPTOP), [f, duplicate],
                    on_pin=sent_pins.append)
                thread.join(10)
            assert not errors and not thread.is_alive(), errors
            assert received_pins == sent_pins and len(sent_pins) == 1 and sent_pins[0].isdigit()
            return status, out

        status, out = transfer(False)
        assert status == send.wf_pb2.ConnectionResponseFrame.Status.REJECT and not out
        assert list(target.iterdir()) == [existing]
        status, out = transfer(True)
        assert status == send.wf_pb2.ConnectionResponseFrame.Status.ACCEPT and len(out) == 2
        assert {p.read_bytes() for p in out} == {f.read_bytes(), duplicate.read_bytes()}
        assert existing.read_bytes() == b"existing file" and len(list(target.iterdir())) == 3
    print("Quick Share encrypted loopback passed: matching PINs, accept/reject, byte integrity and no overwrite.")


if __name__ == "__main__":
    p=argparse.ArgumentParser()
    p.add_argument("--name",default="nekodrop")
    p.add_argument("--address")
    p.add_argument("--folder")
    p.add_argument("--selftest",action="store_true")
    a=p.parse_args()
    if a.selftest:
        self_test()
    else:
        Worker(a.name,a.address,Path(a.folder).resolve()).run()
