"""Melee's sound effects, read from the user's ISO into one bundle for the mod (`melee.sfx`, not for distribution).

Sources (big-endian, see docs/superpowers/research/2026-10-11-melee-audio.md):
- smash2.sem: five tables `u32 count; u32 entries[count]`; table 2 = first script per bank (bank_base), table 3 = script
  offsets in the file. Sound id = bank * 10000 + member; script = scripts[bank_base[bank] + member]. A script is u32
  words, type = w >> 24, p = w & 0xFFFFFF (AXDriverInterp).
- *.ssm: sample banks of DSP-ADPCM sounds; global sample id = the bank's base_id + index.

Bundle `melee.sfx` (little-endian):

    char magic[4] = "MSFX"; u32 version = 1; u32 n_scripts; u32 n_samples
    n_scripts x { u32 sound_id; u32 n_cmds; n_cmds x { u8 op; u8 pad[3]; i32 value; u32 wait } }
    n_samples x { u32 sample_id; u32 rate; u16 channels; u16 pad; u32 frames; i32 loop_start;
                  i16 pcm[frames * channels] (interleaved) }

Commands are the script's words in order (one per word, so a loop end's `value` counts commands back):
op 0 wait (value = wait = ticks); 1 sample (value = global sample id); 2 loop start (value = count, 0 forever); 3 loop
end (jump back `value` commands); 4/5 priority set/add; 6/7 volume set/add (0..255); 8/9 pan set/add (0..255, 0x80
centre); 10/11 unused; 12/13 pitch set/add (cents); 14 end (the sample plays on); 15 end and key off; 16-21 aux sends;
0xFD ignored. `wait` is the delay after the command in ticks (1 tick = 5 ms); a command's value is heard once the
voice next waits or ends. *_add values are signed. loop_start: first frame of the looped part, -1 for one-shots.
Scripts are those of banks 0 (main) and 11 (Fox); samples are every sample they reference.
"""
import argparse
import struct
from dataclasses import dataclass
from pathlib import Path

from . import gciso

BANKS = {0: "main.ssm", 11: "fox.ssm"}
MAGIC, VERSION = b"MSFX", 1
_SIGNED8 = {5, 7, 9, 11, 17, 19}


def _s8(x): return x - 0x100 if x & 0x80 else x
def _s16(x): return x - 0x10000 if x & 0x8000 else x


def parse_command(w: int) -> tuple[int, int, int]:
    """One script word -> (op, value, wait ticks), as AXDriverInterp and parseWait read it."""
    t, p = w >> 24, w & 0xFFFFFF
    if t == 0:
        return 0, p, p
    if t in (6, 7, 8, 9, 10, 11, 16, 17, 18, 19):
        return t, _s8(p & 0xFF) if t in _SIGNED8 else p & 0xFF, (p >> 8) & 0xFFFF
    if t in (12, 13):
        return t, _s16(p & 0xFFFF), (p >> 16) & 0xFF
    if t in (1, 2):
        return t, p & 0xFFFF, 0
    if t == 4:
        return t, p & 0xFF, 0
    if t == 5:
        return t, _s8(p & 0xFF), 0
    if t == 3:
        return t, p, 0
    return t, p if t in (20, 21) else 0, 0


class Sem:
    def __init__(self, b: bytes):
        o, tables = 0, []
        for _ in range(5):
            n = struct.unpack_from(">I", b, o)[0]
            tables.append(list(struct.unpack_from(f">{n}I", b, o + 4)))
            o += 4 + 4 * n
        self.bank_base, self.scripts, self.b = tables[2], tables[3], b

    def bank_size(self, bank: int) -> int:
        end = self.bank_base[bank + 1] if bank + 1 < len(self.bank_base) else len(self.scripts)
        return end - self.bank_base[bank]

    def commands(self, sound_id: int, max_words: int = 256):
        """The sound's script as [(op, value, wait)], up to its end (14/15); None for an id Melee refuses."""
        bank, member = divmod(sound_id, 10000)
        if bank >= len(self.bank_base) or member >= self.bank_size(bank):
            return None
        o = self.scripts[self.bank_base[bank] + member]
        out = []
        for k in range(max_words):
            out.append(parse_command(struct.unpack_from(">I", self.b, o + 4 * k)[0]))
            if out[-1][0] in (14, 15):
                break
        return out


class Ssm:
    """0x10 header {header_size, data_size, count, base_id}; entries {u32 nchan, u32 rate, nchan x 0x40 channel
    header}; ADPCM data at roundup32(header_size + 0x10)."""
    def __init__(self, b: bytes):
        self.header_size, self.data_size, self.count, self.base = struct.unpack_from(">4I", b, 0)
        off = (self.header_size + 0x10 + 31) & ~31
        self.data = b[off:off + self.data_size]
        self.sounds, o = [], 0x10
        for _ in range(self.count):
            n, rate = struct.unpack_from(">II", b, o)
            chans = []
            for k in range(n):
                h = o + 8 + 0x40 * k
                loop, fmt, loop_a, end_a, cur_a = struct.unpack_from(">HHIII", b, h)
                coefs = struct.unpack_from(">16h", b, h + 0x10)
                _gain, _ps, yn1, yn2 = struct.unpack_from(">HHhh", b, h + 0x30)
                chans.append(dict(loop=loop, fmt=fmt, loop_addr=loop_a, end_addr=end_a, cur_addr=cur_a,
                                  coefs=coefs, yn1=yn1, yn2=yn2))
            self.sounds.append((rate, chans))
            o += 8 + 0x40 * n

    def has(self, sample_id: int) -> bool:
        return self.base <= sample_id < self.base + self.count


def dsp_decode(data: bytes, ch: dict) -> list[int]:
    """GameCube DSP-ADPCM, nibble addresses cur_addr..end_addr inclusive (8-byte frames: a header byte
    pred << 4 | scale, then 14 samples; the header's two nibbles are skipped)."""
    coefs, h1, h2 = ch["coefs"], ch["yn1"], ch["yn2"]
    out = []
    a, end = ch["cur_addr"], ch["end_addr"]
    c1 = c2 = scale = 0
    while a <= end:
        if a % 16 < 2:
            a = (a & ~15) + 2
            continue
        if a % 16 == 2 or not out:
            ps = data[(a & ~15) // 2]
            scale, pred = 1 << (ps & 0xF), ps >> 4
            c1, c2 = coefs[pred * 2], coefs[pred * 2 + 1]
        byte = data[a // 2]
        nib = byte >> 4 if a % 2 == 0 else byte & 0xF
        if nib >= 8:
            nib -= 16
        s = (((nib * scale) << 11) + 1024 + c1 * h1 + c2 * h2) >> 11
        s = -32768 if s < -32768 else 32767 if s > 32767 else s
        out.append(s)
        h2, h1 = h1, s
        a += 1
    return out


def _sample_index(a: int) -> int:
    return (a // 16) * 14 + a % 16 - 2


@dataclass
class Sample:
    sample_id: int
    rate: int
    channels: list            # per channel, list of int16
    loop_start: int = -1


def decode_sample(ssm: Ssm, sample_id: int) -> Sample:
    rate, chans = ssm.sounds[sample_id - ssm.base]
    pcm = [dsp_decode(ssm.data, c) for c in chans]
    n = min(map(len, pcm))
    c0 = chans[0]
    loop = _sample_index(c0["loop_addr"]) - _sample_index(c0["cur_addr"]) if c0["loop"] else -1
    return Sample(sample_id, rate, [p[:n] for p in pcm], loop if 0 <= loop < n else -1)


def write_bundle(path: Path, scripts: dict, samples: dict) -> None:
    out = [MAGIC, struct.pack("<III", VERSION, len(scripts), len(samples))]
    for sid in sorted(scripts):
        cmds = scripts[sid]
        out.append(struct.pack("<II", sid, len(cmds)))
        out += [struct.pack("<B3xiI", op, value, wait) for op, value, wait in cmds]
    for sid in sorted(samples):
        s = samples[sid]
        n = len(s.channels[0])
        out.append(struct.pack("<IIHxxIi", sid, s.rate, len(s.channels), n, s.loop_start))
        inter = [x for frame in zip(*s.channels) for x in frame]
        out.append(struct.pack(f"<{len(inter)}h", *inter))
    Path(path).write_bytes(b"".join(out))


def read_bundle(path: Path) -> tuple[dict, dict]:
    b = Path(path).read_bytes()
    if b[:4] != MAGIC:
        raise ValueError(f"{path}: not a Melee sound bundle")
    version, ns, nm = struct.unpack_from("<III", b, 4)
    if version != VERSION:
        raise ValueError(f"{path}: bundle version {version}, expected {VERSION}")
    o, scripts, samples = 16, {}, {}
    for _ in range(ns):
        sid, n = struct.unpack_from("<II", b, o)
        o += 8
        scripts[sid] = [struct.unpack_from("<B3xiI", b, o + 12 * k) for k in range(n)]
        o += 12 * n
    for _ in range(nm):
        sid, rate, nch, n, loop = struct.unpack_from("<IIHxxIi", b, o)
        o += 20
        inter = struct.unpack_from(f"<{n * nch}h", b, o)
        o += 2 * n * nch
        samples[sid] = Sample(sid, rate, [list(inter[c::nch]) for c in range(nch)], loop)
    return scripts, samples


def extract(iso: Path) -> tuple[dict, dict]:
    sem = Sem(gciso.read_file(iso, "smash2.sem"))
    ssms = {bank: Ssm(gciso.read_file(iso, name)) for bank, name in BANKS.items()}
    scripts, samples = {}, {}
    for bank in sorted(BANKS):
        for member in range(sem.bank_size(bank)):
            sid = bank * 10000 + member
            cmds = sem.commands(sid)
            scripts[sid] = cmds
            for op, value, _ in cmds:
                if op != 1 or value in samples:
                    continue
                ssm = next((s for s in (ssms[bank], ssms[0]) if s.has(value)), None)
                if ssm is not None:
                    samples[value] = decode_sample(ssm, value)
    return scripts, samples


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Extract Melee's main and Fox sound effects from your ISO into "
                                             "melee.sfx (not for distribution).")
    ap.add_argument("iso", type=Path)
    ap.add_argument("--out", type=Path, required=True)
    a = ap.parse_args(argv)
    scripts, samples = extract(a.iso)
    a.out.mkdir(parents=True, exist_ok=True)
    path = a.out / "melee.sfx"
    write_bundle(path, scripts, samples)
    secs = sum(len(s.channels[0]) / s.rate for s in samples.values())
    print(f"melee.sfx: {len(scripts)} sounds, {len(samples)} samples ({secs:.1f} s), {path.stat().st_size} bytes")
    return 0
