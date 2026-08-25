# AIMediaWorker

AIMediaWorker is a Windows 10/11 desktop media player and subtitle workstation built with WinUI 3, .NET 10, libmpv, FFmpeg, and the prebuilt CrispASR native runtime loaded directly from C#.

It plays local files, HTTP/HTTPS streams, HLS/DASH sources, and authenticated WebDAV media; imports SRT/WebVTT/ASS/SAMI subtitles and edits them on a timeline; captures still video frames; creates offline or live captions with Qwen3-ASR; and translates or summarizes transcripts through local and cloud LLM providers.

Media files can be opened through the picker, command line, folder explorer, playlist, or drag and drop. The side panel contains Explorer, Playlist, WebDAV, and Subtitles tabs; Explorer and WebDAV entries support name filtering and cyclic name/newest/oldest sorting. Drag the divider beside the side panel or above the timeline to resize either panel. The bottom playback toolbar provides previous/next media, seek, play/pause, stop, current-frame PNG capture, progress, time, volume, speed, and repeat controls. Window size, position, maximized state, panel visibility, and panel dimensions are restored on the next launch.

## Requirements

- Windows 10 version 1809 or newer; Windows 11 is recommended.
- x64 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Visual Studio with the Windows App SDK workload, or the `dotnet` CLI.
- x64 libmpv with its dependent DLLs.
- FFmpeg and FFprobe 6 or newer on `PATH`.
- The prebuilt native CrispASR runtime under `asr-worker/crispasr`.
- For practical Qwen inference: an NVIDIA CUDA GPU with current drivers and enough VRAM. CPU mode is supported but substantially slower.

The application does not bundle libmpv, FFmpeg, model weights, credentials, or API keys.
The Windows App SDK runtime is deployed self-contained with the application, so the unpackaged executable does not require a separately registered Windows App Runtime.

## Build and run

```powershell
dotnet restore AIMediaWorker.slnx
dotnet build AIMediaWorker.slnx -c Debug
dotnet run --project AIMediaWorker/AIMediaWorker.csproj -c Debug -p:Platform=x64
```

Release validation:

```powershell
dotnet build AIMediaWorker.slnx -c Release
dotnet test tests/AIMediaWorker.Tests/AIMediaWorker.Tests.csproj -c Release
```

## libmpv setup

The [official mpv installation page](https://mpv.io/installation/) lists maintained Windows binary providers. AIMediaWorker needs the embeddable **libmpv development build**, not the normal `mpv.exe` player archive.

1. Open the [shinchiro Windows build releases](https://github.com/shinchiro/mpv-winbuild-cmake/releases).
2. From the newest release, download `mpv-dev-x86_64-<date>-git-<commit>.7z`.
   - Choose a file containing both `dev` and `x86_64`.
   - Do not download the similarly named `mpv-x86_64-...` player archive.
   - The build without `-v3` has the broadest CPU compatibility. The `-v3` variant is appropriate only for CPUs supporting the x86-64-v3 instruction set.
3. Optionally compare the downloaded file's SHA-256 hash with the value shown next to the GitHub release asset.
4. Extract the `.7z` archive with [7-Zip](https://www.7-zip.org/).
5. Copy `libmpv-2.dll` from the archive into the repository-level `Libs` folder and rename it to `mpv-2.dll`:

   ```text
   AIMediaWorker/
   ├─ Libs/
   │  └─ mpv-2.dll
   ├─ AIMediaWorker/
   └─ AIMediaWorker.slnx
   ```

6. If the downloaded build supplies additional runtime DLLs, place those DLLs in `Libs` as well.
7. Build the application. Every DLL directly inside `Libs` is copied beside `AIMediaWorker.exe` for Debug, Release, and publish output.
8. Open **Tools → Diagnostics**. `libmpv` should report its version rather than `not loaded`.

AIMediaWorker requests mpv's `gpu-next` D3D11 renderer and `auto-safe` hardware decoding by default. D3D11VA, NVDEC, software decode, renderer, language preferences, cache/network timeout, subtitle appearance, playback rate, and seek interval are configurable.

When RTX Video Super Resolution is set to Auto or On, AIMediaWorker adds mpv's `d3d11vpp` filter with `scaling-mode=nvidia` and a 2x scale request to the D3D11 video path. NVIDIA App/Control Panel must still allow RTX Video enhancement for AIMediaWorker; the driver decides whether each frame is enhanced. Playback falls back to mpv's normal scaler when the filter is disabled or unavailable.

## FFmpeg setup

Install `ffmpeg.exe` and `ffprobe.exe` and add their directory to `PATH`. Verify:

```powershell
ffmpeg -version
ffprobe -version
```

FFmpeg is used as the audio extractor for ASR. Child processes are terminated when work is cancelled.

## Screenshots

Use the camera button in the playback toolbar or **Playback → Save current frame** to save the currently displayed video frame as PNG.

## Native Qwen3 and CrispASR setup

### Install the CrispASR runtime

AIMediaWorker uses CrispASR in-process through its C ABI. Download the full
Windows CUDA **lib** bundle below. The currently tested runtime is CrispASR
v0.8.29:

- [Download `libcrispasr-windows-x86_64-cuda.tar.gz`](https://github.com/CrispStrobe/CrispASR/releases/download/v0.8.29/libcrispasr-windows-x86_64-cuda.tar.gz)
- [View all CrispASR releases](https://github.com/CrispStrobe/CrispASR/releases)

The full bundle is self-contained and includes `crispasr.dll`, the `ggml`
backend DLLs, and the CUDA runtime DLLs.

On Windows, extract the downloaded `.tar.gz` twice if required by the archive
tool, then copy every DLL from its `bin` directory into the repository's
`asr-worker\crispasr` directory. With PowerShell, when the archive is in the
repository root:

```powershell
tar -xzf .\libcrispasr-windows-x86_64-cuda.tar.gz
New-Item -ItemType Directory -Force .\asr-worker\crispasr | Out-Null
Copy-Item .\libcrispasr-windows-x86_64-cuda\bin\*.dll .\asr-worker\crispasr\ -Force
```

The resulting layout must be flat; native dependencies must be beside
`crispasr.dll`:

```text
asr-worker/
└─ crispasr/
   ├─ crispasr.dll
   ├─ ggml-base.dll
   ├─ ggml-cpu.dll
   ├─ ggml-cuda.dll
   ├─ ggml.dll
   ├─ cudart64_*.dll
   ├─ cublas64_*.dll
   └─ cublasLt64_*.dll
```

Open **Settings → Automatic speech recognition** and choose **Install**. The
installer checks the native runtime and downloads these exact files to
`asr-worker\models` beside the executable:

- `Qwen3-ASR-1.7B-Q8_0.gguf` (CrispASR single-file `qwen3asr` Q8_0 model)
- `qwen3-forced-aligner-0.6b-q8_0.gguf`

In **Settings → Automatic speech recognition**, configure:

- Device (`Auto`, `Cpu`, `Cuda`), precision, VAD, language, and chunk duration.

The CrispASR runtime and model storage paths are fixed below
the executable's `asr-worker` directory.

Offline processing extracts bounded chunks, applies a native amplitude gate when VAD is enabled, restores global microsecond timestamps, uses the CrispASR forced aligner, and emits subtitles before the complete file finishes. The live path uses a bounded rolling PCM window because the Qwen3 C ABI is consumed through synchronous session calls.

Missing runtime files, weights, CUDA, FFmpeg, and out-of-memory errors are reported without crashing the WinUI process. Model licenses and access terms remain the user's responsibility.

## Subtitle workflow

1. Open a media file or URL.
2. Load SRT, WebVTT, ASS, or SSA, or let mpv select an embedded/sidecar track.
3. Edit text and microsecond timestamps, split/merge/duplicate/shift cues, or drag/resize cues on the timeline.
4. Use **AI → Generate subtitles**, **Translate**, or **Summarize** as needed.
5. Save as SRT, VTT, or ASS. Converting a styled format to a simpler format shows a style-loss notice while preserving text and timing.

The subtitle encoding setting supports UTF-8 and installed Windows code pages. Editor changes are mirrored to mpv through a debounced temporary ASS overlay and are protected by save/discard prompts.

## WebDAV and remote media

Open **Tools → WebDAV browser** to add, edit, or remove servers. Server metadata is stored in settings; passwords are stored separately in Windows Credential Manager. Supported operations include directory listing, parent navigation, refresh, direct remote playback, authenticated subtitle import (including `.smi`/SAMI), and saving the current WebDAV folder as a favorite. A same-name `.smi` file in the media folder is loaded automatically, with UTF BOM, UTF-8, and legacy EUC-KR/CP949 text detected without manual encoding changes. Connected directory results are also mirrored into the main window's WebDAV side-panel tab.

Local folders, media items, WebDAV folders, and remote URLs can be opened or removed from the Favorites menu. Recent items retain source type and playback position. Missing local paths or removed servers do not prevent startup.

Plain HTTP/HTTPS/HLS/DASH sources are handed directly to mpv. Authenticated WebDAV media is streamed directly for playback. When ASR is requested for an authenticated source, the application streams it to a temporary disk file, removes it after the job, and never writes credentials to settings or logs.

## Camera and live captions

Open **Tools → Camera and live captions**:

1. Select a camera, microphone, and supported camera format.
2. Start preview; optionally save MP4 capture.
3. Configure Qwen first, then start live captions.

Microphone audio is captured through WASAPI, resampled to 16 kHz mono PCM, and passed through bounded drop-oldest channels. Partial captions are visually dimmed; final captions are fully opaque. Font, size, colors, position, and maximum lines are configurable under Capture defaults. Windows camera/microphone privacy settings must permit desktop access.

## LLM providers

Supported provider profiles:

- **Local**: Unsloth Studio
- **Cloud**: Google Gemini, Ollama Cloud, OpenCode Go, and OpenCode Zen

API keys are stored in Windows Credential Manager.

## Keyboard shortcuts

| Gesture | Action |
|---|---|
| Space | Play/pause |
| Left / Right | Seek by the configured interval |
| Up / Down | Volume ±5 |
| M | Mute/unmute |
| V | Show/hide subtitles |
| Ctrl+Left / Ctrl+Right | Previous/next subtitle |
| Ctrl+S / Ctrl+Shift+S | Save/Save As |
| Ctrl+Z / Ctrl+Y | Undo/redo |
| Delete | Delete selected cues |
| Ctrl+W | Close the application |
| Enter / F / F11 | Toggle fullscreen |
| Esc | Leave fullscreen |
| Home / End | Seek to start/end |
| Backspace / Ctrl+Shift+N | Play the current media from the beginning |

Text boxes retain ordinary editing shortcuts. Available shortcuts are shown beside their menu commands or in the related control tooltip. Shortcut gestures are persisted as a declarative action-to-gesture map in `settings.json`, so custom configuration UI can be added without changing command routing.

In fullscreen mode the window frame and panels are hidden. Fullscreen opens on the display currently containing the window and remains frame-free when moved between displays. Hover at the top, bottom, or right edge to reveal the menu, playback toolbar, or side panel respectively; the wider right-edge activation zone also works across a shared dual-monitor boundary.

## Settings, data, and diagnostics

Per-user data is stored below `%LOCALAPPDATA%\AIMediaWorker`:

- `settings.json`: atomically replaced application settings.
- `recent.json`: recent media and playback positions.
- `favorites.json`: favorite media and folders.
- `Logs\app.jsonl`: size-rotated structured diagnostics without credentials.

Corrupt settings are preserved as `settings.json.corrupt-*` and replaced with safe defaults. The UI supports English, 한국어, and 日本語 resources plus System/Light/Dark themes. A language change applies to newly created views and fully applies after restarting the application.

**Tools → Diagnostics** reports app, Windows, .NET, Windows App SDK, libmpv, FFmpeg, CrispASR runtime, the in-process ASR engine/model, GPU/driver, RTX VSR capability, and log location.

## Troubleshooting

- **Playback unavailable**: confirm x64 `mpv-2.dll` and its dependencies are next to the executable. Check Diagnostics for an architecture mismatch.
- **FFmpeg/FFprobe unavailable**: add both executables to `PATH`, restart the app, and inspect Diagnostics.
- **MODEL_NOT_FOUND**: verify the two exact GGUF files under `asr-worker\models`.
- **CUDA_OOM**: use a lower-precision mode, reduce competing GPU load, or select CPU mode.
- **WebDAV 401/403**: edit the server and re-enter the credential. Only Basic authentication is currently supported by the built-in WebDAV browser.
- **Remote seek unavailable**: the origin must support byte ranges. mpv can continue sequential playback where the protocol permits.
- **Camera or microphone denied**: enable desktop-app access under Windows **Privacy & security** and reopen the capture window.
- **LLM model sync fails**: verify the endpoint/key; select a cached model or enter an exact model ID manually.
- **RTX VSR not visible**: use current NVIDIA drivers, enable video enhancement for AIMediaWorker in NVIDIA App, and verify an RTX adapter in Diagnostics.

Environment-dependent media, GPU, camera, WebDAV, model, and provider checks are listed in [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md).
