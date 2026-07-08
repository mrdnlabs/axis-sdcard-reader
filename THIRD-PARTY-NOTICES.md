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
| [FFmpeg](https://ffmpeg.org) (optional, **not** distributed — see note) | user-supplied | LGPL-2.1-or-later / GPL (build-dependent) | © the FFmpeg project and contributors |

## FFmpeg (optional, not bundled)

FFmpeg is **not** included in this distribution. Trimmed MP4/MKV export is an optional feature that runs a
separately-installed `ffmpeg.exe` as an external process (the app never links FFmpeg code). If you choose to
place an `ffmpeg.exe` next to the app, that binary remains under its own license (LGPL-2.1-or-later or GPL,
depending on the build you obtained); this project neither includes nor relicenses it.

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
