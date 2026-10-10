CC ?= gcc
CXX ?= g++
GEN_CFLAGS := -O2 -std=c11 -ffp-contract=off -fno-fast-math -fwrapv -fno-strict-aliasing -Wall -Wno-unused-label
INC := -Igekko -Iruntime
B := build

$(B):
	mkdir -p $(B)

gekko-test: | $(B)
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_cpu.c runtime/test_hooks.c gekko/gekko_fp.c -lm -o $(B)/test_cpu
	$(B)/test_cpu
.PHONY: gekko-test
