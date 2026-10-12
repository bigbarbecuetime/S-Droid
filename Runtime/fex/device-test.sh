#!/bin/sh
# Run the s&box dedicated server on an arm64 Android device (phone or Quest) through FEX, inside
# the sandbox of FEXDroid (github.com/cobrabm12/fexdroid), whose debuggable release APK carries
# FEX and a glibc patched for Android's seccomp filter.
#
#   device-test.sh <fexdroid-legacy.apk> <payload dir from make-payload.sh> [engine args]
#
# Engine args default to loading the spacewars test game. ADB_SERIAL picks the device.
set -eu
APK=$1; PAYLOAD=$2; shift 2
ARGS=${*:-+game unittest/addons/spacewars/.sbproj}
PKG=ro.cobrabm.fexdroid
ADB="adb ${ADB_SERIAL:+-s $ADB_SERIAL}"

echo "== device"
$ADB shell 'getprop ro.product.model; getprop ro.build.version.release; uname -m; getconf PAGESIZE'
[ "$($ADB shell getconf PAGESIZE | tr -d '\r')" = 4096 ] || { echo "FEX needs 4 KB pages"; exit 1; }

echo "== install FEXDroid"
$ADB install -r "$APK"

echo "== copy the payload into the app ($(du -sh "$PAYLOAD" | cut -f1))"
$ADB shell rm -rf /data/local/tmp/sbox-payload
$ADB push "$PAYLOAD" /data/local/tmp/sbox-payload > /dev/null
$ADB shell "run-as $PKG sh -c 'rm -rf files/sbox && mkdir -p files && cp -r /data/local/tmp/sbox-payload files/sbox && chmod -R u+rwX files/sbox'"
$ADB shell rm -rf /data/local/tmp/sbox-payload

echo "== run inside the app sandbox: $ARGS"
# FEXDroid's developer intent runs a shell command with its Linux environment (FXD_ROOT, FXD_FILES,
# FEX config) after installing its own payload on first use; output goes to logcat tag fexdroid-linux
# CoreCLR under FEX needs both DOTNET_ switches: suspend threads at safe points instead of
# interrupting them with signals, and map JIT code once instead of double mapping it
CMD="cd \$FXD_FILES/sbox/game && FEX_ROOTFS=\$FXD_FILES/sbox/x86root HOME=\$FXD_FILES/sbox/home DOTNET_INTERNAL_ThreadSuspendInjection=0 DOTNET_EnableWriteXorExecute=0 timeout 1200 \$FXD_ROOT/usr/bin/FEX ./sbox-server $ARGS 2>&1"
$ADB logcat -c
$ADB shell am force-stop $PKG
$ADB shell "am start -n $PKG/.MainActivity --es action cmd --es cmd '$CMD'" > /dev/null

for _ in $(seq 1 1300); do
	sleep 1
	$ADB logcat -d -s fexdroid-linux:I | grep -qE 'game.load (success|fail)|\[exit ' && break
done
$ADB logcat -d -s fexdroid-linux:I | sed 's/^.*fexdroid-linux: //' | grep -vE 'mount/|RenderQu|NullStaticRef|Failed to clean|^--------- beginning'
