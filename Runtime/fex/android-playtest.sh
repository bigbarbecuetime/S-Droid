#!/bin/sh
# Play a standalone game that device-test.sh started in FEXDroid, through Android's own input: drag
# across the display (FEXDroid moves the X pointer: mouse look) and press W (FEXDroid sends X key
# events: walk). The game must carry probe/InputProbe.cs (make-standalone.sh EXTRA_CODE), whose log
# lines say where the player cube is; screenshots land in <out dir>.
#
#   android-playtest.sh <out dir> [seconds to wait for each step, default 120]
#
# Passes when the cube turned after the drag, moved after W, and the screen changed.
set -eu
OUT=$1; WAIT=${2:-120}
PKG=ro.cobrabm.fexdroid
ADB="adb ${ADB_SERIAL:+-s $ADB_SERIAL}"
EXE=${SBOX_EXE:-sweeper}
mkdir -p "$OUT"

probe() { $ADB shell "run-as $PKG cat files/sbox/game/logs/$EXE.log" | grep -a 'probe frames=' | tail -1; }
field() { echo "$2" | grep -oE "$1=[^ ]+" | cut -d= -f2 | tr -d '\r\t'; }
shot() { $ADB exec-out screencap -p > "$OUT/$1.png"; }
# Wait for the probe to report a frame newer than the line given
next() { for _ in $(seq 1 $(( WAIT / 5 ))); do sleep 5; L=$(probe); [ "$L" != "$1" ] && { echo "$L"; return; }; done; echo "$1"; }

P0=$(probe); [ -n "$P0" ] || { echo "FAIL: no probe lines (is the game running, with InputProbe.cs?)"; exit 1; }
P0=$(next "$P0") # a fresh frame, so the baseline isn't from before the screen settled
shot 0
echo "start: pos=$(field pos "$P0") yaw=$(field yaw "$P0")"

# The display is the SurfaceView at the top of FEXDroid's screen; drag along it, slowly
BOUNDS=$($ADB shell 'uiautomator dump /sdcard/fxd-ui.xml > /dev/null && cat /sdcard/fxd-ui.xml' \
	| grep -oE 'class="android.view.SurfaceView"[^>]*bounds="[^"]*"' | grep -oE '[0-9]+' | tail -4 | tr '\n' ' ')
set -- ${BOUNDS:-0 30 320 140}
Y=$(( ($2 + $4) / 2 )); X0=$(( $1 + ($3 - $1) / 4 )); X1=$(( $1 + ($3 - $1) * 3 / 4 ))
$ADB shell input swipe $X0 $Y $X1 $Y 4000
P1=$(next "$P0")
echo "after drag: pos=$(field pos "$P1") yaw=$(field yaw "$P1")"

# W: a long press, repeated, so at least one frame sees it down
for _ in 1 2 3 4 5; do $ADB shell input keyevent --longpress KEYCODE_W; done
P2=$(next "$P1"); P2=$(next "$P2")
shot 1
echo "after W: pos=$(field pos "$P2") yaw=$(field yaw "$P2")"

TURNED=$([ "$(field yaw "$P1")" != "$(field yaw "$P0")" ] && echo yes || echo no)
MOVED=$([ "$(field pos "$P2")" != "$(field pos "$P1")" ] && echo yes || echo no)
CHANGED=$(compare -metric AE "$OUT/0.png" "$OUT/1.png" null: 2>&1 || true)
echo "turned: $TURNED, moved: $MOVED, pixels changed: $CHANGED"
[ "$TURNED" = yes ] && [ "$MOVED" = yes ] && [ "${CHANGED%% *}" != 0 ] && echo PASS || { echo FAIL; exit 1; }
