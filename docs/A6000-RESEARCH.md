# Apollo A6000 storage: research notes

Researched 2026-10-28. **The official A6000 manual** (`wiki.apollo-computer.com/doku.php?id=a6000manual:start`) **returned HTTP 500 for every page that day** (a DokuWiki plugin fault). Statements below attributed to the manual come from search-engine excerpts of it and must be re-checked against the live page.

## Findings

| Topic | Finding | Source | Confidence |
|---|---|---|---|
| System drive | Ships with a 128 GB CompactFlash card with ApolloOS preinstalled; boots ApolloOS from it. | apollo-computer.com/a6000.php; Obligement review | High |
| ROM | ApolloROM only boots ApolloOS; Kickstart ROMs only boot AmigaOS. | A6000 manual (excerpt) | Medium |
| SD slots | Two full-size SD slots (the V4 Standalone had microSD). | Product page; Obligement review | High |
| Slot roles | One slot: removable storage or alternate boot device using a *native (RDB) bootable OS image*. The other is intended for floppy-image style boot. | A6000 manual (excerpt) | Medium; **sources disagree on which is left/right** |
| SD boot in practice | A reviewer could not boot a disk image from SD; reportedly only the CF boots. | Obligement review | Low–medium |
| Data cards | SDHC supported. If ApolloOS can read a card, don't reformat it; otherwise FAT32 with Rufus: non-bootable, label **SD0**, quick format, no extended label files. | apollo-computer.com/faq.php | High |
| FAT32 compatibility | "ApolloOS is not compatible with all out-of-the-box FAT32 formatted SD cards"; the documented recipe is the fix. | FAQ | High |
| Mounting | ApolloOS Start Menu > SD Card > Mount. | FAQ | High |
| "Uninitialized" | Partitions that have not been formatted show as *Uninitialized* after mounting and must be formatted. | Apollo wiki system_tools:microsd (excerpt) | Medium |
| OS images | ApolloOS distribution images are written to the card with an image writer (e.g. Win32DiskImager). | FAQ | High |

## What the app does with this

- **File Transfer (Verified):** MBR, one FAT32 partition (type 0x0C, 1 MiB aligned), label `SD0`, at most 32 KiB clusters. A warning appears above 32 GB, since SDHC is the documented card type.
- **Hard Drive (Experimental):** a standard RDB with FFS (DOS\3) partitions. These are conservative choices, confirmed as the defaults by the project owner on 2026-10-28 but not yet tested on an A6000:
  - 4 GiB cap per FFS partition (a warning, not an error) and MaxTransfer 0x1FE00.
  - Up to 8 partitions are created automatically; the summary shows any space left unused.
- **CompactFlash (Protected):** backup and authorised image restore only.

## Likely causes of the original report (FAT32 card shows "Uninitialized")

In order of likelihood. The Diagnose page checks each one:

1. The card is actually exFAT (Windows' default for cards over 32 GB), or not a plain MBR+FAT32 layout.
2. A leftover RDB from earlier Amiga use, still in sectors 1–15 behind the Windows MBR.
3. GPT partitioning or a superfloppy (no partition table) layout.
4. Non-standard factory FAT32 parameters. Reformatting with the documented recipe fixes this.
5. The card was mounted through the hard-drive (RDB) path rather than the SD Card > Mount (FAT) path.

## Open questions (need the live manual or an A6000)

1. Which physical slot is the RDB/boot slot, and which is the floppy-image slot?
2. Does ApolloOS auto-mount RDB partitions from an SD slot, or is SDMount or a mountlist required?
3. Are PFS3 and/or SFS handlers included in ApolloOS?
4. What is the largest FFS partition ApolloOS handles reliably on `sagasd.device`? (Capped at 4 GiB by default, as decided on 2026-10-28; raise it only after testing.)
5. What is the correct MaxTransfer for `sagasd.device`?
6. Is the `SD0` volume label actually required, or only recommended?
7. Does a superfloppy (no MBR) FAT32 card mount?

Once confirmed, update `src/AmigaSdCreator.Core/Profiles/profiles.json` (or drop an override file into `%LOCALAPPDATA%\AmigaSdCreator\Profiles`) and move the item from `unverified` to `facts` with its source.
