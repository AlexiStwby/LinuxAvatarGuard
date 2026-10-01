import importlib.util
import json
import os
from pathlib import Path
import socket
import struct
import tempfile
import subprocess
import sys
import select
import unittest
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

location = Path(__file__).parents[1] / 'Package/Assets/LinuxAvatarGuard/Tools/lag_osc.py'
spec = importlib.util.spec_from_file_location('lag_osc', location)
osc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(osc)

class OscTests(unittest.TestCase):
    def test_query_starts_for_loaded_avatar_and_stops_on_change(self):
        avatar = 'avtr_12345678-1234-1234-1234-123456789abc'
        active = [avatar]
        keys = [100, 120, 150, 200]
        params = ['LAG_abcd1234_' + str(i) for i in range(4)]
        class Handler(BaseHTTPRequestHandler):
            def do_GET(self):
                value = active if self.path == '/avatar/change' else [keys[params.index(self.path.rsplit('/', 1)[-1])]]
                payload = json.dumps({'VALUE': value}).encode()
                self.send_response(200); self.end_headers(); self.wfile.write(payload)
            def log_message(self, *args): pass
        with tempfile.TemporaryDirectory() as d, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as events:
            receiver.bind(('127.0.0.1', 0)); events.bind(('127.0.0.1', 0))
            event_port = events.getsockname()[1]; events.close()
            server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
            thread = threading.Thread(target=server.serve_forever, daemon=True); thread.start()
            path, status = Path(d) / 'key.json', Path(d) / 'status.json'
            path.write_text(json.dumps({'format': 1, 'buildId': 'abcd1234' * 4, 'avatarId': avatar, 'parameters': params, 'keys': keys})); os.chmod(path, 0o600)
            process = subprocess.Popen([sys.executable, '-u', str(location), str(path), '--watch', '--port', str(receiver.getsockname()[1]), '--listen-port', str(event_port), '--query-port', str(server.server_port), '--status-file', str(status)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                receiver.settimeout(4)
                for p, k in zip(params, keys): self.assertEqual(receiver.recvfrom(1024)[0], osc.packet(p, k))
                state = json.loads(status.read_text())
                self.assertEqual(state['state'], 'confirmed')
                self.assertEqual(set(state), {'pid', 'buildId', 'state', 'message'})
                self.assertEqual(os.stat(status).st_mode & 0o777, 0o600)
                active[0] = 'avtr_ffffffff-ffff-ffff-ffff-ffffffffffff'
                import time
                deadline = time.monotonic() + 4
                while json.loads(status.read_text())['state'] != 'waiting' and time.monotonic() < deadline: time.sleep(.1)
                self.assertEqual(json.loads(status.read_text())['state'], 'waiting')
                receiver.setblocking(False)
                try:
                    while receiver.recvfrom(1024): pass
                except BlockingIOError: pass
                receiver.settimeout(2.5)
                with self.assertRaises(socket.timeout): receiver.recvfrom(1024)
            finally:
                process.terminate(); process.communicate(timeout=3); server.shutdown(); server.server_close(); thread.join(timeout=2)

    def test_integer_packet(self):
        p = osc.packet('LAG_abcd1234_0', 199)
        address, offset = osc.read_string(p, 0)
        tags, offset = osc.read_string(p, offset)
        self.assertEqual(address, '/avatar/parameters/LAG_abcd1234_0')
        self.assertEqual(tags, ',i')
        self.assertEqual(struct.unpack_from('>i', p, offset)[0], 199)
        self.assertEqual(len(p) % 4, 0)

    def test_avatar_message_and_bundle(self):
        avatar = 'avtr_12345678-1234-1234-1234-123456789abc'
        message = osc.osc_string('/avatar/change') + osc.osc_string(',s') + osc.osc_string(avatar)
        self.assertEqual(osc.avatar_change(message), avatar)
        bundle = b'#bundle\0' + b'\0' * 8 + struct.pack('>I', len(message)) + message
        self.assertEqual(osc.avatar_change(bundle), avatar)
        self.assertIsNone(osc.avatar_change(bundle[:-2]))
        self.assertIsNone(osc.avatar_change(osc.packet('LAG_abcd1234_0', 199)))
        self.assertIsNone(osc.avatar_change(b'garbage'))

    def test_private_config(self):
        with tempfile.TemporaryDirectory() as d:
            path = Path(d) / 'key.json'
            config = {'format': 1, 'avatarId': 'avtr_12345678-1234-1234-1234-123456789abc', 'parameters': ['LAG_abcd1234_' + str(i) for i in range(4)], 'keys': [34, 120, 241, 255]}
            path.write_text(json.dumps(config))
            os.chmod(path, 0o600)
            self.assertEqual(osc.load_key(path), config)
            os.chmod(path, 0o644)
            with self.assertRaises(ValueError): osc.load_key(path)
            os.chmod(path, 0o600)
            config['avatarId'] = ''
            path.write_text(json.dumps(config))
            with self.assertRaises(ValueError): osc.load_key(path)

    def test_watch_sends_only_matching_avatar(self):
        with tempfile.TemporaryDirectory() as d, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as events:
            receiver.bind(('127.0.0.1', 0))
            events.bind(('127.0.0.1', 0)); event_port = events.getsockname()[1]; events.close()
            path = Path(d) / 'key.json'
            avatar = 'avtr_12345678-1234-1234-1234-123456789abc'
            keys = [100, 120, 150, 200]
            params = ['LAG_abcd1234_' + str(i) for i in range(4)]
            path.write_text(json.dumps({'format': 1, 'avatarId': avatar, 'parameters': params, 'keys': keys}))
            os.chmod(path, 0o600)
            process = subprocess.Popen([sys.executable, '-u', str(location), str(path), '--watch', '--port', str(receiver.getsockname()[1]), '--listen-port', str(event_port)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            try:
                readable, _, _ = select.select([process.stdout], [], [], 3)
                self.assertTrue(readable, 'OSC watcher did not initialize')
                process.stdout.readline()
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                    def change(value):
                        sender.sendto(osc.osc_string('/avatar/change') + osc.osc_string(',s') + osc.osc_string(value), ('127.0.0.1', event_port))
                    change('avtr_ffffffff-ffff-ffff-ffff-ffffffffffff')
                    receiver.settimeout(1.2)
                    with self.assertRaises(socket.timeout): receiver.recvfrom(1024)
                    change(avatar)
                    receiver.settimeout(2)
                    for parameter, key in zip(params, keys):
                        self.assertEqual(receiver.recvfrom(1024)[0], osc.packet(parameter, key))
                    change('avtr_ffffffff-ffff-ffff-ffff-ffffffffffff')
                    receiver.settimeout(2.5)
                    with self.assertRaises(socket.timeout): receiver.recvfrom(1024)
            finally:
                process.terminate()
                process.communicate(timeout=3)

    def test_loopback_transport(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            receiver.bind(('127.0.0.1', 0)); receiver.settimeout(1)
            message = osc.packet('LAG_abcd1234_1', 240)
            sender.sendto(message, receiver.getsockname())
            self.assertEqual(receiver.recvfrom(1024)[0], message)

if __name__ == '__main__': unittest.main()
