import pytest

from ssbmhk_tools.recomp.gecko import CAVE, GeckoError, apply_codes, branch_targets, code_words, parent_return_skips, parse, select_ini

from . import asm

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


INI = """
[Gecko]
$Required: General Codes [someone]
04001000 38600001 #Unlock Everything [Datel]
C2001004 00000001 #External/Wanted/Wanted.asm
60000000 00000000
C2001010 00000001 #External/Cosmetic/Cosmetic.asm
38600000 00000000
$Optional: Extra [someone]
C2001020 00000001 #External/Wanted/Wanted.asm
38800000 00000000
[Gecko_Enabled]
$Required: General Codes
"""


def test_select_ini_keeps_only_named_codes_from_named_sections():
    text = select_ini(INI, sections=("$Required:",), paths={"External/Wanted/Wanted.asm"})
    assert parse(text) == ([], [(0x80001004, [0x60000000, 0x00000000])])


def test_select_ini_refuses_a_missing_code():
    with pytest.raises(GeckoError, match="Missing.asm"):
        select_ini(INI, sections=("$Required:",), paths={"External/Wanted/Wanted.asm", "Missing.asm"})


def test_bodies_that_overflow_the_cave_are_refused():
    big = "C2001004 00000400\n" + "60000000 60000000\n" * 0x3FF + "60000000 00000000\n"
    with pytest.raises(GeckoError, match="cave"):
        apply_codes(big)


def test_code_words_hides_data_that_only_a_bl_blrl_pair_reaches():
    words = [asm.bl(0x10), asm.mflr(3), asm.blr(), asm.blr(),
             asm.blrl(), 0x4BFFE818, 0x00003330, asm.b(-0x10)]
    assert code_words(CAVE, words) == (words[0], words[1], words[2], 0, asm.blrl(), 0, 0, 0)  # call sites see the blrl


def test_code_words_follows_branches_and_local_calls():
    words = [asm.bc(12, 2, 0xC), asm.bl(0xC), asm.b(0x8), asm.blr(), asm.blr(), 0x4BFFE818]
    assert code_words(CAVE, words) == (*words[:5], 0)


def test_branch_targets_finds_slippi_branch_macro_targets():
    lis = lambda d, v: (15 << 26) | (d << 21) | v          # noqa: E731
    ori = lambda a, s, v: (24 << 26) | (s << 21) | (a << 16) | v  # noqa: E731
    words = [lis(12, 0x8008), ori(12, 12, 0x0EE4), asm.mtctr(12), asm.bctr(),       # branch r12, 0x80080EE4
             lis(12, 0x8038), ori(12, 12, 0xFD54), asm.mtctr(12), 0x4E800421,        # branchl r12, 0x8038FD54
             lis(3, 0x8045), ori(3, 3, 0x3080), asm.blr(), 0]                        # a data address: not a target
    assert branch_targets(words) == {0x80080EE4, 0x8038FD54}


def test_parent_return_skips_finds_return_to_caller_plus_k():
    # UCF Shield Drop: lwz r7,0x1C(r1); addi r1,r1,0x18; addi r7,r7,8; mtlr r7; blr
    tail = [0x80E1001C, 0x38210018, 0x38E70008, 0x7CE803A6, 0x4E800020]
    assert parent_return_skips([asm.blr(), *tail]) == {8}
    assert parent_return_skips([asm.addi(7, 7, 8), asm.blr()]) == set()   # no mtlr: not a return
