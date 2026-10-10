# Development scripts

Maintenance helpers that are not part of the build. Nothing here is needed to build or test the libraries.

| Script | Purpose | Needs |
|---|---|---|
| `compare_trace.py` | Walks PRC payloads with `scene3d-tool trace` and with prc-rs's `prc-describe` and compares every entity and bit position (`--all` for a whole corpus; writes `oracle.md`/`oracle.json` into the current folder) | Python 3; `PRC_DESCRIBE` = a `prc-describe` built from prc-rs commit `5595499`; `SCENE3D_PRC_CORPUS` = folders of `.prc` payloads; a built `scene3d-tool` |
| `prc-double-table.py` | Regenerates `PrcDoubleTable.g.cs` from prc-rs's `src/double.rs` | a prc-rs checkout (MIT) |
| `prc-type-table.py` | Regenerates `PrcType.g.cs` from prc-rs's `src/constants.rs` | a prc-rs checkout (MIT) |
| `U3dBlocks.ps1` | Block census of raw `.u3d` files (header fields and a histogram of block types) | PowerShell 7 |
| `gltf-validate/` | Runs the Khronos glTF-Validator over the `.glb` files `scene3d-tool glb` writes (`npm install` once, then `node validate.mjs <folder>`) | Node.js |
| `read-issue.py` | Prints a saved PDF Association PRC issue through the clean-room filter (lines naming nanoPRC or the patent are dropped, never shown) | issues saved under `PRC_ISSUES_DIR` (default `tools/scripts/issues`, git-ignored) |

The entity readers of `Caly.Rendering.Scene3D.Prc/Generated` are not written by these scripts but by
`tools/Caly.Rendering.Scene3D.Prc.Generator` (see the README).

## What is deliberately not in the repository

- The standards (ISO 14739-1 and its 2009 working draft, ISO 32000-2, ECMA-363): obtain them yourself. Their text
  was only read; the decoders are written from it.
- Sample corpora: real 3D payloads of unclear redistribution terms (vendor samples, the 310 payloads of
  ralovich/prc-db, the 375 files of ralovich/u3d-db). Tests that use them read their folders from environment
  variables (`SCENE3D_PRC_CORPUS`, `SCENE3D_PRC_CORPUS_EXTRA`, `SCENE3D_U3D_CORPUS`) and do nothing without them;
  everything else, including the 54 U3D sample scenes with their IDTF sources and the PRC bit-reader vectors, is
  in `Caly.Rendering.Scene3D.Tests/TestData`.
- Third-party code: prc-rs (MIT) and the Universal 3D Sample Software (Apache-2.0) are read from your own clones
  (`THIRD-PARTY-NOTICES.md`). nanoPRC is never opened (`docs/clean-room-log.md`).
