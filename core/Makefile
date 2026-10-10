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
DOL_CXXFLAGS := -O2 -std=c++23 -Idifftest/stubs -I$(DOLSRC) -Igekko -Iruntime -msse2 -mfpmath=sse -ffp-contract=off

dolphin-src:
	bash difftest/fetch_dolphin.sh

$(B)/oracle/%.o: $(DOLSRC)/Core/PowerPC/Interpreter/%.cpp | dolphin-src
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/FloatUtils.o: $(DOLSRC)/Common/FloatUtils.cpp
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/ConditionRegister.o: $(DOLSRC)/Core/PowerPC/ConditionRegister.cpp
	mkdir -p $(B)/oracle
	$(CXX) $(DOL_CXXFLAGS) -c $< -o $@
$(B)/oracle/oracle.o: difftest/oracle.cpp difftest/oracle.h
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
