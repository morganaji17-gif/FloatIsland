# Contributing

Thanks for taking a look. This is a small Windows-only desktop app; the whole
thing is a few thousand lines of C#.

## Prerequisites

- Windows 11 (build 20348 or newer is required by the audio capture path)
- .NET 10 SDK
- Spotify Desktop, if you want to test the real behaviour

## Build and run

```powershell
dotnet build -c Release -p:Platform=x64
.\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FloatIsland.exe
```

> If your antivirus blocks the freshly built exe, run the assembly through the runtime:
> `dotnet .\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FloatIsland.dll`.

## Before you open a pull request

Run both of these; CI runs the same commands:

```powershell
.\bin\...\FloatIsland.exe --selftest     # pure-logic assertions, exits non-zero on failure
.\bin\...\FloatIsland.exe --skia-shot    # renders every surface to PNG in .\shots\
```

`--skia-shot` is the render smoke test: it draws each island surface (collapsed, player,
lyrics, menu, settings, volume) offscreen and logs the geometry it measured. If you change
layout, check the numbers it prints.

## Guidelines

- Keep it Windows-only and dependency-light. Prefer the BCL and what is already
  referenced over adding a new NuGet package.
- No polling for metadata: SMTC is event driven.
- Leave one runnable check behind for non-trivial logic (extend `--selftest`).
- Match the surrounding style: file-scoped namespaces, nullable enabled, comments
  explain *why*, not *what*.

## Releasing

Tag `vX.Y.Z`; the release workflow publishes a framework-dependent `win-x64` zip
and attaches it to the GitHub release.

## Legal

Do not add assets, artwork, fonts or code copied from another application. Only
add material you have the right to redistribute, and record it in
`THIRD_PARTY_NOTICES.md`.
