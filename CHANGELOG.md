# Changelog

## 0.9.7-beta — More quality-of-life

- **Recent folders** menu to re-open a previously imported game folder in one click (with a
  "clear recent folders" option).
- **Copy image** — copy the decoded preview to the clipboard.
- **Reveal in Explorer** — jump to the last export from the status bar.
- **Ctrl + scroll** over the thumbnail grid to zoom the tile size.

## 0.9.6-beta — Quality-of-life polish

### Sidebar
- Per-package **Assets** and CDN-stub counts, with a category-colored dot and a rich hover tooltip.
- CDN-only assets are shown amber; selecting a CDN stub explains it is streamed, not installed.
- Package **quick-filter** box (matches name or category).

### Browsing
- Sort assets by **Images first** or **CDN stubs first**; sort the sidebar by asset/CDN counts.
- Right-click actions in both the table and the grid: copy hash/offset, jump-to-address,
  **export raw asset**, and **export multiple selected assets** at once.
- Keyboard: **Esc** resets search/filters; **Ctrl+C** copies the selected asset's hash.

### Import / export
- **Drag and drop** a game folder (or a file within it) onto the window to import it.
- Folder import now runs **off the UI thread**, so opening/dropping a folder never freezes.

### Inspector
- **Copy** buttons for the asset hash and global offset; empty-state guidance when nothing is selected.

### Session
- Remembers and restores grid/table mode, thumbnail size, last-opened package, and window geometry.

## 0.9.5-beta — Update tracking in the UI + readable menus

Change tracking is now **in the app**, not just the headless CLI:
- A compact **"✦ N updated"** chip appears in the header after a game update; it is a toggle —
  click to show only new/changed assets, click again to clear.
- Two new filters in the **Show** dropdown — **New (since update)** and
  **Changed (since update)** — work in both the table and the thumbnail grid.
- A **Δ** column badges each row **NEW** (green) or **CHG** (amber).
- Changes persist across launches (the pre-update snapshot is kept as `previous.snap`), so you
  can review what a patch changed any time until the next patch — not only on first launch.

UI fixes:
- **Tooltips and right-click menus are now dark and readable.** Tooltips had no dark style and
  inherited the app's light text over the pale system background (unreadable); both are now
  fully dark-themed.
- Fixed a latent crash: a disabled menu item referenced an undefined brush.

## 0.9.4-beta — Thumbnail quality

- Thumbnails are now decoded at a moderate resolution (dense block sampling, not the previous
  sparse every-Nth-block subsample) and downscaled with a proper **box/area filter** instead of
  nearest-neighbour, so grid tiles are smooth and match the inspector preview instead of looking
  blocky and aliased.
- Verified end-to-end on a real package: 15,830 image assets, all decode and cache correctly.

## 0.9.3-beta — Update resilience + change tracking

### Surviving game updates
- Thumbnail cache now fingerprints each entry with the asset's compressed/decompressed size,
  so after a game update a changed asset **auto-invalidates and re-decodes** instead of showing
  a stale preview. (The cache version bump also flushes pre-0.9.2 squished thumbnails.)
- Package discovery already skips locked/partial/new-format files, so running mid-update does
  not crash — it just indexes what it can read.

### Change tracking (what a patch added / removed / changed)
- New catalog **snapshot + diff**. Snapshots read only the `.xpak` indices (no decompression),
  so capturing the whole game is fast (~830k assets in seconds). Headless CLI:
  - `MW4AssetTool.exe --snapshot <out.snap> [gameDir]` — capture the current catalog.
  - `MW4AssetTool.exe --diff <old.snap> <new.snap|gameDir> [report.txt]` — report assets added,
    removed, or changed (by size) per package, plus added/removed packages.
- Take a baseline snapshot after the game is fully updated; diff the next update against it.

## 0.9.2-beta — Thumbnail aspect + corrections that stick

### Thumbnails
- Fixed a scaling bug where large non-square textures were downsampled to a **square**
  thumbnail (a tall 256×512 poster collapsed to 96×96), so the grid tile looked nothing like
  the inspector preview. The coarse downsampler now preserves aspect ratio.

### Detection
- **Manual corrections now actually override auto-detection.** Previously a confirmation in the
  "show all formats" tool was logged but a wrong-but-locally-smooth interpretation (e.g. a
  transpose whose flat centre scores high) still won. A confirmed (size → format, dimensions)
  now strongly overrides the crop score, and because the prior is keyed by byte size, one
  correction fixes every asset of that size. Unconfirmed sizes are unchanged.
- Investigated whole-image scoring to auto-fix transposes, but every variant regressed the
  clean labeled-corpus cases (50/50 → ≤45/50), so it was not shipped; the manual override +
  size-keyed learning is the reliable path for genuinely ambiguous same-size interpretations.

## 0.9.1-beta — Texture detection overhaul

### Texture detection
- Auto-detection now scores **every** exact-byte-size interpretation instead of stopping at
  the first smooth-looking one, fixing the main error where a 2:1 texture was misread as a
  square of the same byte length. On an internal labeled test, format-and-dimension recovery
  on decidable cases rose from ~65% to near-perfect.
- Added a BCn block-seam term to the likelihood score: a wrong format/dimension re-reads each
  4×4 block out of alignment and seams at block boundaries, which is now penalised.
- Added a fixed IW10 format prior (BC7-dominant) that breaks genuinely-ambiguous ties (a colour
  image that is a valid BC1 / BC3 / BC7 decode) toward the statistically-correct format.
- **Closed the correction feedback loop:** the `format_choices.csv` written by the "show all
  formats" tool is now read back into detection (`FormatPrior`), so every interpretation you
  confirm improves auto-detection for textures of that size. Empty until first use — with no
  history, detection is unchanged.

## 0.9.0-beta — First public beta

A native Windows (WPF / .NET) asset browser and extractor for the current-generation
Call of Duty engine (IW 10.0). Read-only, offline, static file analysis — never launches,
patches, or injects into the game.

### Extraction
- KAPI package reader (`.xpak` index + `.xsub` data) with Oodle / LZ4 block decompression.
- Handles all on-disk object layouts (wrapped, raw, headerless) — full package extraction.
- Fastfile (`.ff`) Oodle zone decompression + raw zone export.
- Batch export: raw asset, whole package, whole game.

### Textures
- BC1–BC7 and uncompressed (R8/RG8/RGBA8) decoding to RGBA.
- Best-guess interpretation for headerless blobs, scored on a contiguous crop so the
  thumbnail grid and inspector always agree; manual "show all formats" override.
- PNG + DDS (DX10) export, single and batch (per-package / whole game).

### Browsing / UX
- Virtualized asset table at 100k+ rows; background-preloaded, disk-cached thumbnail grid.
- Content / Maps catalog derived from readable fastfile names.
- Search, category / UI-role filters, sortable columns, jump-to-address tabs, right-click
  copy / jump, adjustable thumbnail size, CSV catalog export.
- Name importer (CSV / TXT / JSON hash→name) to label assets when a dictionary is available.

### Known beta limitations
- Texture format auto-detection is a heuristic and is wrong on a meaningful share of
  textures (on the order of half in some packages) — use the manual "show all formats"
  override to select the correct interpretation.
- Per-asset names are not resolved; assets are shown by hash. Names are hash-keyed and
  resolved by the game at runtime, so they are not statically recoverable.
- GSC scripts, audio, and video are not extractable via static analysis on this engine
  (hash-keyed / runtime-linked). See `docs/FORMAT.md`.
