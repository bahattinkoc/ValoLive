# GPU pipeline (branch `gpu-pipeline`)

Goal: keep every video frame on the GPU from capture to encoder, the way OBS does, so the CPU is left to the game.

## Why

Measured on the dev PC (Ryzen 5 5600X, RTX 3070), 1080p60:

| Path | CPU per frame |
|---|---|
| Today: WGC capture → `hwdownload` → CPU overlays → NVENC (uploads again) | ~6.5 ms (+ overlay work) |
| WGC capture → NVENC, nothing on the CPU | ~1.7 ms |

The GPU→CPU→GPU round trip alone costs ~29 % of a core. FFmpeg 9.0.2 can't composite D3D11 frames on the GPU: it has no D3D11 overlay filter, CUDA/Vulkan/OpenCL can't be derived from a D3D11 device on NVIDIA, and `scale_d3d11` to NV12 fails. So we write our own small engine.

## Architecture

```
ValoLive.exe (C#)                      valocap.exe (C++, new)                         ffmpeg (capture)         ffmpeg (publisher)
 ─ layers/panel drawn with GDI+   ──►  shared memory (raw BGRA, seqlock)
 ─ live commands (stdin)          ──►  ┌ WGC capture (window or monitor) ─ D3D11 texture
                                       ├ compositor @ 60 fps CFR (one D3D11 device):
                                       │   game (aspect fit) · masks · cover · flip · zoom
                                       │   · panel · layers (straight alpha)
                                       ├ BGRA → NV12 (BT.709 limited) in two pixel-shader passes
                                       ├ NVENC (D3D11 input, H.264 High, CBR, p5/hq, LA, AQ, 2 B-frames)
                                       └ MPEG-TS mux (video only, PTS/DTS)  ── pipe ──►  + game/mic audio      ──►  Kick (unchanged)
                                                                                         -c:v copy, mpegts
```

- **One process, one D3D11 device** for capture, compositing and encoding, so no shared textures or keyed mutexes are needed (OBS needs them because its encoder lives on a separate device).
- **Capture:** Windows.Graphics.Capture via C++/WinRT. `CreateFreeThreaded` frame pool with 2 buffers. The render thread drains `TryGetNextFrame` each tick and keeps only the newest frame, so there are no event handlers or cross-thread races. `IsCursorCaptureEnabled(false)`, `IsBorderRequired(false)` and `MinUpdateInterval(16 ms)` are set when present (feature-detected, like OBS). Window↔monitor switching happens inside the engine, so the encoder never restarts.
- **Clock:** CFR 60 on a high-resolution waitable timer. Frame *n* is due at `epoch + n/60`; if no new capture frame arrived, the last one is reused. `epoch` comes from ValoLive (same clock as the audio pipes).
- **Color:** our own RGB→NV12 shader (BT.709 limited range), tagged in the VUI. NVENC's built-in ARGB conversion matrix is undocumented.
- **NVENC:** SDK 12.0 header (FFmpeg `nv-codec-headers`, MIT), loaded from the driver's `nvEncodeAPI64.dll` (driver ≥ 522). The settings match today's stream: H.264 High, CBR 6000k, VBV 1 s, p5 + HQ tuning, lookahead 20, spatial AQ, 2 B-frames, GOP 120 (= 2 s, Kick's limit), SPS/PPS repeated on every IDR. Input is a ring of registered NV12 textures. In sync mode, after `EncodePicture` succeeds, the pending bitstreams are locked in order.
- **Timestamps:** the TS carries `pts = 10 s + n/60` (90 kHz) and `dts = pts(decode order) - 2 frames`. FFmpeg gets `-itsoffset (epoch - 10 s)` together with `-copyts`, exactly like the audio inputs.
- **Layers:** each layer and the panel gets a named shared-memory section `{seq, w, h, BGRA…}` that ValoLive writes into as it does today with the pipes. The engine checks `seq` every tick and calls `UpdateSubresource` only when it changed. A seqlock handles torn reads: if `seq` is odd or changed during the copy, the engine retries on the next tick. Hidden layers cost nothing.
- **Commands (stdin, one per line):** `layer <i> <0|1>`, `panel <0|1>`, `mask <i> <x> <y> <w> <h>`, `cover <0|1>`, `flip <0|1>`, `zoom <0|1>`, `source window <hwnd>` / `source monitor <idx>`, `quit`.
- **Stats (stdout):** every 5 s it reports fps, duplicated frames, GPU time and encode queue depth. ValoLive logs these as `[İSTAT]`.

## Fallback

If valocap fails to start (no NVIDIA GPU, old driver, old Windows), ValoLive uses today's FFmpeg-only path. The setting "GPU pipeline" is experimental and off until it has been tested on real streams.

## Phases

1. Engine: capture → composite (game only) → NV12 → NVENC → TS to file. Check it with ffprobe and a frame grab, and measure CPU.
2. Layers through shared memory, plus masks, cover, flip and zoom commands.
3. ValoLive integration: Streamer mode, stats, fallback, setting, build script.
4. TEST RECORDING, then a real stream test by the user.

## Later

- Feed enemy memory and stream moments from the engine's captured texture: a small staging copy of the minimap or top-bar region instead of GDI `CopyFromScreen`, which forces a DWM sync.
- GPU scheduling priority, the way OBS does it in admin mode.
