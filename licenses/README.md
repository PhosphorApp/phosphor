# Third-Party Licenses

Phosphor gratefully uses a number of open-source components. Each is included unmodified,
and the full, verbatim license text for components that ask for one is in this folder.

## Emoji fonts

Used to render DMD tile icons (in `Phosphor/Assets/EmojiFonts/`). Attribution is also shown
on the app's About tab. Segoe UI Emoji is not bundled — it is a Microsoft system font
provided by Windows.

| Font file | Set name | License | License text |
|-----------|----------|---------|--------------|
| `TwemojiMozilla.ttf` | Twemoji | Art: **CC-BY 4.0** (Twemoji © Twitter, Inc. and contributors); build: Apache-2.0 (© Mozilla Foundation) | [`fonts/Twemoji-Mozilla-build-LICENSE.txt`](fonts/Twemoji-Mozilla-build-LICENSE.txt) |
| `NotoColorEmoji.ttf` | Noto Color | **SIL Open Font License 1.1** (© Google) | [`fonts/Noto-OFL.txt`](fonts/Noto-OFL.txt) |
| `OpenMoji-color.ttf` | OpenMoji | **CC-BY-SA 4.0** (© OpenMoji contributors) | [`fonts/OpenMoji-CC-BY-SA-4.0.txt`](fonts/OpenMoji-CC-BY-SA-4.0.txt) |
| `FluentEmojiFlat.ttf` | Fluent Flat | **MIT** (webfont build © Tetsunori Nakayama; art © Microsoft, from `microsoft/fluentui-emoji`, MIT) | [`fonts/FluentEmoji-MIT.txt`](fonts/FluentEmoji-MIT.txt) |

- **Twemoji (CC-BY 4.0):** included unmodified, with attribution on the About tab and the full license text here.
- **OpenMoji (CC-BY-SA 4.0):** included unmodified, with attribution on the About tab and the full license text here.
- **Noto Color (SIL OFL 1.1):** included unmodified, with the full license text here.
- **Fluent Flat (MIT):** included unmodified, with the copyright notice and full license text here.

## Bundled native components (`dependencies/`)

| Component | Purpose | License | License text |
|-----------|---------|---------|--------------|
| projectM (`projectM-4*.dll`) | Music visualization engine | **LGPL-2.1** | [`native/projectM-LGPL-2.1.txt`](native/projectM-LGPL-2.1.txt) |
| GLEW (`glew32.dll`) | OpenGL extension loader (used by projectM) | **BSD / MIT** | [`native/glew-BSD-MIT.txt`](native/glew-BSD-MIT.txt) |
| LibVLC | Media playback engine (via `VideoLAN.LibVLC.Windows`) | **LGPL-2.1** | [`native/LibVLC-LGPL-2.1.txt`](native/LibVLC-LGPL-2.1.txt) |
| FFmpeg (`ffmpeg.exe`) | Remuxing downloaded streams and thumbnail extraction | **GPLv3** | [`native/ffmpeg-GPLv3.txt`](native/ffmpeg-GPLv3.txt), [`native/ffmpeg-SOURCE-OFFER.txt`](native/ffmpeg-SOURCE-OFFER.txt) |

- **projectM / LibVLC (LGPL-2.1):** included unmodified as dynamically-loaded libraries; each
  ships as a separate file that can be replaced. Full license texts are here.
- **GLEW (BSD/MIT):** included unmodified, with the copyright notice and license text here.
- **FFmpeg (GPLv3):** invoked as a separate command-line program (not linked into Phosphor),
  used only for stream-copy remuxing and thumbnail frames. Included unmodified, with the GPLv3
  text and a corresponding-source offer here.

## NuGet packages

Managed libraries that request a notice on redistribution:

| Package | License | License text |
|---------|---------|--------------|
| LibVLCSharp | **LGPL-2.1** | [`packages/LibVLCSharp-LGPL-2.1.txt`](packages/LibVLCSharp-LGPL-2.1.txt) |
| TagLibSharp | **LGPL-2.1** | [`packages/TagLibSharp-LGPL-2.1.txt`](packages/TagLibSharp-LGPL-2.1.txt) |
| YoutubeExplode | **MIT** | [`packages/YoutubeExplode-MIT.txt`](packages/YoutubeExplode-MIT.txt) |
