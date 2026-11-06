#!/usr/bin/env python3
"""
Asks the running file system service to watch file systems, as the app does: the service
can run and answer while every watch fails (a security module denying fanotify), and that
is the case to catch. Speaks the app's protocol (search/Models/LinuxService/ServiceProtocol.cs).

Usage: check-service.py / [/home ...]
"""
import socket
import struct
import sys

SOCKET = "/run/file-search-manager/service.sock"
VERSION, HELLO, WATCH, WELCOME, WATCH_RESULT, MODE_ALL = 2, 1, 2, 101, 102, 1


def string(text):  # .NET BinaryWriter: 7-bit encoded length, then UTF-8
    data, out, n = text.encode(), bytearray(), len(text.encode())
    while n >= 0x80:
        out.append(n & 0x7F | 0x80)
        n >>= 7
    out.append(n)
    return bytes(out) + data


def read_string(data, i):
    n = shift = 0
    while True:
        b = data[i]
        i += 1
        n |= (b & 0x7F) << shift
        shift += 7
        if b < 0x80:
            return data[i:i + n].decode(), i + n


def receive(sock):
    def exactly(n):
        data = b""
        while len(data) < n:
            chunk = sock.recv(n - len(data))
            if not chunk:
                sys.exit("the service closed the connection (refused this client?)")
            data += chunk
        return data
    return exactly(struct.unpack("<i", exactly(4))[0])


def send(sock, payload):
    sock.sendall(struct.pack("<i", len(payload)) + payload)


sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
sock.settimeout(10)
sock.connect(SOCKET)
send(sock, bytes([HELLO]) + struct.pack("<H", VERSION) + bytes([MODE_ALL]))
welcome = receive(sock)
assert welcome[0] == WELCOME and struct.unpack("<H", welcome[1:3])[0] == VERSION, "unexpected answer to hello"
failed = 0
for root in sys.argv[1:]:
    send(sock, bytes([WATCH]) + string(root))
    answer = receive(sock)
    assert answer[0] == WATCH_RESULT, "unexpected answer to watch"
    answered, i = read_string(answer, 1)
    error, _ = read_string(answer, i)
    print(f"watch {answered}: {error or 'OK'}")
    failed += bool(error)
sys.exit(1 if failed else 0)
