import struct
from dataclasses import dataclass, field
from pathlib import Path

from .events import Event, GAME_INFO_SIZE, PLAYER_STRIDE

HEADER = b"{U\x03raw[$U#l"

# (name, offset including command byte, struct code) — SPEC.md v3.18.0
PRE_FIELDS = [
    ("seed", 0x7, "I"), ("action", 0xB, "H"), ("x", 0xD, "f"), ("y", 0x11, "f"),
    ("facing", 0x15, "f"), ("joy_x", 0x19, "f"), ("joy_y", 0x1D, "f"), ("c_x", 0x21, "f"),
    ("c_y", 0x25, "f"), ("trigger", 0x29, "f"), ("buttons", 0x2D, "I"),
    ("phys_buttons", 0x31, "H"), ("phys_l", 0x33, "f"), ("phys_r", 0x37, "f"),
    ("raw_x", 0x3B, "b"), ("percent", 0x3C, "f"), ("raw_y", 0x40, "b"),
    ("raw_cx", 0x41, "b"), ("raw_cy", 0x42, "b"),
]
POST_FIELDS = [
    ("char", 0x7, "B"), ("action", 0x8, "H"), ("x", 0xA, "f"), ("y", 0xE, "f"),
    ("facing", 0x12, "f"), ("percent", 0x16, "f"), ("shield", 0x1A, "f"),
    ("last_attack", 0x1E, "B"), ("combo", 0x1F, "B"), ("last_hit_by", 0x20, "B"),
    ("stocks", 0x21, "B"), ("anim_frame", 0x22, "f"), ("flags", 0x26, "5s"),
    ("misc_as", 0x2B, "f"), ("airborne", 0x2F, "B"), ("ground_id", 0x30, "H"),
    ("jumps", 0x32, "B"), ("lcancel", 0x33, "B"), ("hurtbox_state", 0x34, "B"),
    ("self_air_x", 0x35, "f"), ("self_y", 0x39, "f"), ("kb_x", 0x3D, "f"),
    ("kb_y", 0x41, "f"), ("self_ground_x", 0x45, "f"), ("hitlag", 0x49, "f"),
    ("anim_index", 0x4D, "I"),
]


@dataclass
class PlayerSlot:
    port: int
    ext_char: int
    ptype: int
    costume: int


@dataclass
class GameStart:
    version: tuple[int, int, int]
    game_info: bytes
    seed: int
    stage: int
    players: list[PlayerSlot]
    ucf: list[tuple[int, int]]
    major_scene: int = 0   # 8 = Slippi online; 0 when not recorded (before Slippi 3.12)


@dataclass
class Pre:
    frame: int
    port: int
    follower: bool
    values: dict


@dataclass
class Post:
    frame: int
    port: int
    follower: bool
    values: dict


for _cls, _fields in ((Pre, PRE_FIELDS), (Post, POST_FIELDS)):
    for _name, *_ in _fields:
        setattr(_cls, _name, property(lambda self, n=_name: self.values.get(n)))


@dataclass
class Frame:
    number: int
    seed: int | None = None
    pre: dict = field(default_factory=dict)
    post: dict = field(default_factory=dict)


@dataclass
class Replay:
    start: GameStart
    frames: dict[int, Frame]
    end_method: int | None


def _fields(payload: bytes, spec) -> dict:
    out = {}
    for name, off, code in spec:
        size = struct.calcsize(">" + code)
        if off + size <= len(payload):
            out[name] = struct.unpack_from(">" + code, payload, off)[0]
    return out


def _raw_events(data: bytes) -> bytes:
    if not data.startswith(HEADER):
        raise ValueError("not a .slp file (missing UBJSON raw header)")
    (n,) = struct.unpack_from(">i", data, len(HEADER))
    if n == 0:
        raise ValueError("replay still being written (raw length is 0)")
    return data[len(HEADER) + 4: len(HEADER) + 4 + n]


def _payload_sizes(raw: bytes) -> tuple[dict[int, int], int]:
    if raw[0] != Event.PAYLOADS:
        raise ValueError("first event is not Event Payloads")
    n = raw[1]
    sizes = {raw[2 + i]: struct.unpack_from(">H", raw, 3 + i)[0] for i in range(0, n - 1, 3)}
    return sizes, 1 + n


def _game_start(payload: bytes) -> GameStart:
    version = tuple(payload[1:4])
    gi = payload[5:5 + GAME_INFO_SIZE]
    players = []
    for i in range(4):
        b = 0x60 + PLAYER_STRIDE * i
        if gi[b + 1] != 3:
            players.append(PlayerSlot(i, gi[b], gi[b + 1], gi[b + 3]))
    ucf = []
    for i in range(4):
        off = 0x141 + 8 * i
        if off + 8 <= len(payload):
            ucf.append((struct.unpack_from(">I", payload, off)[0], struct.unpack_from(">I", payload, off + 4)[0]))
        else:
            ucf.append((0, 0))
    return GameStart(
        version=version, game_info=bytes(gi), seed=struct.unpack_from(">I", payload, 0x13D)[0],
        stage=struct.unpack_from(">H", gi, 0xE)[0], players=players, ucf=ucf,
        major_scene=payload[0x1A4] if len(payload) > 0x1A4 else 0,
    )


def read_slp(data: bytes) -> Replay:
    raw = _raw_events(data)
    sizes, pos = _payload_sizes(raw)
    start = None
    frames: dict[int, Frame] = {}
    end_method = None
    while pos < len(raw):
        code = raw[pos]
        if code not in sizes:
            raise ValueError(f"unknown event 0x{code:02x} at {pos}")
        if pos + 1 + sizes[code] > len(raw):
            raise ValueError(f"truncated event 0x{code:02x} at {pos}")
        payload = raw[pos: pos + 1 + sizes[code]]
        pos += 1 + sizes[code]
        if code == Event.GAME_START:
            start = _game_start(payload)
        elif code == Event.FRAME_START:
            f, seed = struct.unpack_from(">iI", payload, 1)
            frames[f] = Frame(f, seed)  # a repeated frame number is a rollback: last copy wins
        elif code in (Event.PRE_FRAME, Event.POST_FRAME):
            f, port, follower = struct.unpack_from(">iBB", payload, 1)
            frame = frames.setdefault(f, Frame(f))
            key = (port, bool(follower))
            if code == Event.PRE_FRAME:
                frame.pre[key] = Pre(f, port, bool(follower), _fields(payload, PRE_FIELDS))
            else:
                frame.post[key] = Post(f, port, bool(follower), _fields(payload, POST_FIELDS))
        elif code == Event.GAME_END:
            end_method = payload[1]
    if start is None:
        raise ValueError("no Game Start event")
    return Replay(start, frames, end_method)


def read_game_start(path: Path) -> GameStart:
    with path.open("rb") as f:
        head = f.read(4096)
    if not head.startswith(HEADER):
        raise ValueError("not a .slp file")
    raw = head[len(HEADER) + 4:]
    sizes, pos = _payload_sizes(raw)
    if raw[pos] != Event.GAME_START:
        raise ValueError("second event is not Game Start")
    return _game_start(raw[pos: pos + 1 + sizes[Event.GAME_START]])
