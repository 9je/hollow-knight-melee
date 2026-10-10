#!/bin/bash
# THROWAWAY: regenerate with spikes/boot/hle.txt, rebuild incrementally, run the boot probe.
set -e
cd "$(dirname "$0")/../.."
make -s -C core gen GEN=build/gen-boot HLE=../spikes/boot/hle.txt GECKO=build/ucf084.gecko > /dev/null
make -s -C core build/libguest.a GEN=build/gen-boot -j"$(nproc)" > /dev/null
gcc -O1 -g -std=c11 -Icore/runtime -Icore/gekko -Icore/build/gen-boot spikes/boot/boot.c core/build/libguest.a -lm -o core/build/boot
ISO=$(python3 -c "import tomllib;print(tomllib.load(open('dev.toml','rb'))['iso'])")
timeout 120 gdb -q -batch -ex run -ex bt --args ./core/build/boot core/build/gen-boot/mem1.bin "$ISO" "${1:-5}" ${2:-} ${3:-} < /dev/null > /tmp/claude-1000/boot.txt 2>&1 || true
grep -E "^(TRAP|FAULT|TIMEOUT|BAD|INDIRECT|__start)" /tmp/claude-1000/boot.txt | head -3
grep "^  CC00" /tmp/claude-1000/boot.txt | sort -t x -k2 -n | tail -3
grep -E "^#[0-9]+ +0x[0-9a-f]+ in (fn|hle)_" /tmp/claude-1000/boot.txt | head -8 | awk '{print $4}' | while read f; do a=${f#fn_}; n=$(grep -m1 -i "0x$a;" ~/src-ref/melee/config/GALE01/symbols.txt | cut -d' ' -f1); echo "  $f $n"; done
