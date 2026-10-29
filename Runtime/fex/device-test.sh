#!/bin/sh
# Run s&box on an arm64 Android device (phone or Quest) through FEX, inside the sandbox of FEXDroid
# (github.com/cobrabm12/fexdroid), whose debuggable release APK carries FEX, a glibc patched for
# Android's seccomp filter, an X server shown in the app and Vulkan (Turnip, or lavapipe) for x86
# programs through FEX's thunks.
#
#   device-test.sh <fexdroid-legacy.apk> <payload dir from make-payload.sh> [engine args]
#
# A dedicated server payload loads the spacewars test game by default. A standalone payload
# (make-payload.sh --standalone) opens its game in FEXDroid's X display, shown in the app.
# ADB_SERIAL picks the device and SBOX_TIMEOUT (seconds, default 1200) bounds the run: emulated
# devices need far longer. SBOX_SCREEN_OFF=1 (server only) turns the screen off once the engine
# starts, so an emulator's software rendering of FEXDroid's UI stops competing with the engine.
# The script returns once the game has loaded; a standalone game keeps running for SBOX_TIMEOUT.
set -eu
APK=$1; PAYLOAD=$2; shift 2
if [ -f "$PAYLOAD/game/standalone.json" ]; then
	EXE=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["Manifest"]["ExecutableName"])' "$PAYLOAD/game/standalone.json")
	ARGS=${*:--windowed -w 640 -h 360 -vulkan_allow_cpu} # lavapipe is a CPU device, which the engine skips without the flag
else
	EXE=sbox-server; ARGS=${*:-+game unittest/addons/spacewars/.sbproj}
fi
PKG=ro.cobrabm.fexdroid
ADB="adb ${ADB_SERIAL:+-s $ADB_SERIAL}"
TIMEOUT=${SBOX_TIMEOUT:-1200}

echo "== device"
$ADB shell 'getprop ro.product.model; getprop ro.build.version.release; uname -m; getconf PAGESIZE'
[ "$($ADB shell getconf PAGESIZE | tr -d '\r')" = 4096 ] || { echo "FEX needs 4 KB pages"; exit 1; }

echo "== install FEXDroid"
$ADB install -r "$APK"

echo "== stream the payload into the app ($(du -sh "$PAYLOAD" | cut -f1))"
# Straight into the app's files with tar over adb exec-in: no staging copy in /data/local/tmp, which
# would need twice the payload's size on the device.
$ADB shell "run-as $PKG sh -c 'rm -rf files/sbox && mkdir -p files/sbox'"
tar -C "$PAYLOAD" -cf - . | $ADB exec-in run-as $PKG sh -c 'tar -xf - -C files/sbox && chmod -R u+rwX files/sbox'
$ADB shell "run-as $PKG ls files/sbox/game/$EXE" > /dev/null || { echo "payload didn't arrive"; exit 1; }

echo "== FEXDroid's own rootfs"
# Its developer intent does nothing until FEXDroid has unpacked its payload (FEX, patched glibc,
# Mesa) from the APK, which only its "Reinstall the rootfs" button starts. Press it if needed.
if ! $ADB shell "run-as $PKG ls files/.payload.version" > /dev/null 2>&1; then
	$ADB shell am force-stop $PKG
	$ADB shell "am start -n $PKG/.MainActivity --es action cmd --es cmd true" > /dev/null
	sleep 20
	BOUNDS=$($ADB shell 'uiautomator dump /sdcard/fexdroid-ui.xml > /dev/null && cat /sdcard/fexdroid-ui.xml' \
		| grep -oE 'text="Reinstall the rootfs"[^>]*bounds="[^"]*"' | grep -oE '[0-9]+' | tail -4 | tr '\n' ' ')
	[ -n "$BOUNDS" ] || { echo "can't find FEXDroid's 'Reinstall the rootfs' button: press it in the app, then rerun"; exit 1; }
	set -- $BOUNDS
	$ADB shell input tap $(( ($1 + $3) / 2 )) $(( ($2 + $4) / 2 ))
	echo "installing (about 700 MB; minutes on a phone, an hour or more emulated)"
	until $ADB shell "run-as $PKG ls files/.payload.version" > /dev/null 2>&1; do sleep 15; done
fi

echo "== run inside the app sandbox: $EXE $ARGS"
# FEXDroid's developer intent runs a shell command with its Linux environment (FXD_ROOT, FXD_FILES,
# FEX config, Vulkan driver) after installing its own payload on first use; output goes to logcat
# tag fexdroid-linux. CoreCLR under FEX needs both DOTNET_ switches: suspend threads at safe points
# instead of interrupting them with signals, and map JIT code once instead of double mapping it
ENV="FEX_ROOTFS=\$FXD_FILES/sbox/x86root HOME=\$FXD_FILES/sbox/home DOTNET_INTERNAL_ThreadSuspendInjection=0 DOTNET_EnableWriteXorExecute=0"
$ADB logcat -c
$ADB shell am force-stop $PKG
if [ "$EXE" = sbox-server ]; then
	CMD="cd \$FXD_FILES/sbox/game && $ENV timeout $TIMEOUT \$FXD_ROOT/usr/bin/FEX ./sbox-server $ARGS 2>&1"
	$ADB shell "am start -n $PKG/.MainActivity --es action cmd --es cmd '$CMD'" > /dev/null
	if [ "${SBOX_SCREEN_OFF:-0}" = 1 ]; then
		sleep 30
		$ADB shell input keyevent SLEEP
	fi
	for _ in $(seq 1 $(( TIMEOUT / 5 + 60 ))); do
		sleep 5
		$ADB logcat -d -s fexdroid-linux:I | grep -qE 'game.load (success|fail)|\[exit ' && break
	done
	$ADB logcat -d -s fexdroid-linux:I | sed 's/^.*fexdroid-linux: //' | grep -vE 'mount/|RenderQu|NullStaticRef|Failed to clean|^--------- beginning'
	exit 0
fi

# A standalone game needs FEXDroid's X server, which runs while its display screen is open. The
# command (which outlives the screen it was started from) waits for the server's socket, then the
# display screen opens and starts it. Output goes to files/sbox/run.log, the engine's own log to
# files/sbox/game/logs/$EXE.log.
CMD="until grep -qa @/tmp/.X11-unix/X0 /proc/net/unix; do sleep 2; done; cd \$FXD_FILES/sbox/game && DISPLAY=:0 DOTNET_ROOT=\$PWD/bin/dotnet $ENV timeout $TIMEOUT \$FXD_ROOT/usr/bin/FEX ./$EXE $ARGS > ../run.log 2>&1; echo [exit \$?] >> ../run.log"
$ADB shell "run-as $PKG rm -f files/sbox/run.log files/sbox/game/logs/$EXE.log"
$ADB shell "am start -f 0x10008000 -n $PKG/.MainActivity --es action cmd --es cmd '$CMD'" > /dev/null
# A cold start takes a while; a second intent before the command runs replaces it. Then open the
# display until its X server is up
until $ADB logcat -d -s fexdroid-linux:I | grep -q 'X11-unix/X0'; do sleep 5; done
until $ADB shell 'ps -A' | grep -q ' Xvfb$'; do
	$ADB shell "am start -f 0x10008000 -n $PKG/.MainActivity --es action x" > /dev/null
	sleep 30
done

LOG="run-as $PKG cat files/sbox/game/logs/$EXE.log"
for _ in $(seq 1 $(( TIMEOUT / 15 + 20 ))); do
	sleep 15
	$ADB shell "$LOG 2> /dev/null; run-as $PKG cat files/sbox/run.log 2> /dev/null" | grep -qE 'game.load (success|fail)|\[exit ' && break
done
$ADB shell "run-as $PKG cat files/sbox/run.log" | tail -20
$ADB shell "$LOG" | grep -E 'game.load|Vulkan|Missing|rror' | head -20
