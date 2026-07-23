# Bundled Font Licenses

Phosphor bundles several open-license emoji fonts (in `Phosphor/Assets/EmojiFonts/`)
used to render DMD tile icons. Each font is redistributed **unmodified** under its
respective license. The full, verbatim license texts are in this folder.

Segoe UI Emoji is **not** bundled — it is a Microsoft system font loaded from the host
operating system, so no redistribution license applies.

| Font file | Set name | License | License text |
|-----------|----------|---------|--------------|
| `TwemojiMozilla.ttf` | Twemoji | Art: **CC-BY 4.0** (Twemoji © Twitter, Inc. and contributors); build: Apache-2.0 (© Mozilla Foundation) | [`fonts/Twemoji-Mozilla-build-LICENSE.txt`](fonts/Twemoji-Mozilla-build-LICENSE.txt) |
| `NotoColorEmoji.ttf` | Noto Color | **SIL Open Font License 1.1** (© Google) | [`fonts/Noto-OFL.txt`](fonts/Noto-OFL.txt) |
| `OpenMoji-color.ttf` | OpenMoji | **CC-BY-SA 4.0** (© OpenMoji contributors) | [`fonts/OpenMoji-CC-BY-SA-4.0.txt`](fonts/OpenMoji-CC-BY-SA-4.0.txt) |
| `FluentEmojiFlat.ttf` | Fluent Flat | **MIT** (webfont build © Tetsunori Nakayama; art © Microsoft, from `microsoft/fluentui-emoji`, MIT) | [`fonts/FluentEmoji-MIT.txt`](fonts/FluentEmoji-MIT.txt) |

## Notes

- **CC-BY 4.0 / CC-BY-SA 4.0 (Twemoji, OpenMoji):** require attribution, which is
  provided on the app's About tab and here.
- **CC-BY-SA 4.0 (OpenMoji):** the "ShareAlike" obligation applies only to *modified/adapted*
  versions of the OpenMoji font that are then redistributed. Phosphor ships OpenMoji
  unmodified, so ShareAlike does not apply to Phosphor or its source code.
- **SIL OFL 1.1 (Noto):** the font may be bundled and redistributed freely; it may not be sold
  on its own, and modified versions may not use the reserved font name.
- **MIT (Fluent):** permissive; requires the copyright notice and license text (included here).

None of these licenses restrict Phosphor's own licensing, distribution, or monetization —
they govern only the font files themselves.
