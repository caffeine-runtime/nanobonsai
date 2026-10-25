# Third-party notices

nanobonsai's own code is under the [MIT License](LICENSE). The components below keep their own licenses.

| Component | License | Where it is | What a shipped game needs |
| --- | --- | --- | --- |
| [Chromium Embedded Framework](https://bitbucket.org/chromiumembedded/cef) 148.0.10, headers and C++ wrapper | BSD 3-Clause | Compiled into `UnityCefPlugin.dll` / `UnityCefHelper.exe` and the macOS bundle | CEF's `LICENSE.txt` |
| CEF runtime: `libcef.dll` (Chromium, with its third-party components), `chrome_elf.dll`, ANGLE (`libEGL.dll`, `libGLESv2.dll`), SwiftShader and the Vulkan loader, Microsoft's shader compilers (`d3dcompiler_47.dll`, `dxcompiler.dll`, `dxil.dll`), resources and locales | BSD 3-Clause for CEF and Chromium; each component under its own license (BSD, MIT, Apache 2.0, LGPL 2.1 for Blink and FFmpeg, and others), all listed in `CREDITS.html` | Not in this repository: `NativePlugin/fetch-cef-windows.ps1` downloads it from CEF's official builds into `Assets/Plugins/x86_64` | `LICENSE.txt` and `CREDITS.html`, unmodified CEF |
| [MinGW-w64](https://www.mingw-w64.org/) runtime and winpthreads | BSD-style / MIT | Statically linked into the prebuilt Windows binaries | The notices in `Assets/Plugins/x86_64/UnityCefPlugin.NOTICES.txt` |
| GCC runtime libraries (libgcc, libstdc++) | GPL v3 with the GCC Runtime Library Exception | Statically linked into the prebuilt Windows binaries | Nothing: the exception places no conditions on them |
| Unity Native Plugin API headers | [Unity Companion License](https://unity.com/legal/licenses/unity-companion-license) | `NativePlugin/unity/` | Nothing beyond use in a Unity project |
| Unity packages (Input System, uGUI) | Unity Companion License | Fetched by Unity's Package Manager | Nothing beyond use in a Unity project |

`Assets/Editor/CefPlayerPayload.cs` copies `LICENSE.txt`, `CREDITS.html` and `UnityCefPlugin.NOTICES.txt` into every Windows and Mac player, beside the binaries they cover.

The LGPL parts of Chromium sit inside `libcef.dll`, a separate library loaded at run time and built entirely from public sources, so they can be replaced and rebuilt. If you modify CEF or Chromium themselves, publish those changes. CEF's published builds contain no proprietary codecs (H.264, AAC).
