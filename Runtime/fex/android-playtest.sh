#!/bin/sh
# Play a standalone game that device-test.sh started in FEXDroid, through Android's own input: drag
# across the display (FEXDroid moves the X pointer: mouse look) and press W (FEXDroid sends X key
# events: walk). The game must carry probe/InputProbe.cs (make-standalone.sh EXTRA_CODE), whose log
# lines say where the player cube is.
#
#   android-playtest.sh <out dir> [seconds to wait for each response, default 600]
#
# Passes when the cube turned after the drag and moved after W, and FEXDroid's display bridge put
# new frames on the Android surface meanwhile. XWD=<arm64 xwd binary> also dumps the X screen
# before and after into <out dir> (x-0.png, x-1.png) and requires the picture to change: an
# emulator's screencap may not show the app's SurfaceView at all (Redroid's doesn't).
set -eu
OUT=$1; WAIT=${2:-600}
PKG=ro.cobrabm.fexdroid
ADB="adb ${ADB_SERIAL:+-s $ADB_SERIAL}"
mkdir -p "$OUT"

probe() { $ADB shell "run-as $PKG cat files/sbox/game/logs/FEX.log" | grep -a 'probe frames=' | tail -1; }
field() { echo "$2" | grep -oE "$1=[^ ]+" | cut -d= -f2 | tr -d '\r\t'; }
# Wait until the probe's field differs from the value given; prints the latest probe line
changed() { for _ in $(seq 1 $(( WAIT / 5 ))); do L=$(probe); [ "$(field "$1" "$L")" != "$2" ] && break; sleep 5; done; echo "$L"; }
xshot() {
	[ -n "${XWD:-}" ] || return 0
	R=/data/user/0/$PKG/files/rootfs
	$ADB exec-out "run-as $PKG sh -c 'DISPLAY=:0 $R/usr/lib/aarch64-linux-gnu/ld-linux-aarch64.so.1 --library-path $R/usr/lib/aarch64-linux-gnu /data/local/tmp/xwd -root -silent'" > "$OUT/x-$1.xwd"
	convert "xwd:$OUT/x-$1.xwd" "$OUT/x-$1.png" && rm "$OUT/x-$1.xwd"
}
[ -z "${XWD:-}" ] || { $ADB push -q "$XWD" /data/local/tmp/xwd && $ADB shell chmod 755 /data/local/tmp/xwd; }

P0=$(probe); [ -n "$P0" ] || { echo "FAIL: no probe lines (is the game running, with InputProbe.cs?)"; exit 1; }
$ADB logcat -c
xshot 0
echo "start: pos=$(field pos "$P0") yaw=$(field yaw "$P0")"

# The display is the SurfaceView at the top of FEXDroid's screen; drag along it, slowly
BOUNDS=$($ADB shell 'uiautomator dump /sdcard/fxd-ui.xml > /dev/null && cat /sdcard/fxd-ui.xml' \
	| grep -oE 'class="android.view.SurfaceView"[^>]*bounds="[^"]*"' | grep -oE '[0-9]+' | tail -4 | tr '\n' ' ')
set -- ${BOUNDS:-6 19 314 143} # where FEXDroid draws it on a 320x240 screen
Y=$(( ($2 + $4) / 2 )); X0=$(( $1 + ($3 - $1) / 4 )); X1=$(( $1 + ($3 - $1) * 3 / 4 ))
$ADB shell input swipe $X0 $Y $X1 $Y 4000
P1=$(changed yaw "$(field yaw "$P0")")
echo "after drag: pos=$(field pos "$P1") yaw=$(field yaw "$P1")"

# W: long presses, repeated, so frames see it down (input waits behind slow frames, so does this)
for _ in 1 2 3 4 5; do $ADB shell input keyevent --longpress KEYCODE_W; done
P2=$(changed pos "$(field pos "$P1")")
echo "after W: pos=$(field pos "$P2") yaw=$(field yaw "$P2")"
xshot 1

TURNED=$([ "$(field yaw "$P1")" != "$(field yaw "$P0")" ] && echo yes || echo no)
MOVED=$([ "$(field pos "$P2")" != "$(field pos "$P1")" ] && echo yes || echo no)
# The bridge logs how many of the last 600 frames it looked at had new X content to put on screen
SHOWN=$($ADB logcat -d -s fexdroid-display:I | grep -oE '[0-9]+ had new content' | awk '{ n += $1 } END { print n + 0 }')
PICTURE=yes
if [ -n "${XWD:-}" ]; then
	CHANGE=$(compare -metric RMSE "$OUT/x-0.png" "$OUT/x-1.png" null: 2>&1 | grep -oE '\(([0-9.e-]+)\)' | tr -d '()' || true)
	python3 -c 'import sys; sys.exit(0 if float(sys.argv[1]) > 0.01 else 1)' "${CHANGE:-0}" || PICTURE=no
	echo "X picture change: $CHANGE"
fi
echo "turned: $TURNED, moved: $MOVED, new frames the bridge put on screen: $SHOWN, picture changed: $PICTURE"
[ "$TURNED" = yes ] && [ "$MOVED" = yes ] && [ "$SHOWN" -gt 0 ] && [ "$PICTURE" = yes ] && echo PASS || { echo FAIL; exit 1; }
