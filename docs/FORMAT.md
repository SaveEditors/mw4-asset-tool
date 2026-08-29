# IW 10.0 container formats (reverse-engineered)

All offsets verified empirically against a retail install. This documents the on-disk
container layout only; it does not cover the runtime asset graph.

## KAPI package (`.xpak` index + `.xsub` data)

Header (`KAPI`, version 3, subversion 23):

| Offset | Field |
|--------|-------|
| 0x00 | `KAPI` magic |
| 0x04 | u16 version (3) |
| 0x06 | u16 subversion (23) |
| 0x10 | u32 type (2 = index, 3 = data) |
| 0x18 | u64 self file size |
| 0x20 | u64 pairing GUID |
| 0x28 | u64 paired data-file count |

The index header (`XSUBHeaderV2`) tail fields sit at **+0x788**: FileCount, DataOffset,
DataSize, **HashCount**, **HashOffset**, HashSize, …, IndexCount, IndexOffset, IndexSize.

Data-file table at **0x800**: one 14-byte entry per `.xsub`, `{ GUID u64, u32, index u16 }`.
Files are matched to entries by the GUID in each `.xsub` header (+0x20).

### Asset hash entry (`XSUBHashEntryV2`, 0x14 bytes)

`{ Key u64, PackedInfo u64, Ex u32 }`

- `Offset = (PackedInfo >> 32) << 7` — **bit-packed**: `fileIndex = Offset >> 30`,
  `localOffset = Offset & 0x3FFFFFFF` (each `.xsub` is < 1 GiB).
- `CompressedSize = (PackedInfo >> 1) & 0x3FFFFFFF`
- `Ex = decompressed size`

### Object layouts at an entry's offset (all handled)

1. **Wrapped** — cache-id u64 at +2 equals Key; block count u8 at +22; then
   `XSUBBlockV2` (0x15 bytes, packed): `{ Compression u8, CompressedSize u32,
   DecompressedSize u32, BlockOffset u32, DecompressedOffset u32, Unknown u32 }`.
   Each block: seek `Offset + BlockOffset`, read `CompressedSize`, decompress
   (`0x0` none / `0x3` LZ4 / `0x6` Oodle) into `result[DecompressedOffset..]`.
2. **Raw** — cache-id ≠ Key and CompressedSize == DecompressedSize: the bytes are the asset.
3. **Headerless-compressed** — a bare Oodle/LZ4 stream at the offset.

Packaged image blobs are **headerless BCn** (no embedded dimensions); dimensions are
inferred from the byte length + shape.

## Fastfile (`.ff`)

`IWffa100`, version 25. The container is **Oodle-compressed, not encrypted** — the inner
zone reconstructs exactly to the declared size (header +0x14). The zone is a sequence of
Oodle blocks (≤ 0x10000 decompressed each); block framing includes a per-block hash, so
blocks are located via Oodle fuzz-safe validation and walked to the declared size.

Asset names inside the zone are hash-referenced and resolved by the game's string table at
runtime, so decompression yields the binary asset graph — not human-named files.

## Sidecars

- `.fp` — `IWffd100` v18 companion patch/preload stream.
- `*_cdn.manifest` — CDN on-demand download tables.
