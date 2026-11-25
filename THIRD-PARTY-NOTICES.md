# Third-party notices

Axis SD Card Reader (the "Software") is licensed under the MIT License — see [LICENSE](LICENSE).

The Software uses and/or redistributes the third-party components listed below. Each remains under its own
license and copyright. The relevant license texts are reproduced or linked below, and the release package
carries the full texts of the LGPL-2.1, GPL-3.0 (which the LGPL-3.0 builds on) and Apache-2.0 licences in
its `licenses` folder, next to FFmpeg's own `FFMPEG-LICENSE.txt`.

| Component | Version | License | Copyright |
|-----------|---------|---------|-----------|
| [libVLC](https://www.videolan.org/vlc/libvlc.html) (native, via `VideoLAN.LibVLC.Windows`) | 3.0.24 | LGPL-2.1-or-later | © the VideoLAN project and contributors |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) / LibVLCSharp.WPF | 3.10.2 | LGPL-2.1-or-later | © the VideoLAN project and contributors |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | © .NET Foundation and Contributors |
| [DiscUtils](https://github.com/LTRData/DiscUtils) (`LTRData.DiscUtils.Core`, `.Streams`, `LTRData.Extensions`) | 1.0.91 | MIT | © Kenneth Bell, Olof Lagerkvist / LTR Data, and contributors |
| [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe`, **bundled**) | n8.1.3 (LGPL build) | LGPL-3.0-or-later | © the FFmpeg project and contributors |
| [ModelContextProtocol C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) (`ModelContextProtocol`, `.Core`; in `AxisSdReader.Mcp.exe`) | 2.4.0 | Apache-2.0 | © Model Context Protocol a Series of LF Projects, LLC. |
| .NET runtime and Windows Desktop runtime (`Microsoft.NETCore.App`, `Microsoft.WindowsDesktop.App`; **bundled**, self-contained) | 10.0 | MIT | © .NET Foundation and Contributors |
| Microsoft.Extensions.AI.Abstractions, Microsoft.Extensions.\*.Abstractions, Microsoft.Extensions.Options / Primitives, System.Text.Json, System.IO.Pipelines, System.Net.ServerSentEvents, System.Diagnostics.DiagnosticSource (dependencies of the SDK, in `AxisSdReader.Mcp.exe`) | 10.x | MIT | © .NET Foundation and Contributors |

## FFmpeg (bundled)

This distribution **includes** an unmodified `ffmpeg.exe`, used to remux/trim exports (`-c copy`) and, by the MCP
server, to decode still frames — always by running it as a **separate process**. The application never links
against, or statically incorporates, any FFmpeg code — it only executes the binary, so the two are merely
aggregated.

- **Build:** `ffmpeg-n8.1.3-39-g660b5aa7db-win64-lgpl-8.1` (2026-11-23) from
  [FFmpeg-Builds](https://github.com/FFmpeg-Builds/FFmpeg-Builds) (BtbN's builds, one of the Windows builders
  linked from ffmpeg.org's download page), a 64-bit static build.
- **Licence: LGPL-3.0-or-later.** The build is configured **without** `--enable-gpl` and with
  `--disable-libx264 --disable-libx265`, so no GPL-licensed components are present; `--enable-version3`
  makes it LGPL **v3**. The full licence text ships alongside the binary as `FFMPEG-LICENSE.txt`.
- **Corresponding source:** FFmpeg's source is at <https://github.com/FFmpeg/FFmpeg> (release branch
  `release/8.1`, commit `660b5aa7db`), and the exact build recipe/scripts used to produce this binary are at
  <https://github.com/FFmpeg-Builds/FFmpeg-Builds>. No modifications were made by this project.
- **Replacing it:** FFmpeg is invoked as a standalone executable, so you may substitute your own
  `ffmpeg.exe` — place it in an `ffmpeg` folder next to the application and it will be used instead.

## LGPL-2.1 components (libVLC, LibVLCSharp)

The libVLC native libraries and the LibVLCSharp / LibVLCSharp.WPF managed wrappers are licensed under the
**GNU Lesser General Public License, version 2.1 or later (LGPL-2.1-or-later)**. They are used **unmodified**
and are loaded dynamically (the native libVLC DLLs ship alongside the application and can be replaced by a
compatible build to relink). The full text of the LGPL-2.1 is available at:

- https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt

Note on VLC plugins: this distribution ships a **reduced** libVLC plugin set — only what playing Matroska
chunks from memory needs (the allow-list is in `src/AxisSdReader.App/AxisSdReader.App.csproj`). The
GPL-licensed x264 encoder and the network, capture, disc, streaming-out and Lua modules are not included.
libVLC core and the great majority of its plugins are LGPL-2.1-or-later, but a few optional plugins can be
GPL-licensed depending on the build; if you redistribute this package where the GPL would be a concern,
review the bundled `libvlc/**/plugins` set, or comply with the GPL for the whole. Corresponding source for
libVLC is at <https://code.videolan.org/videolan/vlc> (tag `3.0.24`), and the NuGet packaging at
<https://code.videolan.org/videolan/libvlc-nuget>.

## .NET runtime (bundled)

The package is self-contained: it includes the .NET and Windows Desktop runtimes (MIT, © .NET Foundation and
Contributors). Their own third-party notices are published with their sources:
<https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT>,
<https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT> and
<https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT>.

`AxisSdReader.exe`, the launcher, is this project's own code (MIT), built with the Microsoft Visual C++ toolset
and its static C runtime.

## Apache-2.0 component (ModelContextProtocol C# SDK)

The ModelContextProtocol and ModelContextProtocol.Core libraries, compiled into `AxisSdReader.Mcp.exe`, are
licensed under the **Apache License, Version 2.0**, and used unmodified. The licence text is at
<https://www.apache.org/licenses/LICENSE-2.0> (plain text: <https://www.apache.org/licenses/LICENSE-2.0.txt>).
Source: <https://github.com/modelcontextprotocol/csharp-sdk>.

## MIT components (CommunityToolkit.Mvvm, DiscUtils, and the MCP SDK's .NET dependencies)

The following components are licensed under the MIT License, reproduced here in full (this text applies to the
CommunityToolkit.Mvvm, DiscUtils / LTRData.Extensions and Microsoft.Extensions / System.\* components listed
above, each under its own copyright):

```
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
