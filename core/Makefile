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
DOLSRC := difftest/dolphin-src/Source/Core
DOL_INTERP := Interpreter_Integer Interpreter_LoadStore Interpreter_Branch Interpreter_SystemRegisters \
              Interpreter_FloatingPoint Interpreter_Paired Interpreter_LoadStorePaired Interpreter_Tables
STUB_HDRS := $(shell find difftest/stubs -name '*.h')
DOL_CXXFLAGS := -O2 -std=c++23 -Idifftest/stubs -I$(DOLSRC) -Igekko -Iruntime -msse2 -mfpmath=sse -ffp-contract=off

dolphin-src:
	bash difftest/fetch_dolphin.sh

$(B)/oracle/%.o: $(DOLSRC)/Core/PowerPC/Interpreter/%.cpp $(STUB_HDRS) | dolphin-src
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/FloatUtils.o: $(DOLSRC)/Common/FloatUtils.cpp
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/ConditionRegister.o: $(DOLSRC)/Core/PowerPC/ConditionRegister.cpp
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/oracle.o: difftest/oracle.cpp difftest/oracle.h $(STUB_HDRS)
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
ORACLE_OBJS := $(DOL_INTERP:%=$(B)/oracle/%.o) $(B)/oracle/FloatUtils.o $(B)/oracle/ConditionRegister.o $(B)/oracle/oracle.o
$(B)/liboracle.a: $(ORACLE_OBJS)
	ar rcs $@ $^

oracle-selftest: $(B)/liboracle.a
	$(CC) $(GEN_CFLAGS) $(INC) -Idifftest difftest/oracle_selftest.c $(B)/liboracle.a -lstdc++ -lm -o $(B)/oracle_selftest
	$(B)/oracle_selftest
.PHONY: dolphin-src oracle-selftest
INSN_DIR := $(B)/insn
insn-cases:
	cd ../tools && .venv/bin/ssbmhk-recomp-insn-tests --config ../dev.toml --out ../core/$(INSN_DIR) $(INSN_ARGS)
insn-fuzz: $(B)/liboracle.a insn-cases
	$(CC) $(GEN_CFLAGS) $(OPT) $(INC) -Idifftest $(INSN_DIR)/insn_cases.c difftest/insn_fuzz.c runtime/test_hooks.c gekko/gekko_fp.c $(B)/liboracle.a -lstdc++ -lm -o $(B)/insn_fuzz
	$(B)/insn_fuzz $(STATES) 1
.PHONY: insn-cases insn-fuzz
STATES ?= 200
GEN := $(B)/gen
gen:
	cd ../tools && .venv/bin/ssbmhk-recomp --config ../dev.toml --out ../core/$(GEN) $(if $(HLE),--hle ../core/$(HLE))
GUEST_SRCS = $(wildcard $(GEN)/shard_*.c) $(GEN)/guest_table.c
GUEST_OBJS = $(GUEST_SRCS:$(GEN)/%.c=$(B)/guest/%.o)
$(B)/guest/%.o: $(GEN)/%.c
	mkdir -p $(B)/guest
	$(CC) $(GEN_CFLAGS) $(OPT) $(INC) -I$(GEN) -c $< -o $@
$(B)/libguest.a: $(GUEST_OBJS) $(B)/guest/dispatch.o $(B)/guest/gekko_fp.o
	ar rcs $@ $^
$(B)/guest/dispatch.o: runtime/dispatch.c
	mkdir -p $(B)/guest; $(CC) $(GEN_CFLAGS) $(INC) -c $< -o $@
$(B)/guest/gekko_fp.o: gekko/gekko_fp.c
	mkdir -p $(B)/guest; $(CC) $(GEN_CFLAGS) $(INC) -c $< -o $@
guest: gen
	$(MAKE) $(B)/libguest.a
.PHONY: gen guest
func-diff: $(B)/liboracle.a $(B)/libguest.a
	$(CC) $(GEN_CFLAGS) $(INC) -Idifftest -I$(GEN) difftest/func_diff.c runtime/test_hooks.c \
	  $(B)/libguest.a $(B)/liboracle.a -lstdc++ -lm -o $(B)/func_diff
.PHONY: func-diff
