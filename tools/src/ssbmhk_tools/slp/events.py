from enum import IntEnum


class Event(IntEnum):
    PAYLOADS = 0x35
    GAME_START = 0x36
    PRE_FRAME = 0x37
    POST_FRAME = 0x38
    GAME_END = 0x39
    FRAME_START = 0x3A
    ITEM_UPDATE = 0x3B
    FRAME_BOOKEND = 0x3C


FIRST_FRAME = -123
EXT_FOX = 2
INT_FOX = 1
GAME_INFO_SIZE = 312
PLAYER_STRIDE = 0x24

WRITER_VERSION = (3, 5, 0)
# Payload sizes exclude the command byte (SPEC.md: offsets include it).
WRITER_SIZES = {
    Event.GAME_START: 0x1A2,   # through Frozen PS (0x1A2, v2.0)
    Event.PRE_FRAME: 0x3F,     # through percent (0x3C, v1.4)
    Event.POST_FRAME: 0x48,    # through self-induced ground x speed (0x45, v3.5)
    Event.GAME_END: 0x2,       # method, LRAS initiator
    Event.FRAME_START: 0x8,    # frame, seed
    Event.FRAME_BOOKEND: 0x4,  # frame
}
