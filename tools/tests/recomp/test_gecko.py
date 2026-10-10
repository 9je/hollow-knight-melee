import pytest

from ssbmhk_tools.recomp.gecko import CAVE, GeckoError, apply_codes, parse

TEXT = """
$Some code [someone]
04001000 38600005
C2001004 00000002
38800007 7C632214
60000000 00000000
* comment
"""


def test_parse_04_and_c2():
    writes, injections = parse(TEXT)
    assert writes == [(0x80001000, 0x38600005)]
    assert injections == [(0x80001004, [0x38800007, 0x7C632214, 0x60000000, 0x00000000])]


def test_apply_places_body_in_cave_with_branch_back():
    patched, bodies = apply_codes(TEXT)
    assert patched[0x80001000] == 0x38600005
    b = patched[0x80001004]                       # b CAVE
    assert b >> 26 == 18 and (b & 3) == 0 and 0x80001004 + ((b & 0x03FFFFFC) ^ 0x02000000) - 0x02000000 == CAVE
    addr, body = bodies[0]
    assert addr == CAVE and body[:3] == [0x38800007, 0x7C632214, 0x60000000]
    back = body[3]                                # final 00000000 becomes b 0x80001008
    assert back >> 26 == 18 and (CAVE + 12 + ((back & 0x03FFFFFC) ^ 0x02000000) - 0x02000000) == 0x80001008


def test_unsupported_code_type_is_an_error():
    with pytest.raises(GeckoError):
        parse("06001000 00000004\n11223344 00000000")
