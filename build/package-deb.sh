#!/usr/bin/env bash
# Builds Debian packages of the Linux app (Avalonia) and the asdc command-line tool.
#
#   build/package-deb.sh                 # amd64 and arm64 (Raspberry Pi OS 64-bit)
#   build/package-deb.sh amd64           # one architecture
#   SKIP_TESTS=1 build/package-deb.sh    # skip the test run
#
# Output: artifacts/amiga-sd-creator_<version>_<arch>.deb
# Needs: .NET 10 SDK, dpkg-deb. The app is published self-contained, so the target system needs
# no .NET runtime.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)"
archs=("${@:-amd64 arm64}")
read -r -a archs <<< "${archs[*]}"
artifacts="$root/artifacts"
mkdir -p "$artifacts"

if [[ "${SKIP_TESTS:-0}" != "1" ]]; then
    dotnet test tests/AmigaSdCreator.Core.Tests -c Release --nologo
fi

for arch in "${archs[@]}"; do
    case "$arch" in
        amd64) rid=linux-x64 ;;
        arm64) rid=linux-arm64 ;;
        *) echo "Unsupported architecture: $arch" >&2; exit 1 ;;
    esac

    stage="$(mktemp -d)"
    trap 'rm -rf "$stage"' EXIT
    lib="$stage/usr/lib/amiga-sd-creator"

    # Both programs go into one folder and share a single copy of the .NET runtime.
    dotnet publish src/AmigaSdCreator.Desktop -c Release -r "$rid" --self-contained true \
        -p:DebugType=none -o "$lib" --nologo
    dotnet publish src/AmigaSdCreator.Cli -c Release -r "$rid" --self-contained true \
        -p:DebugType=none -p:InvariantGlobalization=true -o "$lib" --nologo
    rm -f "$lib"/*.pdb "$lib"/*.dbg

    install -d "$stage/usr/bin"
    ln -s ../lib/amiga-sd-creator/amiga-sd-creator "$stage/usr/bin/amiga-sd-creator"
    ln -s ../lib/amiga-sd-creator/asdc "$stage/usr/bin/asdc"
    install -Dm644 build/linux/amiga-sd-creator.desktop "$stage/usr/share/applications/amiga-sd-creator.desktop"
    install -Dm644 build/linux/au.com.techgurus.amigasdcreator.policy "$stage/usr/share/polkit-1/actions/au.com.techgurus.amigasdcreator.policy"
    install -Dm644 src/AmigaSdCreator.App/Assets/logo.png "$stage/usr/share/icons/hicolor/512x512/apps/amiga-sd-creator.png"
    install -Dm644 README.md "$stage/usr/share/doc/amiga-sd-creator/README.md"
    if [[ -f LICENSE ]]; then install -Dm644 LICENSE "$stage/usr/share/doc/amiga-sd-creator/copyright"; fi

    size_kb="$(du -sk "$stage/usr" | cut -f1)"
    install -d "$stage/DEBIAN"
    cat > "$stage/DEBIAN/control" <<CONTROL
Package: amiga-sd-creator
Version: $version
Section: otherosfs
Priority: optional
Architecture: $arch
Installed-Size: $size_kb
Depends: libc6 (>= 2.31), util-linux (>= 2.33), mount, libfontconfig1, libx11-6, libice6, libsm6, libgcc-s1, libstdc++6, zlib1g
Recommends: pkexec | policykit-1, xdg-utils
Maintainer: Amiga SD Creator contributors <development@techgurus.com.au>
Homepage: https://github.com/freemchr/AmigaSDFormatter
Description: Prepare, diagnose, back up and fill SD cards for Amiga computers
 Amiga SD Creator lays out SD/CF cards and USB sticks for Amiga computers
 without HDToolBox: FAT32 transfer cards (Apollo A6000, PCMCIA, Gotek),
 Amiga hard-drive cards with a Rigid Disk Block and FFS partitions, and
 PiStorm (Emu68) cards. It can also diagnose a card read-only, copy files
 to and from FFS partitions, and back up or restore whole cards.
 .
 Includes the asdc command-line tool for creating and inspecting image files.
CONTROL
    cat > "$stage/DEBIAN/postinst" <<'POSTINST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
    command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
POSTINST
    cp "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm"
    sed -i 's/= "configure"/= "remove"/' "$stage/DEBIAN/postrm"
    chmod 755 "$stage/DEBIAN/postinst" "$stage/DEBIAN/postrm" "$lib/amiga-sd-creator" "$lib/asdc"

    out="$artifacts/amiga-sd-creator_${version}_${arch}.deb"
    dpkg-deb --root-owner-group --build "$stage" "$out"
    rm -rf "$stage"
    trap - EXIT
    echo "Built $out"
done
