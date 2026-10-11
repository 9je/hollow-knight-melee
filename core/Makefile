CC ?= gcc
CXX ?= g++
GEN_CFLAGS := -fPIC -O2 -std=c11 -ffp-contract=off -fno-fast-math -fwrapv -fno-strict-aliasing -Wall -Wno-unused-label
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
GOBJ ?= $(B)/guest
LIBGUEST ?= $(B)/libguest.a
gen:
	cd ../tools && .venv/bin/ssbmhk-recomp --config ../dev.toml --out ../core/$(GEN) $(if $(HLE),--hle ../core/$(HLE)) $(if $(GECKO),--gecko ../core/$(GECKO)) $(if $(TAP),--tap ../core/$(TAP))
GUEST_SRCS = $(wildcard $(GEN)/shard_*.c) $(GEN)/guest_table.c
GUEST_OBJS = $(GUEST_SRCS:$(GEN)/%.c=$(GOBJ)/%.o)
GUEST_HDRS := runtime/cpu.h gekko/gekko_fp.h
$(GOBJ)/%.o: $(GEN)/%.c $(GUEST_HDRS)
	mkdir -p $(GOBJ)
	$(CC) $(GEN_CFLAGS) $(OPT) $(INC) -I$(GEN) -c $< -o $@
$(LIBGUEST): $(GUEST_OBJS) $(GOBJ)/dispatch.o $(GOBJ)/gekko_fp.o
	ar rcs $@ $^
$(GOBJ)/dispatch.o: runtime/dispatch.c runtime/guest_table.h $(GUEST_HDRS)
	mkdir -p $(GOBJ); $(CC) $(GEN_CFLAGS) $(INC) -c $< -o $@
$(GOBJ)/gekko_fp.o: gekko/gekko_fp.c gekko/gekko_fp.h
	mkdir -p $(GOBJ); $(CC) $(GEN_CFLAGS) $(INC) -c $< -o $@

# ---- libmelee_core (plan 1c) ----
CORE_GEN := $(B)/gen-core
GECKO_CODES := $(B)/slippi_gameplay.gecko
$(GECKO_CODES): fetch_gecko.sh runtime/slippi_gameplay.txt
	bash fetch_gecko.sh $@
gecko: $(GECKO_CODES)
$(B)/core.gecko: $(GECKO_CODES) runtime/online_rng.gecko
	cat $^ > $@
core-gen: $(B)/core.gecko
	$(MAKE) gen GEN=$(CORE_GEN) HLE=runtime/hle.txt GECKO=$(B)/core.gecko TAP=runtime/taps.txt
.PHONY: gecko core-gen
guest: gen
	$(MAKE) $(B)/libguest.a
.PHONY: gen guest
func-diff: $(B)/liboracle.a $(B)/libguest.a
	$(CC) $(GEN_CFLAGS) $(INC) -Idifftest -I$(GEN) difftest/func_diff.c runtime/test_hooks.c \
	  $(B)/libguest.a $(B)/liboracle.a -lstdc++ -lm -o $(B)/func_diff
.PHONY: func-diff
runtime-test: | $(B)
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_runtime.c runtime/hw.c runtime/coro.c gekko/gekko_fp.c -lm -o $(B)/test_runtime
	$(B)/test_runtime
.PHONY: runtime-test
CORE_GOBJ := $(B)/guest-core
RUNTIME_SRCS := hw coro irq hle_io frame melee_core room room_check room_build worker sfx
RUNTIME_HDRS := runtime/mc_internal.h runtime/coro.h runtime/melee_core.h runtime/room.h $(GUEST_HDRS)
$(CORE_GOBJ)/rt_%.o: runtime/%.c $(RUNTIME_HDRS) $(CORE_GEN)/guest_meta.h
	mkdir -p $(CORE_GOBJ)
	$(CC) $(GEN_CFLAGS) $(INC) -I$(CORE_GEN) -c $< -o $@
$(B)/libmelee_core.a: $(RUNTIME_SRCS:%=$(CORE_GOBJ)/rt_%.o) $(CORE_GOBJ)/libguest.a
	cp $(CORE_GOBJ)/libguest.a $@ && ar rs $@ $(RUNTIME_SRCS:%=$(CORE_GOBJ)/rt_%.o)
core: core-gen
	$(MAKE) $(CORE_GOBJ)/libguest.a GEN=$(CORE_GEN) GOBJ=$(CORE_GOBJ) LIBGUEST=$(CORE_GOBJ)/libguest.a -j$$(nproc)
	$(MAKE) $(B)/libmelee_core.a
ISO = $(shell python3 -c "import tomllib;print(tomllib.load(open('../dev.toml','rb'))['iso'])")
core-test: core
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_core.c $(B)/libmelee_core.a -lm -lpthread -o $(B)/test_core
	$(B)/test_core $(CORE_GEN)/mem1.bin "$(ISO)"
.PHONY: core core-test
replay-check-tool: core
	$(CC) $(GEN_CFLAGS) $(INC) tools/replay_check.c $(B)/libmelee_core.a -lm -lpthread -o $(B)/replay_check
.PHONY: replay-check-tool

# ---- rooms (plan 2) ----
room-test: | $(B)
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_room.c runtime/room_check.c runtime/room_build.c -lm -o $(B)/test_room
	$(B)/test_room
rooms-test: core
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_rooms.c $(B)/libmelee_core.a -lm -lpthread -o $(B)/test_rooms
	$(B)/test_rooms $(CORE_GEN)/mem1.bin "$(ISO)"
.PHONY: room-test rooms-test

# ---- shared library (plan 3) ----
$(B)/libmelee_core.so: $(B)/libmelee_core.a runtime/exports.map
	$(CC) -shared -o $@ -Wl,--whole-archive $(B)/libmelee_core.a -Wl,--no-whole-archive \
	  -Wl,--version-script=runtime/exports.map -Wl,-soname,libmelee_core.so -lm -lpthread
shared: core
	$(MAKE) $(B)/libmelee_core.so
shared-test: shared
	$(CC) $(GEN_CFLAGS) $(INC) tests/test_shared.c -ldl -lpthread -o $(B)/test_shared
	$(B)/test_shared $(B)/libmelee_core.so $(CORE_GEN)/mem1.bin "$(ISO)"
.PHONY: shared shared-test
