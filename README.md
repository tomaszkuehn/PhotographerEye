# PhotographerEye

Portable JPG/JPEG/PNG, HEIC/HEIF/AVIF and RAW photo viewer for Windows (WPF, .NET 8, single `.exe`), with tone correction (shadows, gamma, histogram stretch) and saving of edited photos.

## Features

- Folder image browsing, navigation (arrows, Home/End); the ←/→ arrows switch photos always — even when a slider has focus (text editing takes priority)
- Fast RAW preview: the built-in camera thumbnail (LibRaw, largest of the thumbnail list) appears in ~0.3 s on open and arrow-key switching, before the full 16-bit decode lands; the thumbnail is rotated immediately per EXIF orientation (tag 274 — from the JPEG thumbnail or the file), and dimensions read from the RAW header (no decompression) swap axes for portrait orientation — aspect ratio and fit-to-window are correct from the first frame; neighbor thumbnails (±1) are pre-decoded in the background (preview cache active)
- Bottom bar, right side: file metadata when known — camera model, date, focal length, aperture, ISO, shutter speed (LibRaw for RAW, own TIFF parser for HEIC/HEIF/AVIF, `BitmapDecoder` EXIF for JPG/PNG/TIFF); loaded in the background after the image arrives, stays empty when the file has no data
- HEIF photos — HEIC/HEIF/HIF (iPhone and others) and AVIF — decoded straight from the file by the bundled LibHeif (LibHeifSharp + libheif with libde265/libx265/aom, no WIC/Store dependency), full input resolution into all corrections; container-transform orientation (`irot`/`imir`/`clap`, applied by the decoder) takes precedence, EXIF orientation applied only when absent; no support for encrypted files (Apple `.HEIC` with encrypted photos needs prior decryption), 10-bit HDR (HDR/SDR render with 8-bit conversion)
- Zoom 10–400%: mouse wheel, `+`/`-`, `1` — 100%, `0`/"Fit" button — fit to window, double-click — fit/100% toggle
- Pan: mouse drag (when the image is larger than the window); the image is always centered and never cropped
- Shadow correction −100…+100 (tonal mask above black, lifts deep shadows and spares halftones; linear sRGB pipeline) and gamma 0.50–2.00 (mid-tone LUT)
- Photoshop-style sliders: clicking the track sets the value at the clicked spot; dragging the **label or number** next to a slider adjusts precisely (Shift ×10 faster, Ctrl/Alt ×0.1 finer); **double-clicking the number** lets you type an exact value; Shift+arrows — big step, Ctrl+arrows — smallest step (0.01 EV); double-click the track or the ♻ button resets the correction
- Compare with the original: hold the "Compare" button or space (preview without corrections; releasing returns)
- Crop & rotate (toolbar "Crop" button or the `C` key): non-destructive, full-resolution crop with a live overlay — drag inside the frame to move it, drag the 8 handles (corners/edges) to resize (crop scaling), drag outside the frame or `Ctrl`+drag anywhere (also inside the frame) to straighten (free rotation ±45°, auto-inset so no empty corners ever appear; rule-of-thirds grid plus a finer 8×8 grid shown during straightening and handles); mouse wheel zooms the image also while cropping (anchored under the cursor, `Shift`+wheel pans); while dragging, the change renders instantly on a downscaled preview (rotation and cropping stay fluid even on full 24 Mpx 16-bit RAWs — the full-resolution render runs once, in the background, when the drag ends or a geo button is clicked); adjustments (toolbar sliders, panel, histogram stretch) are disabled while crop mode is open so slider changes can't fight the crop; aspect presets in the crop bar: Original (the image's own ratio, follows the rotated orientation), 3:2, 4:3, 16:9, 16:10; "Original frame" restores the original aspect ratio and full size; toolbar ⟲/⟳ rotate the image in 90° steps (orientation swap landscape ↔ portrait, the crop follows the content); `R`/Reset also removes crop and rotation; applied in the save path for both JPG (8-bit) and RAW (exact 16-bit planar: 90° rotations and crop re-arrange samples losslessly, free-angle uses bilinear resampling), stored in the `.eye` fingerprint (fields `Rot90`, `Angle`, `CropX/Y/W/H`, `CropAspect`) so it returns on reopen
- RAW file preview: RW2 (Panasonic), NEF/NRW (Nikon), ARW/SRF/SR2 (Sony), CR2/CR3/CRW (Canon), ORF (Olympus), RAF (Fuji), PEF (Pentax), DNG and others — bundled LibRaw: demosaicing, camera white balance, orientation auto-flip; **highlight reconstruction** (mixed LibRaw mode + clipping neutralization: burnt channels are rebuilt and clipped pixels desaturated to white — overexposed backgrounds don't shift magenta/violet), **16-bit-per-channel** decoding (48 bpp — native 12/14-bit sensor depth scaled to the full 0–65535 range, no step loss), on-screen preview with 4×4 Bayer dithering; **preview and all corrections always run at full resolution** (display scaled in the window); edit saving via dialog (JPG 90% / 16-bit TIFF) at full RAW resolution. **Camera tone curve for Sony**: for ARW/SRF/SR2 files the app derives the starting point from the JPEG preview embedded in the file — quantile matching of the scene luminance distribution (raw, after white balance) to the camera preview's displayed luminance distribution yields a monotonic scene curve in EV (32 knots, blacks preserved, highlight shoulder continued from the final segment), replacing the default tonal map in the base process (CPU and GPU); exposure/white balance work under the curve as in-camera; the fit is verified by gates (aspect match, pair count, spread, rank correlation, holdout-sample error better than the default map and within RMS) — failed gates (e.g. preview in another form, missing preview, unusual files) fall back to percentile auto-exposure; the curve is stored in the `.eye` fingerprint (the `ToneCurve` field) and session like `BaseEv`, and the Reset button adopts the new starting point. **Starting auto-exposure (other RAWs)**: exposure normalization is computed from the linear scene (dcraw-like 99.5th luminance percentile → ~0.93 of range, in EV), so RAW opens correctly exposed with no washed-out/darkened sliders; the value is stored in the `.eye` fingerprint (the `BaseEv` field) and session, and old saves (without the field) render as before — the Reset button adopts the new starting point
- Corrections panel (vertical bar on the right, slider button or the `E` key) — tonal model in the **linear scene**: RAW images work in the **linear scene** (LibRaw with gamma off), the base process is an **extended Reinhard** tonemap on luminance (EV LUT −14…+10 around 0.18 gray; whites shift the white point, blacks = soft knee/black lift, contrast = slope in EV) with sRGB encoding at the end; for JPG/PNG an S-curve in the gamma 2.2 domain (identity at neutral); **exposure** (±5 EV) as `2^EV` gain before the regions, **highlights/shadows** as local delta gains on log-luminance (smoothstep masks in EV around gray: shadows below +0.3 EV, highlights above −1 EV, factors 1.7) evaluated on an edge-aware base layer (fast guided filter on log-luminance, ~480 px grid, region radius 3% of the short side) so texture inside bright/dark regions keeps its local contrast and no halos form at edges; brightening rolls off toward white instead of clipping; highlights are desaturated to luminance at tonemap time (no magenta); **white balance** (temperature ±100, symmetric R/B in linear) with a **white-point eyedropper**, **clarity** (±100: large-radius local contrast with halftone weighting), **saturation** (±100: 0 neutral, −100 B&W, after tonemap), **sharpening** (0–150), **dehaze** (±100 haze subtraction on linear values — for RAWs with a camera curve applied after the tonemap in the display domain, so the slider responds just like for JPG/PNG), **noise reduction** (0–100, luminance grain) and **color noise reduction** (0–100, colored speckles and blotches) as two independent sliders: YCoCg planes in a noise-stabilized domain (sqrt of linear for RAW, encoded values for JPG/PNG) are decomposed with an undecimated B3-spline wavelet (4 luma / 5 chroma scales) and detail coefficients are shrunk with a Wiener-like gain whose threshold follows the noise measured on the photo itself, separately for every wavelet scale and 16 brightness bands (robust quantile and block-RMS estimates on a sparse grid, bumps caused by texture-only bands removed), so the stronger and coarser noise of dark areas — read noise, demosaicing blotches — is removed as well as midtone grain; edges and texture above the noise floor stay sharp while flat-area grain and color blotches disappear, and a clean photo is left almost untouched; a little fine luma grain is kept at strong settings; color noise reduction additionally runs a 3×3 median on the two chroma planes (one pass at 1 px spacing for single-pixel dots, one dilated pass at 2 px spacing for the 3–5 px blobs typical of X-Trans/Bayer demosaicing) before a stronger chroma wavelet shrink — brightness detail is never touched by it (both values stored in the `.eye` file as `Denoise` / `ColorNoise`); `.eye` files from version 0.93 (old 0–200% saturation scale) convert automatically
- GPU-accelerated corrections: RAW edit operations (noise reduction, clarity, sharpening, highlights, shadows, white balance/contrast/saturation, curves) computed on the graphics card via DirectCompute (ComputeSharp) — result nearly identical to CPU (mean difference ~0.03 out of 65535), several times faster on a heavy correction set. Hardware GPU detected at startup (largest-memory discrete card preferred); with no GPU — or with `PHOTOGRAPHEREYE_GPU=0` — processing runs on CPU only (automatic fallback). Acceleration status shown in "About…"
- Histogram overlay (luminance + R/G/B overlays) computed from the final preview; shown only with the Adjustments panel open (opening triggers a fresh compute), hidden on image change; scale normalized to the 99th percentile of bin counts (clipping peaks trimmed at the top, the rest stays readable), channel fills closed along the baseline
- Manual histogram stretch: drag a point on the histogram — the part on one side of the point compresses, on the other stretches; points are **anchored in scene EV** (log-luminance relative to 0.18 gray) and enter the tonemap LUT **before sRGB encoding**; piecewise-linear maps in EV with pinned range ends, points keep monotonicity; more points added with further grabs; points removed by: double-clicking the histogram, the reset (♻) button at the overlay's ×, `R`/Reset. The toggle (⤮) at the × selects the mode: **bake into sliders** (default — end of drag converts the knot into Exposure/Highlights/Shadows/Whites/Blacks values per the knot's EV and clears the points, further drags accumulate into the sliders) or an **independent curve** (knots stay as an EV curve entering the tonemap LUT before sRGB encoding, so sliders work consistently under it — +1 EV exposure shifts the scene by exactly 1 EV under the unchanged curve)
- Adjustment Undo/Redo (Ctrl+Z / Ctrl+Y or the ⟲/⟳ buttons): history of the last **50** states per image; commit after 600 ms of slider silence and immediately on histogram-grab end, B&W toggle and other edit-changing actions; undoing past a point truncates possible redo. History is stored in the `.eye` file next to the current state (the `History`/`HistoryPos` fields) — returns when reopening a photo with a saved fingerprint, and an in-memory session holds it between photos
- Documentary-style black & white: fast three-state toggle — toolbar button or the `B` key: color → **documentary** (panchromatic conversion with a slight red bias — like a yellow-red filter: bright skin, darkened blues + filmic S-curve: deep blacks, crisp mids, soft highlight rolloff) → **Tri-X 400** (emulation: stronger curve and fixed-pattern film grain per image, visible mostly in halftones); the histogram shows the result; reset on image change; saving honors the mode
- Compact dark UI: single toolbar line from the minimum window width (980 px)
- Custom title bar: black, reduced height, with minimize/maximize/close buttons
- Always full-resolution decoding (JPG/PNG and RAW); finished images held in a short cache, scaling only for display
- Image navigation arrows on the left/right window edges
- Settings icon (⚙) top-right → "About…" (author, e-mail, GitHub, license)
- Automatic edit reset on image switch, and manual: `R`, the "Reset" button, double-click a slider (or the histogram — clears stretch points)
- Session edit memory: slider settings are remembered per photo while browsing a folder — going back to a photo restores the sliders and re-renders them (the current session state wins over the saved `.eye` file); **auto-save of edits** (on by default, settings menu "Auto-save edits (.eye)"): every committed edit — the same moments as an undo step, plus undo/redo, switching photos and closing the app — is written at once to the `.eye` file next to the photo (with the undo history), so edits, including plugin settings such as heal spots, come back after a restart without saving the photo; a photo that was never edited gets no file, an unchanged state is not rewritten, the write goes through a temp file, and a read-only folder shows "Edits not saved to .eye" in the status bar; switched off, `.eye` is written only by Save Photo
- Saving the edited photo (Ctrl+S or the toolbar save button) via the **Save Photo** dialog — location and name choice (suggested `<current name>_eye`, the chosen format appends the extension) and format: **JPG (90% quality)** or **16-bit TIFF** (lossless 48 bpp RGB, real 16-bit values from RAW decoding — no 8-bit quantization, no compression — readers: WIC/Photoshop/GIMP); save at full source resolution with corrections applied (shadows, gamma, stretch points, black & white mode, corrections panel, crop/rotation — 90° steps and crop exact, free-angle bilinear; JPG/PNG at decoded size, RAW at native sensor dimensions, e.g. 4624×3088 from RW2); an edit-fingerprint file `<name>.eye` with correction parameters is created next to the image, auto-loaded on reopen; the button is active only when some correction is active
- Drag & drop a file, opening via CLI argument, `Ctrl+O`
- Settings stored in `%AppData%\PhotographerEye\settings.json`

## Running

Portable: `dist\PhotographerEye.exe [file|folder]` — no install, .NET bundled in. Also handles RAW files (RW2, NEF, ARW, CR2/CR3, ORF, RAF, DNG…) via bundled LibRaw and HEIC/HEIF/HIF/AVIF via bundled LibHeif.

## Keyboard shortcuts

| Key | Action |
|---|---|
| ←/→, PgUp/PgDn | next/previous image |
| Home/End | first/last image |
| mouse wheel | zoom (anchored under cursor) |
| `Shift`+wheel | horizontal pan |
| `1` / `0` | 100% / fit |
| `R` | reset edits (also crop/rotation) |
| `E` | adjustments panel: exposure, contrast, white balance, saturation, clarity, dehaze, noise reduction, color noise reduction |
| `B` | black & white: color → documentary → Tri-X 400 (loop) |
| `C` | crop & rotate mode (Esc closes) |
| `Ctrl`+drag | straighten (free rotation) |
| space (hold) | original preview |
| `Ctrl+O` | open file |
| `Ctrl+S` | save edited photo |

## Plugins

PhotographerEye loads .NET plugins that target `netstandard2.0` and reference `ImgViewer.Plugin.Abstractions` (`PluginSdk/`). The same plugin DLL works in the .NET 8 build and in the net6 Win7 build.

### SDK v2

- A plugin implements `IEyePlugin`. It has a `PluginManifest` (id, name, version, author, `Api = 2`, declared permissions) and an `Initialize(IPluginHost host)` method. In that method the plugin registers:
  - filters (`AddFilter`)
  - mask providers (`AddMaskProvider`)
  - importers (`AddImporter`)
  - exporters (`AddExporter`)
  - metadata providers (`AddMetadataProvider`)
  - commands (`AddCommand`)
  - presets (`AddPreset`)
- `host.Events` exposes `ImageOpened`, `BeforeSave` (which can cancel the save), `AfterSave` and `AfterExport`. `host.DataDirectory` is a writable folder for each plugin. `host.Imaging` provides blur, resize and JPEG/PNG encoding, and uses the GPU when one is available (`host.GpuAvailable`).
- **Filters** receive a `PluginImage` with float RGB planes in 0..1, the `FilterParams`, and a `RenderContext`. The context carries the stage, color space, preview flag, preview scale, full-resolution size, `ImageMeta`, cancellation token and progress callback.
- **Stages** decide where a filter runs:
  - `SceneLinear`: linear light, before the core adjustments.
  - `Display`: sRGB, after the core adjustments.
  - `Output`: after crop and rotate (for example a vignette).
- **Placement**: a filter goes either on the toolbar (a toggle button) or in the Adjustments panel (a collapsible section per `Group`, with up/down/reset buttons). The user's filter order is stored in `.eye`.
- **Parameter kinds**: `Slider`, `Toggle`, `Choice`, `Color` (hex, with an HSL picker), `Curve` (`x,y;...`, with a curve editor), `Point` (pick on the image), `File` (browse). Optional `IsIdentity` skips a filter while it would have no effect.
- **IDs are namespaced**: `pluginId:filterId`, and parameter values are stored as `pluginId:filterId/paramId`. Values that belong to plugins which are not installed are kept untouched in `.eye`, so a photo survives a round-trip through a machine without the plugin.
- **Masks**: any filter with `SupportsMask` (in the SceneLinear and Display stages) can be limited by one of:
  - a linear gradient
  - a radial gradient
  - a brush
  - a luminance range
  - a plugin mask provider (`plugin:<pluginId:maskId>`)

  Masks can be inverted. They are edited on the image through `Controls/MaskOverlay.cs`.
- **Presets**: plugins can ship presets, and users can save their own to `%APPDATA%\PhotographerEye\Presets`. Preset keys are `core.Exposure`, `core.Contrast` and the other core controls, plus plugin keys.
- **Built-in filters**: `core:lut` applies a `.cube` 3D LUT, either a built-in profile (warm, cool, tealorange, matte, vivid) or a file from `%APPDATA%\PhotographerEye\LUTs` or `luts\` next to the executable. `core:local` provides masked local adjustments.
- **v1 compatibility**: old `IPlugin` DLLs still load through `V1Adapter`. Each v1 feature becomes a Display filter `v1.<file>.<name>:<featureId>` with parameters `p0..pN`, and v1 `.eye` keys are migrated when a photo is opened.

### Packages, trust and failure handling

- **Package format**: a `.pepkg` is a zip that contains `manifest.json` (`id`, `name`, `version`, `author`, `api`, `entry`, `permissions`) and the assemblies.
- **Install**: `Settings ▸ Plugins… ▸ Install` opens a trust dialog before anything is executed. The dialog shows:
  - the manifest
  - the SHA-256 of the package and of the entry assembly
  - the Authenticode signature status
  - the plugin types and dependencies, read from assembly metadata without loading the code
  - the declared permissions

  Packages are installed to `%APPDATA%\PhotographerEye\Plugins\<id>\`. Plain `.dll` files are still accepted.
- **Automatic disabling**: a plugin is disabled after 3 consecutive failures (exceptions in a render, mask, importer, exporter or command). A preview render that takes longer than 8 s disables it immediately. The reason is saved in `settings.json` (`PluginDisabledReason`) and shown in the Plugins window, where the plugin can be re-enabled. Each plugin inside a package can be enabled or disabled separately.
- **Limitations**:
  - Plugins run **in-process**. There is no process isolation or sandbox: a plugin has the same rights as the app.
  - Permissions are **declarative only**. They are shown to the user before install but not enforced.
  - The timeout stops waiting for a hung preview and disables the plugin, but it cannot kill the plugin's thread. Install only plugins you trust.

### Samples

The v2 sample plugins are separate projects, one plugin each: `eyePlugins/negative/` (`sample.negative`), `eyePlugins/bwred/` (`sample.bwred`), `eyePlugins/splittone/` (`sample.grade`), `eyePlugins/vignette/` (`sample.vignette`), `eyePlugins/netpbm/` (`sample.netpbm`), `eyePlugins/webexport/` (`sample.web`), `eyePlugins/exposure/` (`sample.exposure`), `eyePlugins/sky/` (`sample.sky`) and `eyePlugins/eventlog/` (`sample.eventlog`). What each one does:

| Sample | What it shows |
|---|---|
| `sample.negative` | Toolbar filter plus a command (Ctrl+Shift+N) |
| `sample.bwred` | Black and white with a red mix; uses choice and toggle parameters and ships a preset |
| `sample.grade` | Split-tone grade; uses color and curve parameters |
| `sample.vignette` | Vignette in the Output stage; center picked as a point on the image |
| `sample.netpbm` | `.ppm`/`.pgm` importer |
| `sample.web` | Web JPEG exporter with options, plus an "outbox" exporter |
| `sample.exposure` | Exposure-value metadata provider |
| `sample.sky` | Sky mask provider |
| `sample.eventlog` | Logs host events |

- **Build**: `dotnet build eyePlugins\bwred\ImgViewer.Plugins.BwRed.csproj -c Release` (and so on for each folder; each build also packs a `.pepkg`). Building the app builds all bundled plugins and copies each one to its own `plugins\<folder>\` (`manifest.json` + DLL) in the output and publish folders (list: `BundledPlugin` items in `ImgViewer.csproj`). The installers ship those folders.
- **Harness coverage**: `TestPluginSamples` in `tools/Harness/PluginTests.cs` tests the following, using an isolated app-data dir (`PHOTOGRAPHEREYE_APPDATA`):
  - loading the samples and the namespaced IDs
  - each stage
  - each parameter kind
  - masks
  - LUTs
  - presets
  - `.eye` v5 round-trip and v1 migration
  - the v1 adapter
  - automatic disabling after 3 failures
  - the 8 s preview timeout
  - `.pepkg` create, inspect, install, reinstall and remove
  - the importer

## Installation

Two Windows installers, built with Inno Setup (`installer\PhotographerEye.iss`, driven by `installer\build.ps1`):

- `PhotographerEye-Setup-Win10-11-1.00.exe` — Windows 10 (1607+) / 11, GPU acceleration (ComputeSharp/DirectCompute).
- `PhotographerEye-Setup-Win7-1.00.exe` — Windows 7 SP1 x64, CPU-only build (`net6.0-windows`, GPU compiled out, identical corrections results).

Both are fully self-contained: the .NET runtime, LibRaw/LibHeif codecs, the `dcraw_emu` fallback and the VC++ 2015-2022 redist are packed inside, so nothing is downloaded during installation. Builds:

```
powershell -ExecutionPolicy Bypass -File installer\build.ps1          # both installers
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Win7    # only the Win7 one
```

The Win7 build is produced from the same sources: `ImgViewer.csproj` switches to `net6.0-windows` with `-p:Win7=true`, and `Adjust16.Gpu.Stub.cs` replaces the GPU layer (CPU path returns the same pixels as the GPU path, verified by the harness). Requires Inno Setup 6 (`ISCC.exe`, e.g. `%LOCALAPPDATA%\Programs\Inno Setup 6`).

## Building

```
dotnet build ImgViewer.csproj -c Release
dotnet publish ImgViewer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
```

## Tests

Logic harness (zoom/pan/fit, DPI, corrections, histogram, RAW, HEIC/EXIF, corrections panel):

```
dotnet run --project tools\Harness\tools.csproj -c Release
```

Auto-stretch diagnostic mode (channel percentiles, luminance thresholds, post-adjustment output):

```
dotnet run --project tools\Harness\tools.csproj -c Release -- image1.jpg image2.jpg
```

E2E UIA (requires a running app): `scripts\uia-e2e.ps1 <pid>`

Single-file diagnostics (harness modes): `--dump <file>` (decode/logic), `--meta <file>` (metadata), `--raworient <file>` (orientation: EXIF, header vs full-decode dimensions, preview, `AlignToPreview`), `--raw22 <file>` / `--rawhe <file>` (decoder fallback, resulting JPG), `--heif-probe` / `--heif-diag` (HEIC/HEIF/AVIF), `--sh <file> <out.png> <highlights> <shadows>` (renders Shadows/Highlights at slider values -100..100 to PNG), `--sh-raw <dir> <outDir>` (Shadows/Highlights on every RAW in a folder: CPU vs GPU diff, timings, clipping, PNG previews), `--nr-raw <dir> <outDir> [pushEv]` (noise reduction 0/50/100 on every RAW in a folder: CPU vs GPU diff, timings, 100% crops of a dark area as PNG).

## Structure

- `MainWindow.xaml(.cs)` — toolbar, title bar, navigation, saving, view refresh, settings
- `Controls/ImageViewer.cs` — zoom/pan/fit (layout rendering, no matrix transforms)
- `Controls/CropOverlay.cs` — crop frame UI: shades, rule-of-thirds grid, 8 resize handles (aspect-locked), move and outside-drag straighten, frame derived from the live displayed image rect; overlay rendering and hit-testing are clipped to the visible image area, so the crop lines and handles never cover the toolbar, status bar or adjustments panel (also when the image is zoomed/panned beyond the window)
- `Controls/HistogramOverlay.cs` — top-right histogram + interactive stretch points
- `Controls/PhotoshopSlider.cs` — Photoshop-like slider behavior (track click, precise label/number dragging with modifiers, value typing, arrow-key steps)
- `Imaging/RawDecoder.cs` — RAW decoding (LibRaw: 16 bit/channel **linear** (LibRaw gamma off, `-g 1 1` in fallback), mixed highlight mode, camera white balance, 16-bit pair cache + Bgra32 preview; fast RAW preview: `TryDecodePreview` — largest built-in JPEG/camera thumbnail, LRU cache ~12, `TryGetInfoHeader` — header dimensions without decompression; EXIF orientation (tag 274) applied immediately to header and preview, so portrait RAWs have correct proportions from open and fit-to-window works (`AlignToPreview` aligns dimensions to preview aspect for formats without EXIF in the container); starting preview = neutral base process: for Sony a tone curve fitted to the embedded JPEG preview (`Imaging/CameraTone.cs`, knots → EV LUT), otherwise exposure normalization from the luminance percentile (99.5% → ~0.93) + Reinhard tonemap + sRGB encoding; when the internal decode fails or yields garbage — automatic fallback to the bundled command-line tools: `thirdparty/libraw22/dcraw_emu.exe` (LibRaw 0.22.2; handles e.g. plain NEFs from Nikon Z6 III) and `thirdparty/librawhe/dcraw_emu.exe` (LibRaw fork with the Nikon NEF High Efficiency = TicoRAW/JPEG-XS decoder; decodes the HE variant, the HE* variant stays unsupported — instead of garbage the app shows a readable message)
- `Services/AdjustmentsProcessor.cs` — 8-bit pipeline for JPG/PNG: develop-operation order (white-balance+dehaze LUT → EV regions → clarity/sharpening → luminance tonemap with exposure) + gamma, manual stretch map, histogram
- `Imaging/Adjust16.cs` — RAW pipeline at 16 bit/channel in the **linear-scene** domain (white-balance+dehaze LUT → highlight/shadow regions in EV on log-luminance → clarity/sharpening → scene Reinhard tonemap with exposure and highlight anti-desaturation → saturation → sRGB encoding → gamma/manual map/mono/grain → histogram); for RAWs with a camera curve dehaze is applied after the tonemap (display domain), and WB enters without dehaze
- `Imaging/Adjust16.Gpu.cs` — GPU backend (ComputeSharp/DirectCompute) for `Adjust16`: hardware GPU detection (discrete-card preference), RAW edit operation shaders (incl. ToneKernel — EV LUT), automatic CPU fallback when no GPU or on error
- `Imaging/LinearRender.cs` — linear RAW gains: exposure in EV + temperature as plain multiplication in linear scene space (before regions and tonemap); linear→sRGB LUTs for display encoding
- `Imaging/Raw16.cs` — planar 16-bit RGB image, deinterleaving, 4×4 Bayer-dithered quantization, interleaving for TIFF
- `Imaging/PhotoGeo.cs` — non-destructive geometry (`GeoState`: 90° quarter turns, free-angle straighten, normalized crop): lazy quarter-turn+crop transforms for display, bilinear auto-inset rotation for both 8-bit BGRA and planar 16-bit RAW (save path), crop rect preserved across quarter turns
- `Services/EyeFile.cs` — `.eye` correction fingerprint (JSON) next to the source, auto-applied on open; stretch points stored in EV (`.eye` files with old display-fraction points converted on load)
- `Imaging/NoiseReduction.cs` — wavelet noise reduction for luminance and color (noise estimate, chroma speckle median, threshold plan, a trous decomposition; shared by CPU 16-bit, JPG and the GPU kernels in `Adjust16.Gpu.cs`)
- `Imaging/PhotoOps.cs` — EV shadow/highlight region masks (scene log-luminance), white-balance/dehaze LUTs, luminance tonemap (scene Reinhard for RAW — always active as the base process, display gamma-2.2 S-curve for JPG), saturation, clarity/sharpening (unsharp), temperature measurement from a sample
- `Imaging/ManualStretch.cs` — stretch-point map in scene EV (folded into the tonemap LUT before sRGB encoding)
- `Imaging/HeifDecoder.cs` — HEIC/HEIF/AVIF decoding (in-proc LibHeif): `IsHeif`, `GetInfo` with orientation per container/EXIF rules, `LoadScaled`/`DecodeFull` with a short LRU cache (3), `ExifBytes` for the metadata bar; in-file ISOBMFF parser (`ContainerHasTransforms`) detects `irot`/`imir`/`clap` in `ipco` (container transforms are already applied by libheif, so EXIF is skipped then)
- `Services/ImageFolder.cs`, `Services/SettingsStore.cs` — file list, settings
- `Services/PhotoMeta.cs` — file metadata for the bottom bar (`PhotoMetaReader.Get`, cached): RAW via LibRaw (`ImageParams`/`ImageOtherParams` — header only, no decompression), HEIC/HEIF/AVIF via own minimal TIFF parser (`ParseTiff` — LE/BE, IFD0 + ExifIFD: Make/Model, DateTimeOriginal, ExposureTime/FNumber/FocalLength/ISO), the rest via `BitmapDecoder` EXIF; no data → empty label
- `PluginSdk/` — plugin contract (`ImgViewer.Plugin.Abstractions`, netstandard2.0)
- `PluginHosting/` — plugin host: loading/registry/failure handling (`PluginManager.cs`), stage pipeline (`PluginPipeline.cs`), per-photo state (`PluginState.cs`), masks (`MaskRender.cs`), LUT + local filters (`BuiltInFilters.cs`), `.pepkg` + trust (`PluginPackage.cs`, `TrustDialog.cs`), presets, importer bridge; UI in `MainWindow.Plugins.cs`
- `tools/Harness/` — logic tests
- `scripts/` — E2E UIA tests and helpers

## Debugging

Set `PHOTOGRAPHEREYE_DEBUG=1`, log: `%TEMP%\photographereye-debug.log`. Uncaught exceptions are logged automatically (UI dispatcher as `UIEX:`, AppDomain as `DOMEX:`) — the app no longer crashes silently on a handler bug.

## License

Private, non-commercial use. Modification and redistribution prohibited — full terms: [LICENSE](LICENSE).
