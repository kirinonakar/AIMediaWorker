# AIMediaWorker Implementation Status

Last updated: 2026-08-23

## Completed

- Repository baseline inspection and correction of the solution configuration so Debug builds produce Debug output.
- WinUI 3 responsive application shell with a native child HWND video surface.
- `libmpv` lifecycle, command marshalling, event loop, local/HTTP/HLS/DASH opening, play/pause/stop/seek, volume, mute, rate, frame stepping, A/B loop API, media-end notification, hardware decoder selection, decoder diagnostics, track enumeration and track selection.
- Graceful `libmpv` missing/architecture mismatch handling.
- External/embedded subtitle playback through mpv/libass and fuzzy sidecar subtitle discovery through mpv.
- SRT, WebVTT and ASS/SSA document parsing/writing with integer-microsecond canonical timestamps.
- Subtitle document dirty tracking, add/delete/text edit/split/merge/move/batch shift, incremental Undo/Redo and save/save-as conversion.
- Edited-document overlay synchronization through a debounced temporary ASS track and `sub-reload`.
- Playback/cue highlight synchronization and virtualized WinUI `ListView` editor.
- Timeline visible-range rendering, click seek, cue move, start/end resize, pan, zoom and playhead/cue-boundary snapping.
- WinUI3 in-process CrispASR C ABI lifecycle, native session management, cancellation, restart support and failure reporting without a helper executable.
- CrispASR integration for Qwen3-ASR-1.7B and qwen3-forced-aligner-0.6B, fixed GGUF model paths, FFmpeg chunk extraction, native amplitude gating, global offset restoration, incremental segments and bounded long-media processing.
- Configurable punctuation/silence/duration/line-length subtitle segmentation.
- Live PCM buffering with partial/final events and a low-latency path separate from offline forced alignment.
- HTTP media playback and cancellable WebDAV PROPFIND browser with add/edit/delete servers, parent/refresh/open and authenticated direct mpv playback.
- Windows Credential Manager storage for WebDAV and LLM secrets; no secrets in settings JSON.
- Recent local/HTTP/WebDAV media, playback-position resume and media favorites persistence.
- Camera/microphone enumeration, Media Foundation camera preview, MP4 camera capture, WASAPI microphone capture, bounded buffering, 16 kHz mono resampling, live ASR and live caption overlay.
- Current-frame PNG capture.
- LLM abstraction and capability model with Google Gemini, Ollama Cloud, OpenCode Go, OpenCode Zen and Unsloth Studio HTTP providers.
- ID-mapped chunked subtitle translation and hierarchical short/detailed summarization with progress and cancellation.
- JSON settings with atomic replacement, corruption recovery, separate Preferences UI, System/Light/Dark theme selection and core shortcut handling.
- Data-loss confirmation on close, media replacement and subtitle replacement.
- Unit/integration tests for subtitle formats/editing, timeline transforms, settings recovery, recent media, credential identifiers, WebDAV URI handling, ASR protocol/worker lifecycle and LLM mapping/hierarchical summarization.

## Completed in Final Integration

- English/Korean/Japanese resources now cover the main shell, Preferences, WebDAV, camera, and user-facing dynamic workflows. Language qualifiers are applied before window construction and whenever settings are saved.
- Persistent declarative shortcut mapping, including `Ctrl+Shift+S`, exact modifier matching, and TextBox focus protection.
- Favorites browsing/removal, local folder favorites, WebDAV folder favorites, and re-entry into saved WebDAV directories.
- Provider model synchronization with cached-list and manual-ID fallback.
- Full diagnostics probes and structured rotating logs that exclude credentials.
- RTX-class GPU detection, unsupported-control state, D3D11 path, mpv NVIDIA RTX VSR filter configuration, driver-controlled activation disclosure, and normal scaler fallback.
- Camera resolution/frame-rate selection and configurable live-caption font, size, colors, position, and line count.
- Authenticated remote media preparation for ASR, direct unauthenticated HTTP ASR, network timeout/reconnect behavior, and cancellable FFmpeg extraction.
- Subtitle encoding selection and explicit style-loss status when converting formats.
- C# native runtime/path/protocol regression tests, missing-model recovery tests and shortcut/protocol regression tests.
- Complete setup/usage/troubleshooting README.
- Self-contained Windows App SDK deployment so the unpackaged executable starts without a separately registered Windows App Runtime.
- Drag-and-drop and multi-file playlist opening, sibling-folder playlists, previous/next, repeat modes, deferred exact seeking, subtitle visibility toggle, and command-line autoplay.
- Enter fullscreen toggle, Escape restore, frame-free fullscreen, edge-hover menu/controls/side panel, and independently hideable timeline and side panels.
- The fullscreen splitter rows collapse completely, shortcut hints are exposed in menus/tooltips, Ctrl+W follows the guarded close workflow, and Preferences are grouped into seven focused tabs.
- libmpv startup initialization runs off the UI thread; the subtitle timeline follows playback with an overlaid red playhead.
- Fullscreen targets the window's current display, repairs its presenter/chrome after multi-monitor topology or display moves, and uses virtual-screen coordinates plus an expanded shared-edge hover zone for the right overlay.
- First-open work uses asynchronous libmpv commands, deferred history/Explorer synchronization, and bounded folder enumeration; the unpackaged title bar explicitly loads the shipped application icon.
- Main side-panel Explorer, Playlist, WebDAV, and Subtitles tabs; connected WebDAV listings mirror into the main panel while server registration remains available in the WebDAV manager.
- Explorer and WebDAV name filtering plus cyclic name/newest/oldest sorting with state-specific icons.
- Persistent main-window position, size, maximized state, panel visibility, and user-resizable side/timeline panel dimensions with current-display bounds correction.
- Unpackaged secondary-window localization crash fixed for WebDAV, Preferences, and Camera windows; all three activate together in the runtime smoke check without a new error log.

## Remaining Environment Validation

- Manual workflows with real libmpv, a multi-track MKV fixture, NVIDIA RTX hardware/driver enhancement, Qwen weights/CUDA, authenticated WebDAV range seeking, camera/microphone devices, and live provider credentials cannot be executed on the current machine.
- These are environment-dependent acceptance checks rather than placeholder implementations; capability detection, failure reporting, fallback, cancellation, and cleanup paths are implemented.

## Known Issues

- The current machine has a verified x64 `mpv-2.dll` and passes local generated-video playback startup. Qwen model weights, CUDA, a WebDAV test server, provider API keys and known camera/microphone fixtures still require environment-dependent validation.
- The WinUI C# client uses bounded rolling-window transcription for live partial captions and serialized CrispASR session calls; offline results additionally use the CrispASR forced-aligner ABI.
- RTX Video Super Resolution activation is driver/profile controlled for the D3D11 video path; the application now submits mpv's `d3d11vpp` NVIDIA scaling request, but cannot independently prove the driver's per-frame enhancement state.
- A language change fully applies after application restart; newly opened views use the updated resource qualifier immediately.

## Architecture Decisions

- WinUI does not import Python or start a helper process; C# loads the prebuilt CrispASR DLL from `asr-worker/crispasr` and calls its C ABI directly.
- `libmpv` owns decode, A/V sync, GPU rendering and normal subtitle rendering in a native child HWND.
- Subtitle times are integer microseconds and only convert to floating-point seconds at external process/API boundaries.
- Long audio is extracted through bounded FFmpeg chunks; live audio is kept in a bounded rolling buffer and offline chunks are released after inference.
- Secrets use Windows Credential Manager and are only attached to in-memory HTTP/mpv requests.
- The test project references a UI-free linked core assembly so unit tests do not require Windows App SDK activation.

## Environment-dependent Tests

- `libmpv` initialization, D3D11VA/NVDEC fallback, track switching and media matrix.
- NVIDIA GPU/driver RTX VSR activation.
- Qwen3-ASR and ForcedAligner inference through the CrispASR C ABI with real CUDA/model weights.
- WebDAV authentication/range-seek behavior against real servers.
- Camera preview/capture, microphone removal/permission-denial and live-caption latency.
- Google/Ollama Cloud/OpenCode/Unsloth model listing, translation and summarization with real credentials.

## Final Validation Checklist

- Native runtime path, protocol lifecycle, missing-model recovery, and subtitle timing tests pass.
- All .NET unit/integration tests pass, including the native runtime lifecycle when the prebuilt DLL is available.
- Debug and Release solution builds pass with no errors; warnings are tracked in the normal build output.
- The self-contained unpackaged Release executable creates a visible top-level `AIMediaWorker` window during its launch smoke test; the exact smoke-test process is then stopped cleanly.
- Real-hardware/service workflows remain listed above for the target deployment environment.
