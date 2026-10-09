"""struct Fighter fields to record, from doldecomp src/melee/ft/types.h and lb/types.h (79b13a3)."""
import struct
from dataclasses import dataclass

FRAME_COUNTER_ADDR = 0x80479D60
PLAYER_BLOCK = 0x80453080
PLAYER_STRIDE = 0xE90
ENTITY_OFF = 0xB0
GOBJ_USER_DATA = 0x2C

ECB_OFF = 0x794        # CollData.ecb (fp+0x6F0 + 0xA4)
HIT_OFF = 0x914        # HitCapsule x914[4], 0x138 each
HURT_OFF = 0x11A0      # FighterHurtCapsule hurt_capsules[15], 0x4C each


@dataclass(frozen=True)
class Field:
    name: str
    offset: int
    kind: str
    byte: int = 0


def _vec(name: str, off: int, n: int = 3) -> list[Field]:
    return [Field(f"{name}.{a}", off + 4 * i, "f32") for i, a in enumerate("xyz"[:n])]


def _fields() -> list[Field]:
    out = [
        Field("motion_id", 0x10, "u32"),
        Field("facing", 0x2C, "f32"),
        *_vec("self_vel", 0x80),
        *_vec("kb_vel", 0x8C),
        *_vec("pos", 0xB0),
        *_vec("gr_vel", 0xEC, 1),
        Field("ground_or_air", 0xE0, "u32"),
        Field("anim_frame", 0x894, "f32"),
        Field("percent", 0x1830, "f32"),
        Field("jumps_used", 0x1968, "u8", 0),
        Field("shield", 0x1998, "f32"),
        Field("env_flags", 0x6F0 + 0x134, "u32"),
        # struct Fighter_input at +0x620: index 0 = current frame (processed, post-deadzone)
        Field("in.lstick.x", 0x620, "f32"), Field("in.lstick.y", 0x624, "f32"),
        Field("in.cstick.x", 0x638, "f32"), Field("in.cstick.y", 0x63C, "f32"),
        Field("in.trigger", 0x650, "f32"), Field("in.held", 0x65C, "u32"),
    ]
    for i, corner in enumerate(("top", "bottom", "right", "left")):
        out += _vec(f"ecb.{corner}", ECB_OFF + 8 * i, 2)
    for i in range(4):
        base = HIT_OFF + 0x138 * i
        out += [Field(f"hit{i}.state", base, "u32"), Field(f"hit{i}.damage", base + 0xC, "f32"),
                Field(f"hit{i}.scale", base + 0x1C, "f32"), Field(f"hit{i}.kb_angle", base + 0x20, "s32"),
                *_vec(f"hit{i}.pos", base + 0x4C)]
    for i in range(15):
        base = HURT_OFF + 0x4C * i
        out += [Field(f"hurt{i}.state", base, "u32"), *_vec(f"hurt{i}.a_pos", base + 0x28),
                *_vec(f"hurt{i}.b_pos", base + 0x34)]
    return out


FIGHTER_FIELDS = _fields()


def location(port: int, f: Field) -> str:
    entity = PLAYER_BLOCK + PLAYER_STRIDE * port + ENTITY_OFF
    return f"{entity:08X} {GOBJ_USER_DATA:X} {f.offset:X}"


def counter_location() -> str:
    return f"{FRAME_COUNTER_ADDR:08X}"


def field_key(port: int, f: Field) -> str:
    return f"p{port}.{f.name}"


def locations(ports: list[int]) -> list[str]:
    return [location(p, f) for p in ports for f in FIGHTER_FIELDS] + [counter_location()]


def decode(kind: str, byte: int, word: int) -> float | int:
    b = struct.pack(">I", word)
    if kind == "f32":
        return struct.unpack(">f", b)[0]
    if kind == "u32":
        return word
    if kind == "s32":
        return struct.unpack(">i", b)[0]
    if kind == "u8":
        return b[byte]
    if kind == "s16":
        return struct.unpack(">h", b[byte:byte + 2])[0]
    raise ValueError(kind)
