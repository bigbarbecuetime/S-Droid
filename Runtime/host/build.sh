#!/bin/sh
# Build the runtime host APK with the SDK tools directly: javac, d8, aapt2, zipalign, apksigner.
set -e
cd "$(dirname "$0")"
SDK=${ANDROID_HOME:?set ANDROID_HOME}
BT=$SDK/build-tools/${BUILD_TOOLS:-35.0.0}
JAR=$SDK/platforms/${PLATFORM:-android-35}/android.jar
OUT=${OUT:-build}
rm -rf $OUT && mkdir -p $OUT/classes $OUT/dex

javac -source 11 -target 11 -classpath $JAR -d $OUT/classes $(find src -name '*.java')
$BT/d8 --min-api 26 --lib $JAR --output $OUT/dex $(find $OUT/classes -name '*.class')
$BT/aapt2 link -o $OUT/unsigned.apk -I $JAR --manifest AndroidManifest.xml
(cd $OUT/dex && zip -q ../unsigned.apk classes.dex)
$BT/zipalign -f -p 4 $OUT/unsigned.apk $OUT/aligned.apk

KS=${KEYSTORE:-$HOME/.android/debug.keystore}
[ -f "$KS" ] || keytool -genkeypair -keystore "$KS" -storepass android -alias androiddebugkey -keypass android \
	-keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Android Debug,O=Android,C=US"
$BT/apksigner sign --ks "$KS" --ks-pass pass:android --key-pass pass:android --out $OUT/sbox-runtime.apk $OUT/aligned.apk
echo "$OUT/sbox-runtime.apk"
