from ssbmhk_tools.recomp.decode import decode
from ssbmhk_tools.recomp.emit import emit_insn


def e(word): return emit_insn(decode(word), 0x80003000)


def test_addi_with_r0_is_absolute():
    assert e(0x38600005) == "c->gpr[3] = 0u + (uint32_t)5;"


def test_stwu_updates_base_after_store():
    out = e(0x9421FFE0)
    assert out.index("st32(") < out.index("c->gpr[1] = ea_")


def test_add_dot_sets_cr0_from_rd():
    assert e(0x7C632215).endswith("CR0(c->gpr[3]);")


def test_rlwinm_mask_is_constant():
    assert "0xFFFFFFFCu" in e(0x5483103A)


def test_mtcrf_mask():
    # mtcrf 0x80, r3 -> only CR0
    assert "0xF0000000u" in e((31 << 26) | (3 << 21) | (0x80 << 12) | (144 << 1))
