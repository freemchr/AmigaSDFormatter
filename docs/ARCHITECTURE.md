# Architecture

## Projects

| Project | Target | Responsibility |
|---|---|---|
| `AmigaSdCreator.Core` | net10.0 (cross-platform) | Every format and rule: block devices, MBR/GPT/RDB, FAT32 and OFS/FFS (format, inspect, read/write files), hardware profiles, layout planning/validation, image building and verification, diagnostics, imaging, the safety gate. No UI or OS code. |
| `AmigaSdCreator.Windows` | net10.0-windows | Disk discovery (Storage WMI `MSFT_Disk`/`MSFT_Partition`), raw `\\.\PhysicalDriveN` I/O, volume lock/dismount, device-change events, elevation. Implements `IDiskService`. |
| `AmigaSdCreator.Ui` | net10.0 | View models (CommunityToolkit.Mvvm) and app services shared by both front ends. Talks to the OS and UI toolkit only through `IPlatform` (disks, dialogs, UI thread, elevation, mounts, theme). |
| `AmigaSdCreator.App` | WPF (Windows) | Windows front end: XAML views, converters, `WpfPlatform`. |
| `AmigaSdCreator.Linux` | net10.0 (Linux) | Disk discovery (`lsblk` + `/proc/self/mountinfo` + `/proc/swaps`), raw `/dev` I/O (`pread`/`pwrite`, `O_EXCL` for writes, `BLKGETSIZE64`, `BLKRRPART`), unmounting, pkexec elevation, device-change watching. Implements `IDiskService`. |
| `AmigaSdCreator.Desktop` | Avalonia 11 (Linux) | Linux front end: AXAML views mirroring the WPF ones, `AvaloniaPlatform`. Packaged as a `.deb` by `build/package-deb.sh`. |
| `AmigaSdCreator.Cli` | net10.0 | `asdc`: create, inspect and verify **image files**. Never opens physical disks. |
| `AmigaSdCreator.Core.Tests` | xUnit | Unit, fixture and amitools interop tests. |

## Core namespaces

```
IO            IBlockDevice; FileBlockDevice, MemoryBlockDevice, SubBlockDevice (partition window)
Partitioning  Mbr, Gpt (read-only), Rdb (RDSK/PART/FSHD/LSEG model), RdbReader, RdbWriter, DosType, HunkFile
FileSystems   FilesystemProbe, FatBootSector, Fat32Formatter, AmigaFfsFormatter, AmigaFfsInspector, AmigaFfsVolume
Profiles      profiles.json (embedded) + user overrides -> HardwareProfile / StorageInterfaceProfile / CompatibilityMode
Layout        LayoutPlanner (recommend) -> LayoutPlan -> LayoutValidator -> DiskImageBuilder -> LayoutVerifier
Diagnostics   DiskInspector (read-only) -> DiagnosticEngine (+profile) -> DiagnosticReport -> ReportExporter (md/json)
Imaging       DiskImager: backup (raw/gzip + manifest), inspect (hash), restore (+ read-back verify)
Safety        DiskDescriptor, DeviceIdentity, WriteGuard (all destructive checks), CardOperations (orchestration)
```

## Data flow for "Prepare"

```
select card ──► DeviceIdentity captured
choose Amiga + tile ──► LayoutPlanner.Recommend(profile, interface, use, size, kickstart mode)
                        └► LayoutValidator (errors block; profile caps from Kickstart modes are errors,
                                            unverified interface caps are warnings)
Prepare ──► DiskInspector on the card (shown to the user) ──► type ERASE
        ──► CardOperations.PrepareAsync
              ├ re-enumerate disks, WriteGuard.Evaluate(current, selected identity, ...)
              ├ IDiskService.OpenForWrite: re-checks identity, locks/dismounts volumes, compares size
              ├ DiskImageBuilder.Apply (re-validates; clears first/last MiB; writes)
              └ LayoutVerifier.Verify (re-reads everything and compares with the plan)
```

The same `DiskImageBuilder` and `LayoutVerifier` produce image files, so an image and a card come out byte-identical apart from random IDs.

## Key design decisions

- **Own formatters, not Windows `format`.** FAT32 is written per Microsoft's fatgen103, using the fat32format/Rufus cluster table. Amiga partitions are never touched by a Windows FAT API.
- **Profiles are data.** `profiles.json` gives every interface a `status` (Verified / Partial / Experimental / Planned / Protected), sourced `facts`, and an explicit `unverified` list. Users can drop corrected profiles into `%LOCALAPPDATA%\AmigaSdCreator\Profiles`. The home screen's tiles are generated from profiles, so adding an Amiga needs no code.
- **Protected interfaces** (the A6000 CompactFlash system drive) only allow backup and authorised-image restore.
- **FFS writes follow AmigaDOS crash semantics.** The root's bitmap-valid flag is cleared before the first change and set only after the bitmap is flushed, so an interrupted session makes the Amiga revalidate the volume instead of trusting a stale bitmap.
- **Timestamps.** AmigaDOS stamps are local wall-clock time; they're converted as such.
- **Geometry.** 16 heads × 63 sectors (504 KiB cylinders), which also guarantees even block counts. That matters because AmigaDOS computes the root block as `(blocks + 1) / 2` and amitools as `blocks / 2`, which agree only for even counts.

## Phase status

| Phase | Status |
|---|---|
| 1. Foundation: WPF/MVVM, disk detection, read-only inspection, A6000 profile, diagnostic report, tests | Done |
| 2. Image preparation: MBR+FAT32, RDB+FFS, validation, amitools-verified fixtures | Done |
| 3. Physical writing: identity checks, elevation, lock/dismount, write verification, backup/restore | Implemented; **not yet exercised on a physical card** (see VERIFICATION.md) |
| 4. OS installation | Partial: authorised image restore, and copying files onto FFS partitions (Amiga Files). Not done: guided Workbench install from ADF/install media, `.info`/protection-bit metadata via WinUAE `.uaem` sidecars, and boot-block installation. |
| 5. More hardware | A500, A600, A2000, A3000, A4000, generic RDB, PCMCIA FAT32 and Gotek (FlashFloppy USB stick) available. PiStorm/Emu68 cards (MBR: FAT32 boot partition + a type 0x76 partition holding an RDB) are created and verified. PFS3 supported by embedding a user-supplied handler; PFS3/SFS formatting is left to the Amiga. |

## Known limitations

- OFS/FFS file read/write is limited to DOS\0–DOS\3 (DirCache DOS\4/5 is read-only; long names DOS\6/7 aren't supported) and 512-byte blocks.
- PFS3 and SFS volumes can be partitioned and identified, but not formatted or browsed.
- GPT is read-only (used for diagnostics).
- No automatic update check (by design; no network access).
