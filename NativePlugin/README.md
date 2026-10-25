# Native CEF bridge

C++ sources of the `UnityCefPlugin` bridge and the `UnityCefHelper` subprocess used by `Assets/HtmlUi/HtmlUiView.cs`, for Windows x64 and Apple Silicon macOS, built against CEF 148.0.10 (Chromium 148.0.7778.218).

| File | Role |
| --- | --- |
| `UnityCefPlugin.cpp` | The bridge: CEF's lifetime, offscreen browsers, input, the page-to-C# console channel and the frame hand-off. |
| `UnityCefHelper.cpp` | The executable CEF runs its renderer, GPU and utility processes from. |
| `UnityCefMac.mm` | macOS only: loads the framework, adds `CefAppProtocol` to Unity's `NSApplication`, translates key events. |
| `unity/` | Unity's native plugin API headers (Unity Companion License) for the D3D12 hand-off. |
| `fetch-cef-windows.ps1` | Downloads the pinned CEF runtime into `Assets/Plugins/x86_64`. |
| `build-cef-mac.sh` | Builds the macOS payload on an Apple Silicon Mac. |

## How it works

- **Frames.** Under Unity's Direct3D 12 each frame arrives as a shared GPU texture on Unity's adapter. CEF's thread copies it into one of three frames the bridge owns, and Unity's render thread copies the newest into the view's texture (`CommandBuffer.IssuePluginEventAndData`). Elsewhere (Direct3D 11, macOS) frames come as CPU buffers that `HtmlUiView` copies straight into the texture's memory. If the GPU hand-off fails, the page reopens on the CPU path.
- **Frame rate.** Pages paint at the display's refresh within 120-144 FPS (`SetFrameRate`, set by `HtmlUiView` before each browser).
- **Messages.** Page console lines prefixed `unity:` reach C# through a callback; C# runs script in the page through `ExecuteJS`.
- **Processes.** Each process gets its own CEF root under `%TEMP%\UnityCef\<pid>` (`$TMPDIR` on macOS), so game instances running side by side do not contend for CEF's lock. The root holds the session's cache and storage, with CEF's log beside it as `<pid>.log`. `ShutdownCEF` deletes what Chromium no longer holds open. Every start removes the roots and logs of processes that are no longer running, which also covers the Editor (it never shuts CEF down) and crashes.
- **Launch.** Chromium's automatic de-elevation relaunch is disabled, since Unity owns the process and its arguments. On macOS the mock keychain keeps Chromium from asking the player for keychain access, and CEF runs on Unity's main thread, pumped once per frame (`PumpCEF`).

## Windows

The repository carries prebuilt `UnityCefPlugin.dll` and `UnityCefHelper.exe` in `Assets/Plugins/x86_64`; `fetch-cef-windows.ps1` adds the CEF runtime beside them. To rebuild them, unpack the CEF 148.0.10 Windows x64 minimal distribution from [CEF's builds](https://cef-builds.spotifycdn.com/index.html) and configure CMake with `CEF_ROOT`. The outputs are written to `Assets/Plugins/x86_64`; close Unity first, since it holds the loaded DLL.

With Visual Studio (CMake's standard path, not yet tested; the prebuilt binaries come from the MinGW build below):

```powershell
cmake -S NativePlugin -B NativePlugin/build -DCEF_ROOT="C:/path/to/cef_binary_148.0.10+g7ee53f5+chromium-148.0.7778.218_windows64_minimal"
cmake --build NativePlugin/build --config Release
```

With MinGW-w64 (for example a [WinLibs](https://winlibs.com/) UCRT build) and Ninja, no Visual Studio needed:

```powershell
$env:PATH = "C:\path\to\mingw64\bin;$env:PATH"
cmake -S NativePlugin -B NativePlugin/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DCEF_ROOT="C:/path/to/cef_binary_148.0.10+g7ee53f5+chromium-148.0.7778.218_windows64_minimal"
cmake --build NativePlugin/build
```

The prebuilt binaries were built this way, with GCC 16.2 and MinGW-w64 14, statically linked, so they need no MinGW DLLs. The MinGW-w64 runtime's notices ship beside them in `UnityCefPlugin.NOTICES.txt`.

## macOS (Apple Silicon, experimental)

On an arm64 Mac with Xcode's Command Line Tools and CMake, `bash NativePlugin/build-cef-mac.sh` downloads the same CEF version for macOS arm64 (SHA-1 pinned), builds `UnityCefPlugin.bundle` and the helper, and gathers them with the framework and CEF's license files into `NativePlugin/mac`. It also zips them as `NativePlugin/cef-mac.zip`, to carry to another machine.

A Unity Mac build picks the payload up through `Assets/Editor/CefPlayerPayload.cs`. The bundle goes into `Contents/PlugIns`, and the framework and one helper app per subprocess kind (`UnityCefHelper`, `UnityCefHelper (GPU)`, `(Renderer)`, `(Plugin)`, `(Alerts)`) into `Contents/Frameworks`. Sign the finished app before running it, for example ad hoc with `codesign --force --deep --sign - <App>.app`. The Mac Editor has no CEF; only the Mac player does.
