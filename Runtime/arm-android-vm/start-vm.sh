#!/bin/sh
# Boot an arm64 Ubuntu 24.04 VM under QEMU's software emulation (no arm64 host or KVM needed),
# as the place to run arm64 Android (Redroid) when there's no phone or Quest to test on.
#
#   start-vm.sh <work dir>
#
# First run downloads the cloud image and makes an SSH key; ssh to the guest with
#   ssh -i <work dir>/id_ed25519 -p 2222 ubuntu@127.0.0.1
# Guest port 5555 (Redroid's adb) is forwarded to 127.0.0.1:5556 here.
#
# CPU model: neoverse-n1. FEX crashes on QEMU's "max" model (its SVE2 emulation).
set -eu
W=$1; mkdir -p "$W"; cd "$W"

[ -f noble-arm64.img ] || curl -sSfo noble-arm64.img https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-arm64.img
[ -f os.qcow2 ] || qemu-img create -q -f qcow2 -F qcow2 -b noble-arm64.img os.qcow2 24G
[ -f id_ed25519 ] || ssh-keygen -q -t ed25519 -N "" -f id_ed25519
if [ ! -f seed.img ]; then
	cat > user-data <<EOC
#cloud-config
users:
  - name: ubuntu
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    ssh_authorized_keys:
      - $(cat id_ed25519.pub)
package_update: false
EOC
	cloud-localds seed.img user-data
fi

exec qemu-system-aarch64 -machine virt,gic-version=3 -accel tcg,thread=multi -cpu neoverse-n1 \
	-smp "${VM_CPUS:-4}" -m "${VM_MEMORY:-8192}" \
	-bios /usr/share/qemu-efi-aarch64/QEMU_EFI.fd \
	-drive if=virtio,file=os.qcow2,format=qcow2 -drive if=virtio,file=seed.img,format=raw \
	-netdev user,id=n0,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=tcp:127.0.0.1:5556-:5555 -device virtio-net-pci,netdev=n0 \
	-display none -serial file:serial.log
