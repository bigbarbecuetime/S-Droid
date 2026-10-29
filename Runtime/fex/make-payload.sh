#!/bin/sh
# Build the x86-64 engine payload that FEX runs on an arm64 device: the game files the engine opens
# while loading a game, plus the system libraries it loads, taken from this machine.
#
#   make-payload.sh <game dir> <project .sbproj, relative to game dir> <output dir>   (dedicated server)
#   make-payload.sh <standalone dir from Runtime/linux/make-standalone.sh> --standalone <output dir>
#
# Run on x86-64 Linux with glibc 2.39+ (Ubuntu 24.04 / Debian 13), after ./Setup.sh, with strace
# installed (and Xvfb and Mesa's lavapipe for --standalone, which loads the game in a window). The
# output holds game/ (engine + content, hardlinked where possible) and x86root/ (the FEX guest
# rootfs: glibc, libstdc++, X11, Vulkan and friends).
set -eu
GAME=$(realpath "$1"); PROJECT=$2; OUT=$3
TRACE=$(mktemp); FILES=$(mktemp)
trap 'rm -f "$TRACE" "$FILES"' EXIT
STRACE="strace -f -qq -e trace=openat,open,stat,newfstatat,access,execve -o $TRACE"

if [ "$PROJECT" = --standalone ]; then
	# Load the game once in a window under strace, rendering with lavapipe, then stop it
	EXE=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Manifest"]["ExecutableName"])' "$GAME/standalone.json")
	LOG="$GAME/logs/$EXE.log"; CONTENT=assets
	Xvfb :96 -screen 0 1280x720x24 -nolisten tcp > /dev/null 2>&1 & XVFB=$!
	rm -f "$LOG"
	( cd "$GAME" && DISPLAY=:96 DOTNET_ROOT="$GAME/bin/dotnet" VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json \
		exec timeout 900 $STRACE ./"$EXE" -windowed -vulkan_allow_cpu ) > /dev/null 2>&1 & RUN=$!
	until grep -qE 'game.load (success|fail)' "$LOG" 2> /dev/null || ! kill -0 $RUN 2> /dev/null; do sleep 5; done
	sleep 30 # the first frames load shaders and textures
	kill $RUN 2> /dev/null || true; pkill -x "$EXE" || true; wait $RUN 2> /dev/null || true; kill $XVFB
else
	# Load the game once under strace, then stop the server
	LOG="$GAME/logs/sbox-server.log"; CONTENT=$(dirname "$PROJECT")
	( cd "$GAME" && timeout 180 $STRACE ./sbox-server +game "$PROJECT" > /dev/null 2>&1 ) || true
fi
grep -q 'game.load success' "$LOG" 2>/dev/null || echo "warning: no 'game.load success' in $LOG" >&2

grep -v 'ENOENT\|ENOTDIR' "$TRACE" | grep -oE '"/[^"]+"' | tr -d '"' | sort -u > "$FILES"

mkdir -p "$OUT/game"
copy() { mkdir -p "$(dirname "$2")"; ln -f "$1" "$2" 2>/dev/null || cp -f "$1" "$2"; }

# Engine and content the run touched, plus the runtime and managed code in full
{ grep "^$GAME/" "$FILES" | sed "s#^$GAME/##"; ( cd "$GAME" && find bin/managed bin/dotnet "$CONTENT" -type f; ls standalone.json "${EXE:-sbox-server}" 2> /dev/null ); } \
	| grep -vE '^(logs|\.source2|config/convar)' | sort -u | while read -r f; do
	if [ -f "$GAME/$f" ]; then copy "$GAME/$f" "$OUT/game/$f"; fi
done
# Older dedicated servers throw loading a local project without a Libraries folder
# (fixed upstream since); an empty one keeps the payload working on those engines
[ "$PROJECT" = --standalone ] || mkdir -p "$OUT/game/$CONTENT/Libraries"

EXE=${EXE:-sbox-server}
rm -f "$OUT/game/$EXE" && cp "$GAME/$EXE" "$OUT/game/$EXE" # a real copy, so patching it never touches the original

# System libraries, never certificates or host configuration. The rootfs is usrmerged like current
# distributions (/lib -> usr/lib), so a library has one path: FEX's thunks overlay /usr/lib/... paths
rm -rf "$OUT/x86root"; mkdir -p "$OUT/x86root/usr"
for f in $(grep -E '^/(usr/)?lib(64)?/' "$FILES") /lib64/ld-linux-x86-64.so.2; do
	[ -f "$f" ] || continue
	d=/usr${f#/usr}
	mkdir -p "$OUT/x86root$(dirname "$d")"
	cp -L "$f" "$OUT/x86root$d"
done
ln -s usr/lib "$OUT/x86root/lib"; ln -s usr/lib64 "$OUT/x86root/lib64"
# Data the libraries read: X keyboard maps, Vulkan drivers' manifests, Mesa's driver config
for f in $(grep -E '^/usr/share/(X11|vulkan|drirc.d|glvnd)/' "$FILES"); do
	[ -f "$f" ] || continue
	mkdir -p "$OUT/x86root$(dirname "$f")"
	cp -L "$f" "$OUT/x86root$f"
done
mkdir -p "$OUT/x86root/etc/ssl/certs" && printf 'nameserver 1.1.1.1\nnameserver 8.8.8.8\n' > "$OUT/x86root/etc/resolv.conf"

# TLS roots: the distribution's Mozilla set only, never CAs this machine added locally
cat /usr/share/ca-certificates/mozilla/*.crt > "$OUT/x86root/etc/ssl/certs/ca-certificates.crt"

du -sh "$OUT/game" "$OUT/x86root"
