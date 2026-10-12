#!/bin/sh
# Build the x86-64 engine payload that FEX runs on an arm64 device: the game files the dedicated
# server opens while loading a game, plus the system libraries it loads, taken from this machine.
#
#   make-payload.sh <game dir> <project .sbproj, relative to game dir> <output dir>
#
# Run on x86-64 Linux with glibc 2.39+ (Ubuntu 24.04 / Debian 13), after ./Setup.sh, with strace
# installed. The output holds game/ (engine + content, hardlinked where possible) and x86root/
# (the FEX guest rootfs: glibc, libstdc++ and friends).
set -eu
GAME=$(realpath "$1"); PROJECT=$2; OUT=$3
TRACE=$(mktemp); FILES=$(mktemp)
trap 'rm -f "$TRACE" "$FILES"' EXIT

# Load the game once under strace, then stop the server
( cd "$GAME" && timeout 180 strace -f -qq -e trace=openat,open,stat,newfstatat,access,execve -o "$TRACE" \
	./sbox-server +game "$PROJECT" > /dev/null 2>&1 ) || true
grep -q 'game.load success' "$GAME/logs/sbox-server.log" 2>/dev/null || echo "warning: no 'game.load success' in the server log" >&2

grep -v 'ENOENT\|ENOTDIR' "$TRACE" | grep -oE '"/[^"]+"' | tr -d '"' | sort -u > "$FILES"

mkdir -p "$OUT/game" "$OUT/x86root"
copy() { mkdir -p "$(dirname "$2")"; ln -f "$1" "$2" 2>/dev/null || cp -f "$1" "$2"; }

# Engine and content the run touched, plus the runtime and managed code in full
{ grep "^$GAME/" "$FILES" | sed "s#^$GAME/##"; ( cd "$GAME" && find bin/managed bin/dotnet "$(dirname "$PROJECT")" -type f ); } \
	| grep -vE '^(logs|\.source2|config/convar)' | sort -u | while read -r f; do
	if [ -f "$GAME/$f" ]; then copy "$GAME/$f" "$OUT/game/$f"; fi
done
# Unpatched dedicated servers throw loading a local project without a Libraries folder
# (fixed by engine-patches/0001); an empty one keeps the payload working on stock engines
mkdir -p "$OUT/game/$(dirname "$PROJECT")/Libraries"

rm -f "$OUT/game/sbox-server" && cp "$GAME/sbox-server" "$OUT/game/sbox-server" # a real copy, so patching it never touches the original

# System libraries, by their canonical paths, never certificates or host configuration
for f in $(grep -E '^/(usr/)?lib(64)?/' "$FILES") /lib64/ld-linux-x86-64.so.2; do
	[ -f "$f" ] || continue
	mkdir -p "$OUT/x86root$(dirname "$f")"
	cp -L "$f" "$OUT/x86root$f"
done
mkdir -p "$OUT/x86root/etc/ssl/certs" && printf 'nameserver 1.1.1.1\nnameserver 8.8.8.8\n' > "$OUT/x86root/etc/resolv.conf"

# TLS roots: the distribution's Mozilla set only, never CAs this machine added locally
cat /usr/share/ca-certificates/mozilla/*.crt > "$OUT/x86root/etc/ssl/certs/ca-certificates.crt"

du -sh "$OUT/game" "$OUT/x86root"
