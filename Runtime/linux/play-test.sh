#!/bin/sh
# Boot a standalone game (from make-standalone.sh) on a virtual X display with CPU Vulkan (lavapipe),
# then play it: hold a key and check the picture moves while it didn't when idle.
#
#   play-test.sh <standalone dir> [key, default w] [seconds to hold, default 4]
#
# Needs Xvfb, openbox (SDL only takes keyboard focus under a window manager), xdotool, ImageMagick
# and Mesa's lavapipe. Screenshots land in <standalone dir>/playtest-*.png.
set -eu
DIR=$(realpath "$1"); KEY=${2:-w}; HOLD=${3:-4}
EXE=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Manifest"]["ExecutableName"])' "$DIR/standalone.json")
DISPLAY_NUM=:${PLAYTEST_DISPLAY:-97}
LOG="$DIR/logs/$EXE.log"
SHOT="$DIR/playtest"
export DISPLAY=$DISPLAY_NUM

Xvfb $DISPLAY_NUM -screen 0 1280x720x24 -nolisten tcp > /dev/null 2>&1 & XVFB=$!
sleep 2
openbox > /dev/null 2>&1 & WM=$!
XDG_RUNTIME_DIR=$(mktemp -d); export XDG_RUNTIME_DIR
rm -f "$LOG"
( cd "$DIR" && DOTNET_ROOT="$DIR/bin/dotnet" VK_ICD_FILENAMES=/usr/share/vulkan/icd.d/lvp_icd.json SDL_VIDEO_DRIVER=x11 \
	exec ./"$EXE" -windowed -w 1280 -h 720 -vulkan_allow_cpu ) > "$DIR/playtest-stdout.log" 2>&1 & GAME=$!
trap 'kill $GAME 2> /dev/null; sleep 2; kill $WM $XVFB 2> /dev/null; rm -rf "$XDG_RUNTIME_DIR"' EXIT

echo "== boot $EXE"
until grep -qE 'game.load (success|fail)' "$LOG" 2> /dev/null; do
	kill -0 $GAME 2> /dev/null || { tail -20 "$DIR/playtest-stdout.log"; echo "FAIL: the game exited"; exit 1; }
	sleep 5
done
grep -oE 'game.load .*' "$LOG"
grep -q 'game.load success' "$LOG" || { echo "FAIL: game didn't load"; exit 1; }
grep -E 'Missing Component|Error loading resource file "scenes' "$LOG" && { echo "FAIL: the scene is incomplete"; exit 1; }
sleep 20 # let shaders and streaming settle

WIN=$(xdotool search --name "$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Manifest"]["Name"])' "$DIR/standalone.json")" | head -1)
xdotool windowactivate --sync "$WIN" 2> /dev/null || true
xdotool mousemove 640 360 click 1
sleep 3

diff() { compare -metric RMSE "$1" "$2" null: 2>&1 | grep -oE '\(([0-9.e-]+)\)' | tr -d '()'; }
import -window root "$SHOT-0.png"; sleep 3
import -window root "$SHOT-1.png"
xdotool keydown "$KEY"; sleep "$HOLD"; xdotool keyup "$KEY"; sleep 3
import -window root "$SHOT-2.png"

LIT=$(convert "$SHOT-1.png" -format '%[fx:maxima]' info:)
IDLE=$(diff "$SHOT-0.png" "$SHOT-1.png"); MOVED=$(diff "$SHOT-1.png" "$SHOT-2.png")
echo "brightest pixel $LIT, idle change $IDLE, change after holding $KEY $MOVED"
python3 -c 'import sys; lit, idle, moved = map(float, sys.argv[1:]); sys.exit(0 if lit > 0 and moved > max(0.01, 4 * idle) else 1)' "$LIT" "$IDLE" "$MOVED" \
	&& echo "PASS: rendered, and the view moved with input" || { echo "FAIL: no rendering or no response to input"; exit 1; }
