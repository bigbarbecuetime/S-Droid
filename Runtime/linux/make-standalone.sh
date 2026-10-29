#!/bin/sh
# Put a standalone (exported) s&box game together on Linux without the editor: what the editor's
# exporter writes, built from a game folder and a project with the dedicated server's compiler.
#
#   make-standalone.sh <game dir> <project dir, relative to game dir> <output dir>
#
# Needs an engine with engine-patches/ applied (0001 reads standalone.json, 0004 writes the compiled
# assemblies out) and python3. The output links to the game folder's files rather than copying them.
# Run the game from the output dir with:
#   DOTNET_ROOT=$PWD/bin/dotnet ./<ident> -windowed -vulkan_allow_cpu
set -eu
GAME=$(realpath "$1"); PROJECT=${2%/}; OUT=$3
[ -f "$GAME/$PROJECT/.sbproj" ] || { echo "no $PROJECT/.sbproj in $GAME"; exit 1; }
BIN=$(mktemp -d); trap 'rm -rf "$BIN"' EXIT

echo "== compile $PROJECT"
# The server compiles the project while loading it; stop it once the game has loaded
LOG=$(mktemp -p "$BIN")
( cd "$GAME" && SBOX_DUMP_ASSEMBLIES="$BIN/dll" DOTNET_ROOT="$GAME/bin/dotnet" exec timeout 600 ./sbox-server +game "$PROJECT/.sbproj" ) > "$LOG" 2>&1 &
SERVER=$!
until grep -qE 'game.load (success|fail)' "$LOG" || ! kill -0 $SERVER 2> /dev/null; do sleep 2; done
kill $SERVER 2> /dev/null || true; wait $SERVER 2> /dev/null || true
ls "$BIN"/dll/*.dll > /dev/null 2>&1 || { grep -E 'error CS|game.load' "$LOG"; echo "no assemblies written: is engine-patches/0004 applied?"; exit 1; }

echo "== engine"
# Everything but other games' content, logs and a previous standalone build's files
mkdir -p "$OUT"
for f in "$GAME"/* "$GAME"/.[!.]*; do
	[ -e "$f" ] || continue
	case "${f##*/}" in addons|samples|logs|assets|standalone.json|.source2) continue ;; esac
	cp -al "$f" "$OUT/" 2> /dev/null || cp -a "$f" "$OUT/"
done

echo "== game"
# assets/ is the project's Assets folder, its settings and localization, and the compiled code in .bin
rm -rf "$OUT/assets"; mkdir -p "$OUT/assets/.bin"
cp -r "$GAME/$PROJECT/Assets/." "$OUT/assets/"
for d in ProjectSettings Localization; do [ -d "$GAME/$PROJECT/$d" ] && cp -r "$GAME/$PROJECT/$d" "$OUT/assets/"; done
cp "$BIN"/dll/*.dll "$OUT/assets/.bin/"

# standalone.json: what the exporter embeds in a Windows executable's resources, and the launcher,
# which is the standalone apphost pointed at bin/managed (as StandaloneExporter.SetAppHostAssemblyPath does)
EXE=$(python3 - "$GAME/$PROJECT/.sbproj" "$OUT" "$GAME/bin/managed/sbox-standalone" <<'EOF'
import json, sys, datetime
sbproj, out, apphost = sys.argv[1:]
project = json.load(open(sbproj, encoding="utf-8-sig"))
ident = f"{project['Org']}.{project['Ident']}"
exe = project["Ident"].lower()
manifest = {"Name": project.get("Title") or exe, "Ident": ident, "ExecutableName": exe, "AppId": 480,
	"BuildDate": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"), "IsVRProject": False}
json.dump({"Manifest": manifest, "Project": project}, open(f"{out}/standalone.json", "w"), indent=2)

image = bytearray(open(apphost, "rb").read())
old, new = b"sbox-standalone.dll\0", b"bin/managed/sbox-standalone.dll"
at = image.find(old)
if at < 0: sys.exit("the apphost doesn't start sbox-standalone.dll")
image[at:at + 1024] = new + bytes(1024 - len(new))  # the apphost's path buffer holds 1024 bytes
open(f"{out}/{exe}", "wb").write(image)
print(exe)
EOF
)
chmod +x "$OUT/$EXE"
echo "== $OUT/$EXE"
