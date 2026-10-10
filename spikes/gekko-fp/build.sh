#!/bin/bash
# usage: build.sh <tag> <cflags...>   -> build/harness_<tag>
set -e
cd "$(dirname "$0")"
tag=$1; shift
INC="-Istubs -Idolphin-src/Source/Core -I. -I../../core/gekko"
CXXF="-std=c++23 $INC"
mkdir -p build/$tag
for f in Interpreter_FloatingPoint Interpreter_Paired Interpreter_LoadStorePaired FloatUtils; do
  g++ $CXXF "$@" -c build/$f.cpp -o build/$tag/$f.o
done
g++ $CXXF "$@" -c harness.cpp -o build/$tag/harness.o
CSTD=${CSTD:--std=c99}
gcc $CSTD "$@" -c ../../core/gekko/gekko_fp.c -o build/$tag/gekko_fp.o
g++ build/$tag/*.o -o build/harness_$tag
