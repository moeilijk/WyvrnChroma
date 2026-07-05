# WyvrnChroma

A small, standalone .NET library to read and play **Razer ChromaAnimation (`.chroma`)** effects and the
**Wyvrn haptic-folder catalog** that event-driven games (e.g. *007: First Light*) use for their per-event
RGB lighting — **without Synapse, without the Razer Chroma services, and without any cloud dependency at
runtime**.

It exists so [Aurora](https://github.com/Aurora-RGB/Aurora) (issue #292) can react to `SetEventName` games
on non-Razer hardware: Aurora reads the live event stream, looks up the event in the local `wyvrn.config`,
and plays the matching `.chroma` itself.

## What it does
- **`.chroma` parser** (`ChromaAnimation`): decodes the ChromaAnimation file format into frames of per-LED
  colours. Pure managed code, no Razer DLLs.
- **animation player** (`ChromaPlayer`): samples the current frame at time *t* (loop vs one-shot).
- **catalog acquisition** (`HapticCatalogProvider`): local Synapse/Chroma/Wyvrn install first, else Razer's
  public CDN (manifest → sha256-verified ZIP), cached offline.
- **`wyvrn.config` mapping** (`WyvrnConfig`): event id → per-device `.chroma` effect (+ loop/priority semantics).
- **public API** (`HapticCatalog` / `HapticGame`): `GetEffectForEvent(game, event, device)` → the parsed
  animation ready to play.

## Status
- Phase A (this library) complete: parser, player, catalog acquisition, `wyvrn.config`, public API — all tested.
- Phase B (the Aurora integration that consumes this library) lives in the Aurora fork (issue #292).

## Build & test
```bash
dotnet build -c Release
dotnet test  -c Release
```
Targets `net10.0` (matches the Aurora consumer; cross-platform — no Windows APIs).

## Notes
This library only **parses/plays data files**; it does not bundle or redistribute any Razer content. The
catalog of `.chroma`/`wyvrn.config` files is Razer/Wyvrn content, obtained at the user's machine either from
an existing local install or from Razer's own public CDN.
