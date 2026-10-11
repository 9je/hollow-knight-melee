import struct

import pytest

from ssbmhk_tools.gxtex import UnsupportedTexture, decode


def test_rgb5a3_opaque_and_translucent_texels():
    # one 4x4 block: first texel opaque white (1 11111 11111 11111), second 50% grey translucent (0 011 1000 1000 1000)
    texels = [0xFFFF, 0x3888] + [0x8000] * 14
    rgba = decode(5, 4, 4, struct.pack(">16H", *texels))
    assert rgba[0:4] == bytes([255, 255, 255, 255])
    assert rgba[4:8] == bytes([0x88, 0x88, 0x88, 0x6D])


def test_i8_is_grey():
    rgba = decode(1, 8, 4, bytes(range(32)))
    assert rgba[0:4] == bytes([0, 0, 0, 255]) and rgba[4:8] == bytes([1, 1, 1, 255])


def test_cmpr_block_endpoints():
    # one 8x8 CMPR tile = four 4x4 DXT1 sub-blocks; colour0 red, colour1 blue, all indices 0 -> red
    sub = struct.pack(">HHI", 0xF800, 0x001F, 0)
    rgba = decode(14, 8, 8, sub * 4)
    assert rgba[0:4] == bytes([255, 0, 0, 255])


def test_unknown_format_is_reported():
    with pytest.raises(UnsupportedTexture):
        decode(7, 4, 4, bytes(32))
