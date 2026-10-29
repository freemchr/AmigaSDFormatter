<p align="center"><img src="src/AmigaSdCreator.App/Assets/logo.png" alt="Amiga SD Creator" width="480"></p>

# Amiga SD Creator

A desktop utility for **Windows** and **Linux (Debian, Ubuntu, Raspberry Pi OS)** that prepares, diagnoses, backs up and fills SD cards for Amiga computers, without needing HDToolBox on the Amiga.

- **Apollo A6000**: FAT32 data-transfer cards for the SD slot, and Amiga hard-drive (RDB) cards.
- **Classic Amigas** (A500, A600, A1200, A2000, A3000, A4000, any other RDB machine, WinUAE): SD/CF-to-IDE hard-drive cards laid out exactly like a real Amiga hard drive, with Kickstart-aware size limits. Also FAT32 transfer cards, FAT32 USB sticks for Gotek/FlashFloppy, and **PiStorm (Emu68)** SD cards (FAT32 boot partition + Amiga 0x76 partition with an RDB). For PiStorm the app downloads the latest Emu68 release from GitHub and installs it; you can add your own Kickstart ROM and set it up for a **Framethrower** video board (720p/1080p 50 Hz, pixel-perfect or smooth scaling).
- **Diagnose** an existing card without changing it, including likely reasons for an *"Uninitialized"* volume.
- **Amiga Files**: drag and drop files between your computer and the FFS partitions on a card or image.
- **Backup & Restore** whole cards, with SHA-256 checks and read-back verification.

> Version 1.0. The on-disk formats are verified against independent tools (see [Verification](#verification)), and the Apollo A6000 FAT32 transfer card has been tested on real hardware. Hardware test reports for other machines are welcome.

## Using it

1. Run `AmigaSdCreator.exe` (Windows) or **Amiga SD Creator** from the applications menu (Linux). Writing to cards needs administrator (Windows) or root (Linux) rights, and the app offers to restart elevated when that's required.
2. **Select SD Card.** Only USB/SD card readers are offered. The system disk is always protected.
3. **Choose your Amiga** (top-right dropdown) and a **configuration tile** (e.g. *File Transfer* or *Hard Drive*).
4. Check the summary (partition scheme, filesystem, partitions, boot partition, Kickstart, unused space).
5. Press **Prepare SD Card**, review what's on the card now, and type `ERASE`.
6. The app writes the layout, reads every structure back, and verifies it.

*Customise partitions…* opens the full wizard: partition sizes, DosTypes, MaxTransfer, boot priority, and embedding a filesystem handler such as pfs3aio. *Create an image file instead* builds the same layout as a `.img`/`.hdf` file.

### If your Amiga says "Uninitialized"

Run **Diagnose**, pick the card and the slot you use it in. Common findings:

| Finding | Meaning |
|---|---|
| exFAT | Most cards over 32 GB ship as exFAT. Amiga FAT handlers need FAT32. |
| GPT, or no MBR | The documented A6000 recipe is MBR + one FAT32 partition. |
| Leftover RDB behind an MBR | The card was once an Amiga hard drive, then reformatted on Windows. Amiga drivers find the old RDB first. |
| RDB partition with no filesystem | Normal for new partitions: format them on the Amiga. |
| PC layout used as a hard drive | Amiga hard drives need an RDB. The card isn't damaged. |

The tool never calls a card "corrupt" just because Windows can't read it. Windows can't read Amiga partitions by design.

## What gets written

| Configuration | Layout |
|---|---|
| File Transfer | MBR, one partition type 0x0C at 1 MiB, FAT32 written by this app's own formatter (fatgen103 rules, fat32format/Rufus cluster table). The label is `SD0` for the A6000, as Apollo's FAQ instructs. |
| Hard Drive | RDB at block 0 (RDSK, PART, FSHD/LSEG), 16 heads × 63 sectors, partitions cylinder-aligned after a ≥1 MiB RDB area. OFS/FFS partitions can be formatted immediately; PFS3/SFS partitions are left blank for the Amiga's `Format`. |

Before writing, the first and last 1 MiB of the card are cleared so that no stale MBR/GPT/RDB remains.

## Linux

Install the package for your machine from the [releases page](https://github.com/freemchr/AmigaSDFormatter/releases):

```
sudo apt install ./amiga-sd-creator_<version>_amd64.deb     # PCs
sudo apt install ./amiga-sd-creator_<version>_arm64.deb     # Raspberry Pi OS 64-bit, other ARM64
```

Then start **Amiga SD Creator** from the applications menu, or run `amiga-sd-creator`. The app opens as your normal user and can list cards. To read or write them, press **Restart as root**: polkit asks for your password and reopens the app with access to the card. The `asdc` command-line tool is installed too.

How the Linux version differs from the Windows one:

- Cards are found with `lsblk`. A disk is protected if it, or anything stacked on it (LVM, LUKS, RAID), holds `/`, `/boot`, `/usr`, `/var`, `/home` or swap.
- Before writing, the card's mounted partitions are unmounted, and the device is opened with `O_EXCL`, so the kernel refuses the write if anything still uses the card.
- FAT32 partitions mounted by your desktop (for example `/media/you/SD0`) appear on the Files page. For PiStorm cards, the app mounts the new boot partition itself to install Emu68 if the desktop doesn't.
- Settings and logs are in `~/.local/share/AmigaSdCreator`. Files the root app saves in your home folder are given back to you.

## Safety

- Inspection is read-only; disks are opened read-only unless a write was confirmed.
- System, boot and page-file (swap) disks can never be written, whatever the settings. Internal (SATA/NVMe) disks and disks over 512 GB are blocked unless enabled in Settings.
- The card's identity (path, model, serial, unique ID, size, sector size) is captured when you select it. It is re-checked right before writing and again after opening the device. If anything differs, or the card has gone, nothing is written. The app never falls back to another disk.
- Volumes on the card are locked and dismounted (Windows) or unmounted and opened exclusively (Linux) before raw writes.
- Interrupted writes are reported as such ("the card is now incomplete"), never as success.
- Logs (`%LOCALAPPDATA%\AmigaSdCreator\Logs` on Windows, `~/.local/share/AmigaSdCreator/Logs` on Linux) use disk numbers and a short hash. They never contain serial numbers or file contents, and your user folder path is masked.

## Building

Requirements: .NET 10 SDK. The WPF app and Windows layer build on Windows. The core library, shared view models (`AmigaSdCreator.Ui`), the Linux app (`AmigaSdCreator.Desktop`, Avalonia), CLI and tests build and run on Linux.

```powershell
dotnet build AmigaSdCreator.sln
dotnet test tests\AmigaSdCreator.Core.Tests
dotnet run --project src\AmigaSdCreator.App
```

Release packages: `pwsh build\publish.ps1` runs the tests and produces in `artifacts\`:

- `AmigaSdCreator-<ver>-win-x64-portable.zip`: self-contained single-file app; no .NET install needed.
- `asdc-<ver>-win-x64.zip`: command-line tool.
- `AmigaSdCreator-<ver>-setup.exe`: only if Inno Setup 6 is installed. `build\installer.iss` has not yet been compiled.

Linux packages: `build/package-deb.sh` runs the tests and produces `artifacts/amiga-sd-creator_<ver>_amd64.deb` and `_arm64.deb` (self-contained; no .NET install needed). Run the Linux app from source with `dotnet run --project src/AmigaSdCreator.Desktop`.

### Command-line tool (image files only)

```
asdc profiles
asdc create data.img --size 8G --interface a6000-sd-data
asdc create hdd.hdf  --size 4G --interface a1200-ide --use BootableSystem --mode ks31
asdc inspect card.img --interface a6000-sd-data --report report.md
```

`asdc` never opens physical disks.

## Verification

- **102 automated tests** (`tests/AmigaSdCreator.Core.Tests`): partition tables, FAT32, FFS, the FFS file engine, layout limits, diagnostics fixtures (exFAT, GPT, stale RDB, superfloppy, unformatted RDB), safety gate, backup/restore including gzip, a fake-capacity card, an interrupted write, and the Linux disk layer's lsblk/mount parsing.
- **Interop with amitools 0.8.1** (rdbtool/xdftool, an independent implementation): our RDBs pass `rdbtool info/show`, and we read rdbtool-made RDBs. Our FFS volumes are readable and writable by xdftool. Files we write are unpacked byte-for-byte by xdftool (OFS and FFS, including extension blocks), and we read files xdftool wrote. These tests run automatically when amitools is installed:
  ```
  uv venv ~/.cache/asdc-tools/venv && uv pip install --python ~/.cache/asdc-tools/venv/bin/python amitools==0.8.1
  ```
  (or set `ASDC_AMITOOLS_BIN`). amitools is GPL-2.0; it's used only as a test oracle and isn't distributed.
- **FAT32** output was cross-checked with pyfatfs (reads, writes, lists).

See [docs/VERIFICATION.md](docs/VERIFICATION.md) for what still needs real hardware.

## Documentation

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): structure, design decisions, phase status
- [docs/A6000-RESEARCH.md](docs/A6000-RESEARCH.md): Apollo A6000 storage findings, sources and open questions
- [docs/VERIFICATION.md](docs/VERIFICATION.md): verification status and the hardware test checklist

## Not included

No Kickstart ROMs, AmigaOS/Workbench files, ApolloOS images or third-party filesystem handlers are bundled. Emu68 (MPL-2.0) is downloaded from its GitHub releases only when you prepare a PiStorm card. Supply your own licensed copies (for example pfs3aio from Aminet, or the ApolloOS image from Apollo).
