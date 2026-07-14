# Third-party notices

Axis SD Card Reader (the "Software") is licensed under the MIT License — see [LICENSE](LICENSE).

The Software uses and/or redistributes the third-party components listed below. Each remains under its own
license and copyright. The relevant license texts are reproduced or linked below.

| Component | Version | License | Copyright |
|-----------|---------|---------|-----------|
| [libVLC](https://www.videolan.org/vlc/libvlc.html) (native, via `VideoLAN.LibVLC.Windows`) | 3.0.23.1 | LGPL-2.1-or-later | © the VideoLAN project and contributors |
| [LibVLCSharp](https://github.com/videolan/libvlcsharp) / LibVLCSharp.WPF | 3.10.0 | LGPL-2.1-or-later | © the VideoLAN project and contributors |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT | © .NET Foundation and Contributors |
| [DiscUtils](https://github.com/LTRData/DiscUtils) (`LTRData.DiscUtils.Core`, `.Ext`, `LTRData.Extensions`) | 1.0.85 | MIT | © Kenneth Bell, Olof Lagerkvist / LTR Data, and contributors |
| [FFmpeg](https://ffmpeg.org) (`ffmpeg.exe`, **bundled**) | n7.1 (LGPL build) | LGPL-3.0-or-later | © the FFmpeg project and contributors |

## FFmpeg (bundled)

This distribution **includes** an unmodified `ffmpeg.exe`, used only to remux/trim exports by running it as a
**separate process** (`-c copy`). The application never links against, or statically incorporates, any FFmpeg
code — it only executes the binary, so the two are merely aggregated.

- **Build:** `ffmpeg-n7.1-latest-win64-lgpl` from
  [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) (one of the Windows builders linked from
  ffmpeg.org's download page), a 64-bit static build.
- **Licence: LGPL-3.0-or-later.** The build is configured **without** `--enable-gpl` and with
  `--disable-libx264 --disable-libx265`, so no GPL-licensed components are present; `--enable-version3`
  makes it LGPL **v3**. The full licence text ships alongside the binary as `FFMPEG-LICENSE.txt`.
- **Corresponding source:** FFmpeg's source is at <https://github.com/FFmpeg/FFmpeg> (release branch
  `release/7.1`), and the exact build recipe/scripts used to produce this binary are at
  <https://github.com/BtbN/FFmpeg-Builds>. No modifications were made by this project.
- **Replacing it:** FFmpeg is invoked as a standalone executable, so you may substitute your own
  `ffmpeg.exe` — place it in an `ffmpeg` folder next to the application and it will be used instead.

## LGPL-2.1 components (libVLC, LibVLCSharp)

The libVLC native libraries and the LibVLCSharp / LibVLCSharp.WPF managed wrappers are licensed under the
**GNU Lesser General Public License, version 2.1 or later (LGPL-2.1-or-later)**. They are used **unmodified**
and are loaded dynamically (the native libVLC DLLs ship alongside the application and can be replaced by a
compatible build to relink). The full text of the LGPL-2.1 is available at:

- https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html
- https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt

Note on VLC plugins: this distribution ships the standard libVLC plugin set. libVLC core and the great majority
of its plugins are LGPL-2.1-or-later, but a small number of optional plugins can be GPL-licensed depending on
the build. If you intend to redistribute this package in a context where the GPL would be a concern, review the
bundled `libvlc/**/plugins` set and remove any GPL-only plugin, or comply with the GPL for the whole.

## MIT components (CommunityToolkit.Mvvm, DiscUtils)

The following components are licensed under the MIT License, reproduced here in full (this text applies to the
CommunityToolkit.Mvvm and DiscUtils / LTRData.Extensions components listed above, each under its own copyright):

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
