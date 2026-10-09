import socket
from dataclasses import dataclass, field
from pathlib import Path

from .layout import counter_location


class TraceGap(Exception):
    pass


@dataclass
class Snapshot:
    counter: int
    words: dict[str, int] = field(default_factory=dict)


def parse_datagram(data: bytes) -> list[tuple[str, int]]:
    lines = data.decode("ascii").replace("\x00", "").strip("\n").split("\n")
    if len(lines) % 2:
        raise ValueError(f"odd number of lines in datagram: {data!r}")
    return [(lines[i], int(lines[i + 1], 16)) for i in range(0, len(lines), 2)]


class FrameAssembler:
    def __init__(self, locations: list[str], allow_gaps: bool = False):
        self.counter_loc = counter_location()
        if locations[-1] != self.counter_loc:
            raise ValueError("frame counter must be the last location")
        self.known = set(locations)
        self.values: dict[str, int] = {}
        self.last: int | None = None
        self.allow_gaps = allow_gaps
        self.gaps: list[int] = []

    def feed(self, pairs: list[tuple[str, int]]) -> list[Snapshot]:
        out = []
        for loc, value in pairs:
            if loc not in self.known:
                raise ValueError(f"unknown location {loc!r}")
            if loc != self.counter_loc:
                self.values[loc] = value
                continue
            if self.last is not None and value != self.last + 1:
                missing = list(range(self.last + 1, value))
                if not self.allow_gaps or value <= self.last:
                    raise TraceGap(f"frame counter jumped {self.last} -> {value}; missing {missing}")
                self.gaps += missing
            self.last = value
            out.append(Snapshot(value, dict(self.values)))
        return out


class Receiver:
    def __init__(self, sock_path: Path):
        self.path = sock_path

    def __enter__(self) -> "Receiver":
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.unlink(missing_ok=True)
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_DGRAM)
        self.sock.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 8 << 20)
        self.sock.bind(str(self.path))
        return self

    def recv(self, timeout: float) -> bytes | None:
        self.sock.settimeout(timeout)
        try:
            return self.sock.recv(1 << 16)
        except socket.timeout:
            return None

    def __exit__(self, *exc) -> None:
        self.sock.close()
        self.path.unlink(missing_ok=True)
