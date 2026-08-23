# AIMediaWorker

AIMediaWorker is a Windows 10/11 desktop media player and subtitle workstation built with WinUI 3, .NET 10, libmpv, FFmpeg, and a separately restartable Python Qwen3-ASR worker.

It plays local files, HTTP/HTTPS streams, HLS/DASH sources, and authenticated WebDAV media; edits SRT/WebVTT/ASS subtitles on a timeline; generates bounded-memory waveforms; creates offline or live captions with Qwen3-ASR; and translates or summarizes transcripts through local and cloud LLM providers.

## Requirements

- Windows 10 version 1809 or newer; Windows 11 is recommended.
- x64 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- Visual Studio with the Windows App SDK workload, or the `dotnet` CLI.
- x64 libmpv with its dependent DLLs.
- FFmpeg and FFprobe 6 or newer on `PATH`.
- Python 3.11 or 3.12 for ASR. Python 3.12 is the recommended environment.
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
python -m unittest discover -s asr-worker/tests -v
```

## libmpv setup

1. Obtain a maintained x64 Windows libmpv build compatible with your distribution requirements.
2. Place `mpv-2.dll` and every DLL it depends on beside `AIMediaWorker.exe`. If the distribution calls the import library `libmpv-2.dll`, use the actual runtime filename expected by the application: `mpv-2.dll`.
3. Open **Tools → Diagnostics**. `libmpv` should report its version rather than `not loaded`.

AIMediaWorker requests mpv's `gpu-next` D3D11 renderer and `auto-safe` hardware decoding by default. D3D11VA, NVDEC, software decode, renderer, language preferences, cache/network timeout, subtitle appearance, playback rate, and seek interval are configurable.

RTX Video Super Resolution is controlled by the NVIDIA driver/NVIDIA App. AIMediaWorker detects RTX-class hardware and preserves the D3D11 video path, but does not claim driver-side enhancement is active. Enable RTX Video enhancement for the application in NVIDIA App. Playback falls back to mpv's normal scaler when unsupported or disabled.

## FFmpeg setup

Install `ffmpeg.exe` and `ffprobe.exe` and add their directory to `PATH`. Verify:

```powershell
ffmpeg -version
ffprobe -version
```

FFmpeg is used as a streaming PCM source for waveforms and as the audio extractor for ASR. The implementation retains bounded min/max peaks instead of complete decoded audio and terminates child processes when work is cancelled.

## Python and Qwen setup

Create an isolated environment. Install a PyTorch build appropriate for your CUDA driver first, then the worker dependencies:

```powershell
py -3.12 -m venv .venv
.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
# Install the CUDA or CPU PyTorch build appropriate for this machine first.
python -m pip install -r asr-worker/requirements.txt
```

In **Settings → Automatic speech recognition**, configure:

- Python executable, for example `D:\path\to\.venv\Scripts\python.exe`.
- Qwen3-ASR-1.7B local directory or model identifier.
- Optional Qwen3-ForcedAligner-0.6B directory or model identifier.
- Device (`Auto`, `Cpu`, `Cuda`), precision, VAD, language, and chunk duration.

Offline processing extracts bounded chunks, optionally applies Silero VAD, restores global microsecond timestamps, uses forced alignment when configured, and emits subtitles before the complete file finishes. The live path uses a bounded rolling 30-second PCM window because transformer Qwen streaming does not expose the same official low-latency vLLM path.

Missing packages, weights, CUDA, FFmpeg, and out-of-memory errors are reported without crashing the WinUI process. Model licenses and access terms remain the user's responsibility.

## Subtitle workflow

1. Open a media file or URL.
2. Load SRT, WebVTT, ASS, or SSA, or let mpv select an embedded/sidecar track.
3. Edit text and microsecond timestamps, split/merge/duplicate/shift cues, or drag/resize cues on the timeline.
4. Use **AI → Generate subtitles**, **Translate**, or **Summarize** as needed.
5. Save as SRT, VTT, or ASS. Converting a styled format to a simpler format shows a style-loss notice while preserving text and timing.

The subtitle encoding setting supports UTF-8 and installed Windows code pages. Editor changes are mirrored to mpv through a debounced temporary ASS overlay and are protected by save/discard prompts.

## WebDAV and remote media

Open **Tools → WebDAV browser** to add, edit, or remove servers. Server metadata is stored in settings; passwords are stored separately in Windows Credential Manager. Supported operations include directory listing, parent navigation, refresh, direct remote playback, and saving the current WebDAV folder as a favorite.

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

- **Unsloth Studio**: local OpenAI-compatible endpoint at `http://127.0.0.1:8000/v1/`; API key optional.
- **Google Gemini**: Google Generative Language API, including model-aware [thinking levels/budgets](https://ai.google.dev/gemini-api/docs/generate-content/thinking).
- **Ollama Cloud**: OpenAI-compatible Ollama cloud API.
- **OpenCode Go** and **OpenCode Zen**: OpenAI-compatible provider profiles.

Select a provider in Preferences, store its API key, synchronize the real model list, or enter a model ID manually. Successful model lists are cached for offline fallback. API keys are stored in Windows Credential Manager and are never included in diagnostics. Translation uses stable cue IDs and preserves timing; summarization uses hierarchical chunks so long transcripts do not need to fit in one model request.

## Keyboard shortcuts

| Gesture | Action |
|---|---|
| Space | Play/pause |
| Left / Right | Seek by the configured interval |
| Ctrl+Left / Ctrl+Right | Previous/next subtitle |
| Ctrl+S / Ctrl+Shift+S | Save/Save As |
| Ctrl+Z / Ctrl+Y | Undo/redo |
| Delete | Delete selected cues |
| F11 | Fullscreen |

Text boxes retain ordinary editing shortcuts. Shortcut gestures are persisted as a declarative action-to-gesture map in `settings.json`, so custom configuration UI can be added without changing command routing.

## Settings, data, and diagnostics

Per-user data is stored below `%LOCALAPPDATA%\AIMediaWorker`:

- `settings.json`: atomically replaced application settings.
- `history.json`: recent media and favorites.
- `Waveforms`: source-fingerprint waveform cache.
- `Logs\app.jsonl`: size-rotated structured diagnostics without credentials.

Corrupt settings are preserved as `settings.json.corrupt-*` and replaced with safe defaults. The UI supports English, 한국어, and 日本語 resources plus System/Light/Dark themes. A language change applies to newly created views and fully applies after restarting the application.

**Tools → Diagnostics** reports app, Windows, .NET, Windows App SDK, libmpv, FFmpeg, Python, PyTorch, CUDA, GPU/driver, ASR worker/model, RTX VSR capability, and log location.

## Troubleshooting

- **Playback unavailable**: confirm x64 `mpv-2.dll` and its dependencies are next to the executable. Check Diagnostics for an architecture mismatch.
- **FFmpeg/FFprobe unavailable**: add both executables to `PATH`, restart the app, and inspect Diagnostics.
- **MODEL_NOT_FOUND**: verify the local model directory or a model identifier accessible to the Python environment.
- **CUDA_OOM**: use a lower-precision mode, reduce competing GPU load, or select CPU mode.
- **WebDAV 401/403**: edit the server and re-enter the credential. Only Basic authentication is currently supported by the built-in WebDAV browser.
- **Remote seek unavailable**: the origin must support byte ranges. mpv can continue sequential playback where the protocol permits.
- **Camera or microphone denied**: enable desktop-app access under Windows **Privacy & security** and reopen the capture window.
- **LLM model sync fails**: verify the endpoint/key; select a cached model or enter an exact model ID manually.
- **RTX VSR not visible**: use current NVIDIA drivers, enable video enhancement for AIMediaWorker in NVIDIA App, and verify an RTX adapter in Diagnostics.

Environment-dependent media, GPU, camera, WebDAV, model, and provider checks are listed in [docs/IMPLEMENTATION_STATUS.md](docs/IMPLEMENTATION_STATUS.md).
