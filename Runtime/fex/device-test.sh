#!/bin/sh
# Run the s&box dedicated server on an arm64 Android device (phone or Quest) through FEX, inside
# the sandbox of FEXDroid (github.com/cobrabm12/fexdroid), whose debuggable release APK carries
# FEX and a glibc patched for Android's seccomp filter.
#
#   device-test.sh <fexdroid-legacy.apk> <payload dir from make-payload.sh> [engine args]
#
# Engine args default to loading the spacewars test game. ADB_SERIAL picks the device and
# SBOX_TIMEOUT (seconds, default 1200) bounds the run: emulated devices need far longer.
# SBOX_SCREEN_OFF=1 turns the screen off once the engine starts, so an emulator's software
# rendering of FEXDroid's UI stops competing with the engine (the app stays top, not cached).
set -eu
APK=$1; PAYLOAD=$2; shift 2
ARGS=${*:-+game unittest/addons/spacewars/.sbproj}
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
$ADB shell "run-as $PKG ls files/sbox/game/sbox-server" > /dev/null || { echo "payload didn't arrive"; exit 1; }

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

echo "== run inside the app sandbox: $ARGS"
# FEXDroid's developer intent runs a shell command with its Linux environment (FXD_ROOT, FXD_FILES,
# FEX config) after installing its own payload on first use; output goes to logcat tag fexdroid-linux
# CoreCLR under FEX needs both DOTNET_ switches: suspend threads at safe points instead of
# interrupting them with signals, and map JIT code once instead of double mapping it
CMD="cd \$FXD_FILES/sbox/game && FEX_ROOTFS=\$FXD_FILES/sbox/x86root HOME=\$FXD_FILES/sbox/home DOTNET_INTERNAL_ThreadSuspendInjection=0 DOTNET_EnableWriteXorExecute=0 timeout $TIMEOUT \$FXD_ROOT/usr/bin/FEX ./sbox-server $ARGS 2>&1"
$ADB logcat -c
$ADB shell am force-stop $PKG
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
