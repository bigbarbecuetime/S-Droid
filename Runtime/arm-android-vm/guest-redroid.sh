#!/bin/sh
# Inside the arm64 VM: run arm64 Android 12 (Redroid) in Docker, reachable over adb on port 5555
# (127.0.0.1:5556 on the host that ran start-vm.sh).
#
#   sudo sh guest-redroid.sh
#
# The display is tiny and slow on purpose: under emulation, rendering the UI of the app under test
# otherwise takes the CPU the engine needs. Redroid applies no app seccomp filter, so a pass here
# doesn't cover Android's syscall filtering; real devices still need testing for that.
set -eu
apt-get install -y -qq "linux-modules-extra-$(uname -r)" docker.io > /dev/null
modprobe binder_linux devices=binder,hwbinder,vndbinder
# Docker Hub rate-limits anonymous pulls; Google's mirror serves the same image
IMAGE=${REDROID_IMAGE:-mirror.gcr.io/redroid/redroid:12.0.0_64only-latest}
docker pull -q "$IMAGE"
docker rm -f redroid > /dev/null 2>&1 || true
docker run -itd --privileged --name redroid -v /var/lib/redroid:/data -p 5555:5555 "$IMAGE" \
	androidboot.redroid_gpu_mode=guest androidboot.redroid_width=320 androidboot.redroid_height=240 \
	androidboot.redroid_dpi=120 androidboot.redroid_fps=5
echo "booting; on the host: adb connect 127.0.0.1:5556 && adb -s 127.0.0.1:5556 shell getprop sys.boot_completed"
