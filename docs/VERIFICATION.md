# Verification status

## Verified in development

| Area | How |
|---|---|
| RDB structure | `rdbtool info/show` (amitools 0.8.1) accepts every checksum and field; we read rdbtool-created disks back. |
| FFS format | `xdftool` reads our volumes; its used-block count matches our bitmap exactly, including bitmap extension blocks. |
| FFS file engine | Two-way interop with `xdftool` (OFS + FFS, files needing extension blocks, nested folders, comments); a 300-file hash-chain test; delete returns every block; simulated crash leaves the bitmap flagged invalid. |
| FAT32 | Spec checks in tests (FAT size covers all clusters, backup boot sector, FSInfo, label); cross-checked with pyfatfs (read/write/list). |
| Diagnostics | Fixtures: Windows FAT32, exFAT, GPT, stale RDB behind MBR, superfloppy, RDB card in the data slot, PC card in the HDD slot, unformatted RDB partitions, random data. |
| Safety gate | Swapped card at the same path, removed card (no fall-back), system disk with permissive policy, internal bus, write-protect, wrong confirmation. |
| Imaging | Raw and gzip round trip, damaged image refused before writing, image larger than target, fake-capacity card detected, interrupted write reported. |
| Windows app | Built with the Windows .NET 10 SDK with zero warnings; launched on Windows 11; disk enumeration correctly protects the NVMe system disk and a virtual disk; every page loads. |
| Packaging | `build/publish.ps1` produces the self-contained portable app and the CLI zip. The portable exe starts, and the Windows CLI creates and verifies images. |

## Verified on hardware

| Date | Test | Result |
|---|---|---|
| 2026-10-28 | Physical card prepared with the A6000 *File Transfer* configuration (MBR + FAT32, label SD0) on Windows 11, then mounted on an Apollo A6000 via SD Card > Mount | Mounted as SD0; files read and written on the Amiga. Read-back verification passed on Windows. |
| 2026-10-28 | Large folder copied from a mapped network drive (R:) to that card with the app's Files page (FAT32 path), then used on the A6000 | Worked without errors; files copied on the Amiga locally. |

## Not yet verified (needs hardware or a human)

1. **Writing a physical card** with Restore, or Amiga Files on an FFS (hard-drive) partition with "Allow changes". Prepare and FAT32 file copying have been exercised on real hardware; see above.
2. **Backup of a physical card** (raw read through `RawDiskDevice`).
3. **Lock/dismount behaviour** with Explorer windows open on the card.
4. **A6000:** which slot the FAT32 card was tested in; an RDB card in each slot (auto-mount? boot?). See the open questions in A6000-RESEARCH.md.
5. **A1200/A600 with an IDE-to-SD adapter:** boot from DH0 after installing Workbench; the KS 3.1 4 GB rule; MaxTransfer with common adapters.
6. **WinUAE:** mount a generated `.hdf` as a hardfile (RDB mode) and confirm partitions mount and validate.
7. **Installer:** `build/installer.iss` has not been compiled (no Inno Setup on the build machine).
8. **Device-change watcher:** card insert/remove refreshes the list (`Win32_DeviceChangeEvent`).
