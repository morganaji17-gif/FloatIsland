# Third-party notices

Sono Island bundles or depends on the following third-party material. Each item
retains its own license and copyright.

## Plus Jakarta Sans

- Files: `Assets/Fonts/*.ttf`
- Author: Tokotype
- License: SIL Open Font License 1.1
- Full text: [`Assets/Fonts/OFL-PlusJakartaSans.txt`](../Assets/Fonts/OFL-PlusJakartaSans.txt)

The font is redistributed unmodified under its own name, as the OFL requires.

## SkiaSharp

- Used by: `Overlay/*` (the Skia renderer)
- Author: Microsoft / Mono Project
- License: MIT
- https://github.com/mono/SkiaSharp

The native binaries are provided by `SkiaSharp.NativeAssets.Win32`.

## .NET and Windows App SDK

- Used by: the whole application
- License: MIT
- https://github.com/dotnet/runtime

## Data sources

- **LRCLIB** (https://lrclib.net) — lyrics lookup at runtime. Not bundled; the app
  sends the track title/artist/album/duration as a search query. No account, no API key.

## No bundled app assets

The application contains no artwork, icons, fonts or code copied from any other
music player. The visual design, layout constants and drawing code in this
repository are original to this project.
