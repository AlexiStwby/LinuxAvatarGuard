#!/usr/bin/env python3
"""Linux-only, dependency-free OSC unlocker. MIT license. Keys are never logged."""
import argparse
import errno
import json
import os
from pathlib import Path
import re
import socket
import stat
import struct
import sys
import time
import urllib.request


def osc_string(value):
    data = value.encode('utf-8') + b'\0'
    return data + b'\0' * (-len(data) % 4)


def packet(parameter, value):
    return osc_string('/avatar/parameters/' + parameter) + osc_string(',i') + struct.pack('>i', value)


def read_string(data, start):
    end = data.index(b'\0', start)
    return data[start:end].decode('utf-8'), (end + 4) & ~3


def avatar_change(data):
    """Only trust a correctly tagged OSC message, including messages inside bundles."""
    if data.startswith(b'#bundle\0'):
        offset = 16
        while offset + 4 <= len(data):
            size = struct.unpack_from('>I', data, offset)[0]
            offset += 4
            if size > len(data) - offset or size == 0:
                return None
            found = avatar_change(data[offset:offset + size])
            if found is not None:
                return found
            offset += size
        return None
    try:
        address, offset = read_string(data, 0)
        tags, offset = read_string(data, offset)
        if address == '/avatar/change' and tags == ',s':
            return read_string(data, offset)[0]
    except (ValueError, UnicodeError, struct.error):
        pass
    return None


def load_key(path):
    path = Path(path).resolve()
    if os.name != 'posix' or not sys.platform.startswith('linux'):
        raise ValueError('Esta herramienta es exclusiva de Linux.')
    info = path.stat()
    if info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) & 0o077:
        raise ValueError('La clave debe pertenecer a tu usuario y tener permisos 600. Ejecuta chmod 600 sobre el JSON.')
    config = json.loads(path.read_text())
    if config.get('format') != 1 or not re.fullmatch(r'avtr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}', config.get('avatarId', '')):
        raise ValueError('Asigna el avatarId correcto (avtr_UUID) al JSON antes de enviar claves.')
    params, keys = config.get('parameters'), config.get('keys')
    if not isinstance(params, list) or len(params) != 4 or len(set(params)) != 4 or not all(isinstance(p, str) and re.fullmatch(r'LAG_[0-9a-f]{8}_[0-3]', p) for p in params):
        raise ValueError('Parámetros de clave inválidos.')
    if not isinstance(keys, list) or len(keys) != 4 or not all(type(k) is int and 0 <= k <= 255 for k in keys):
        raise ValueError('Clave inválida.')
    return config


def query_json(port, path):
    """Local OSCQuery only; ignore proxy environment variables."""
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    with opener.open('http://127.0.0.1:%d%s' % (port, path), timeout=0.4) as response:
        return json.load(response)


def discover_query_port(osc_port):
    """Find loopback listeners belonging to this user's Wine/VRChat processes."""
    inodes = set()
    for process in Path('/proc').iterdir():
        if not process.name.isdigit():
            continue
        try:
            if process.stat().st_uid != os.getuid() or (process / 'comm').read_text().strip() not in ('wineserver', 'VRChat.exe'):
                continue
            for fd in (process / 'fd').iterdir():
                try:
                    target = os.readlink(fd)
                    if target.startswith('socket:['):
                        inodes.add(target[8:-1])
                except OSError:
                    pass
        except OSError:
            pass
    try:
        for row in Path('/proc/net/tcp').read_text().splitlines()[1:]:
            columns = row.split()
            if columns[3] != '0A' or columns[9] not in inodes:
                continue
            address, port = columns[1].split(':')
            if address != '0100007F':
                continue
            port = int(port, 16)
            try:
                info = query_json(port, '/?HOST_INFO')
                if info.get('NAME', '').startswith('VRChat-') and info.get('OSC_PORT') == osc_port:
                    return port
            except (OSError, ValueError):
                pass
    except OSError:
        pass
    return None


def write_status(path, config, state, message):
    if not path:
        return
    path = Path(path)
    temporary = path.with_name(path.name + '.%d.tmp' % os.getpid())
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(descriptor, 'w') as stream:
            json.dump({'pid': os.getpid(), 'buildId': config.get('buildId', ''), 'state': state, 'message': message}, stream)
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()


def main():
    parser = argparse.ArgumentParser(description='Desbloqueo de Linux Avatar Guard mediante OSC local.')
    parser.add_argument('key_file')
    parser.add_argument('--port', type=int, default=9000, help='Puerto de entrada OSC de VRChat')
    parser.add_argument('--listen-port', type=int, default=9001, help='Puerto de salida OSC de VRChat')
    parser.add_argument('--watch', action='store_true', help='Esperar /avatar/change; reenviar sólo mientras el ID coincida')
    parser.add_argument('--once', action='store_true', help='Enviar una vez al avatar activo; selecciona antes el ID indicado')
    parser.add_argument('--status-file', help='Estado para el asistente Unity; no contiene claves')
    parser.add_argument('--query-port', type=int, help='Puerto OSCQuery local; 0 desactiva la detección automática')
    args = parser.parse_args()
    if args.watch == args.once:
        parser.error('Elige --watch (recomendado) o --once.')
    if not 1 <= args.port <= 65535 or not 1 <= args.listen_port <= 65535:
        parser.error('Puerto fuera de rango.')
    if args.query_port is not None and not 0 <= args.query_port <= 65535:
        parser.error('Puerto OSCQuery fuera de rango.')
    config = None
    state = None
    def status(new_state, message):
        nonlocal state
        write_status(args.status_file, config or {}, new_state, message)
        if new_state != state:
            print(message, flush=True)
            state = new_state
    try:
        config = load_key(args.key_file)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            messages = [packet(p, k) for p, k in zip(config['parameters'], config['keys'])]
            def send():
                for message in messages:
                    sender.sendto(message, ('127.0.0.1', args.port))
            if args.once:
                send()
                print('Cuatro parámetros OSC enviados al cliente local. Esto no confirma recepción.')
                return
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
                listener.bind(('127.0.0.1', args.listen_port))
                listener.settimeout(0.5)
                active, deadline = False, 0.0
                query_port = args.query_port or None
                next_query, next_discovery = 0.0, 0.0
                status('waiting', 'Esperando el avatar configurado. Activa OSC en VRChat.')
                while True:
                    now = time.monotonic()
                    if args.query_port != 0 and not query_port and now >= next_discovery:
                        query_port = discover_query_port(args.port)
                        next_discovery = now + 15.0
                    if query_port and now >= next_query:
                        try:
                            current = query_json(query_port, '/avatar/change').get('VALUE', [])
                            observed = bool(current) and current[0] == config['avatarId']
                            if observed != active:
                                active, deadline = observed, time.monotonic() + 1.0
                            if not active:
                                status('waiting', 'Otro avatar activo. Esperando el avatar configurado.')
                            elif all(query_json(query_port, '/avatar/parameters/' + p).get('VALUE') == [k] for p, k in zip(config['parameters'], config['keys'])):
                                status('confirmed', 'VRChat confirma las cuatro claves. Desbloqueo activo.')
                        except (OSError, ValueError):
                            query_port = None
                        next_query = time.monotonic() + 2.0
                    try:
                        data, peer = listener.recvfrom(65535)
                        if peer[0] != '127.0.0.1':
                            continue
                        avatar_id = avatar_change(data)
                        if avatar_id is not None:
                            active = avatar_id == config['avatarId']
                            deadline = time.monotonic() + 1.0
                            status('detected' if active else 'waiting', 'Avatar configurado detectado.' if active else 'Otro avatar activo. Esperando el avatar configurado.')
                    except socket.timeout:
                        pass
                    # First send after 1 second, so the controller and expression parameters exist.
                    if active and time.monotonic() >= deadline:
                        send()
                        if state != 'confirmed':
                            status('sending', 'Enviando claves por OSC. Si sigue deformado, comprueba OSC y el ID vinculado.')
                        deadline = time.monotonic() + 2.0
    except (OSError, ValueError, KeyError, TypeError) as error:
        message = 'El puerto OSC está ocupado. Cierra otra copia del desbloqueador o coordina tu aplicación OSC.' if getattr(error, 'errno', None) == errno.EADDRINUSE else 'No se pudo iniciar OSC: ' + str(error)
        status('error', message)
        print('Error: ' + str(error), file=sys.stderr)
        sys.exit(1)
    except KeyboardInterrupt:
        status('stopped', 'Desbloqueador detenido.')
        print('\nFinalizado.')


if __name__ == '__main__':
    main()
