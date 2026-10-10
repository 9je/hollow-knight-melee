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
def test_fadds_writes_both_halves():
    assert e(0xEC22182A) == "c->fpr[1] = gekko_fadds(c->fpscr, c->fpr[2].ps0, c->fpr[3].ps0);"


def test_fmadd_double_keeps_ps1_and_orders_a_c_b():
    # fmadd f1,f2,f3,f4 : a=2 c=3 b=4
    assert e(0xFC2220FA) == "c->fpr[1].ps0 = gekko_fmadd(c->fpscr, c->fpr[2].ps0, c->fpr[3].ps0, c->fpr[4].ps0);"


def test_lfs_fills_both_halves():
    out = e(0xC0230008)   # lfs f1,8(r3)
    assert "c->fpr[1].ps0 = c->fpr[1].ps1 = u2f(gekko_convert_to_double(ld32(c, ea_)))" in out


def test_every_decoder_op_is_emittable():
    from ssbmhk_tools.recomp import decode as d
    names = (set(d.PRIMARY.values()) | set(d.OP19.values()) | set(d.OP31.values()) | set(d.OP31_XO.values())
             | set(d.OP59.values()) | set(d.OP63_A.values()) | set(d.OP63_X.values())
             | set(d.OP4_A.values()) | set(d.OP4_X.values()))
    missing = {n for n in names - d.BRANCH_OPS - d.TRAP_OPS - d.NOP_OPS
               if n not in __import__("ssbmhk_tools.recomp.emit", fromlist=["OPS"]).OPS}
    assert missing == set()


def test_fp_load_store_faults_on_misaligned_ea():
    # Dolphin raises an alignment exception for every FP load/store whose EA is not 4-byte aligned
    for word in (0xC0230008, 0xC8230008, 0xD0230008, 0xD8230008, 0x7C2327AE):  # lfs lfd stfs stfd stfiwx
        assert "if (ea_ & 3u) guest_fault(c, ea_" in e(word), hex(word)
