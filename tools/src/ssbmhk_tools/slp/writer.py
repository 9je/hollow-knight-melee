import struct
from dataclasses import dataclass, field

from .events import (
    Event, GAME_INFO_SIZE, INT_FOX, PLAYER_STRIDE, WRITER_SIZES, WRITER_VERSION,
)


@dataclass
class PreFrame:
    frame: int
    port: int
    follower: bool = False
    seed: int = 0
    action: int = 0
    x: float = 0.0
    y: float = 0.0
    facing: float = 1.0
    joy_x: float = 0.0
    joy_y: float = 0.0
    c_x: float = 0.0
    c_y: float = 0.0
    trigger: float = 0.0
    buttons: int = 0
    phys_buttons: int = 0
    phys_l: float = 0.0
    phys_r: float = 0.0
    raw_x: int = 0
    percent: float = 0.0


@dataclass
class PostFrame:
    frame: int
    port: int
    follower: bool = False
    char: int = INT_FOX
    action: int = 0
    x: float = 0.0
    y: float = 0.0
    facing: float = 1.0
    percent: float = 0.0
    shield: float = 60.0
    last_attack: int = 0
    combo: int = 0
    last_hit_by: int = 6
    stocks: int = 4
    anim_frame: float = 0.0
    flags: bytes = field(default=bytes(5))
    misc_as: float = 0.0
    airborne: int = 0
    ground_id: int = 0
    jumps: int = 0
    lcancel: int = 0
    hurtbox_state: int = 0
    self_air_x: float = 0.0
    self_y: float = 0.0
    kb_x: float = 0.0
    kb_y: float = 0.0
    self_ground_x: float = 0.0


def make_game_info(stage: int, players: list[tuple[int, int, int]]) -> bytes:
    gi = bytearray(GAME_INFO_SIZE)
    struct.pack_into(">H", gi, 0xE, stage)
    for i in range(4):
        base = 0x60 + PLAYER_STRIDE * i
        if i < len(players):
            ext, ptype, costume = players[i]
            gi[base:base + 4] = bytes([ext, ptype, 4, costume])
        else:
            gi[base + 1] = 3
    return bytes(gi)


def _pack_pre(p: PreFrame) -> bytes:
    return struct.pack(
        ">BiBBIHffffffffIHffbf",
        Event.PRE_FRAME, p.frame, p.port, int(p.follower), p.seed, p.action, p.x, p.y,
        p.facing, p.joy_x, p.joy_y, p.c_x, p.c_y, p.trigger, p.buttons, p.phys_buttons,
        p.phys_l, p.phys_r, p.raw_x, p.percent,
    )


def _pack_post(p: PostFrame) -> bytes:
    return struct.pack(
        ">BiBBBHfffffBBBBf5sfBHBBBfffff",
        Event.POST_FRAME, p.frame, p.port, int(p.follower), p.char, p.action, p.x, p.y,
        p.facing, p.percent, p.shield, p.last_attack, p.combo, p.last_hit_by, p.stocks,
        p.anim_frame, p.flags, p.misc_as, p.airborne, p.ground_id, p.jumps, p.lcancel,
        p.hurtbox_state, p.self_air_x, p.self_y, p.kb_x, p.kb_y, p.self_ground_x,
    )


class SlpWriter:
    def __init__(self, game_info: bytes, seed: int = 0):
        assert len(game_info) == GAME_INFO_SIZE
        table = b"".join(struct.pack(">BH", int(k), v) for k, v in WRITER_SIZES.items())
        start = bytearray(1 + WRITER_SIZES[Event.GAME_START])
        start[0] = Event.GAME_START
        start[1:5] = bytes([*WRITER_VERSION, 0])
        start[5:5 + GAME_INFO_SIZE] = game_info
        struct.pack_into(">I", start, 0x13D, seed)
        self._raw = bytearray([Event.PAYLOADS, len(table) + 1]) + table + start

    def _emit(self, payload: bytes) -> None:
        expected = 1 + WRITER_SIZES[Event(payload[0])]
        assert len(payload) == expected, (Event(payload[0]), len(payload), expected)
        self._raw += payload

    def frame(self, frame: int, pre: list[PreFrame], post: list[PostFrame], seed: int = 0) -> None:
        self._emit(struct.pack(">BiI", Event.FRAME_START, frame, seed))
        for p in pre:
            self._emit(_pack_pre(p))
        for p in post:
            self._emit(_pack_post(p))
        self._emit(struct.pack(">Bi", Event.FRAME_BOOKEND, frame))

    def end(self, method: int = 2) -> None:
        self._emit(struct.pack(">Bbb", Event.GAME_END, method, -1))

    def to_bytes(self) -> bytes:
        return b"{U\x03raw[$U#l" + struct.pack(">i", len(self._raw)) + bytes(self._raw) + b"}"
