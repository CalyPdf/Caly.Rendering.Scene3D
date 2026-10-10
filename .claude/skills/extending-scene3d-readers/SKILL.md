---
name: extending-scene3d-readers
description: Use when adding or fixing a feature in external/Scene3D — U3D or PRC reader, Scene model, SceneLimits, GLB writer, generated PRC entity readers, animations/skeletons, a new 3D format, or a decoder that reads a model wrongly or crashes on hostile input.
---

# Extending the Scene3D readers

`external/Scene3D` = `Caly.Rendering.Scene3D` (+ `.U3D`, `.Prc`, `.Tests`, `.Benchmarks`, `tools/`). Net8.0/net10.0, BCL only, `TreatWarningsAsErrors`, strong-named. **REQUIRED:** `clean-room-3d-sources` before reading any reference. Its README is the as-built description; keep it true.

## Process

1. **Ground truth first.** U3D: the 54 sample scenes with their IDTF text (`Caly.Rendering.Scene3D.Tests/TestData/TestScenes`) — parse independently and compare every node/mesh/set. PRC: `scene3d-tool trace` against prc-rs's `prc-describe` (`python tools/scripts/compare_trace.py --all`, 38 of 38 identical; needs `PRC_DESCRIBE` and `SCENE3D_PRC_CORPUS`, see `tools/scripts/README.md`). Real files: corpora (`verifying-3d-changes`). When the standard and real files disagree, **files win** (as does the reference implementation for U3D); document the case where it is handled.
2. **Test first** (xUnit 2: no runtime skip, corpus tests pass vacuously without their env var — check they really ran).
3. **Performance-first**: spans, pooled buffers, nothing allocated per vertex/face, no LINQ/boxing on hot paths; measure with BenchmarkDotNet in Release; update the README tables only from measurements.
4. **Hostile input** (files come from untrusted PDFs): check every count against what remains before allocating; respect `SceneLimits`; honour the `CancellationToken`; bad input surfaces only as `Scene3DException` (`Truncated`, `InvalidValue`, `LimitExceeded`, `Unsupported`). Add the case to the fuzz tests (`SCENE3D_FUZZ_MUTATIONS`).
5. **Diagnostics, not silence**: what is skipped or approximated goes to `Scene.Diagnostics` with a stable code (`animation-skipped`, `prc-exact-geometry-only`...). A model is shown whole or flagged: partial detail never passes as complete.
6. Update README ("What the U3D/PRC reader covers" tables), `THIRD-PARTY-NOTICES.md` if a source was used.

## Specific traps

- **Never edit `Generated/*.g.cs`** in the PRC project: edit `Schema/prc-display.json` (or the generator in `tools/…Prc.Generator`) and run `dotnet run --project tools/Caly.Rendering.Scene3D.Prc.Generator -c Release`; a test fails if checked-in output differs.
- **Compressed tessellation:** see `prc-compressed-tessellation` (do not simplify arithmetic).
- A PRC material whose four alphas are 0 means "no alpha given" = opaque.
- U3D progressive/line/point sets follow the **Sample Software's** set order and predictions (ECMA-363 does not give them); the context 0x400 is dynamic as the reference does, unlike Annex A.
- Animations: node transform is `Sample(t) × Local`; skeleton maths follows the reference player; unsupported: bone links/joints/limits, IK, motion blending, queued second motions.
- Weights are kept to the four strongest bones per vertex.
- Never write `(x ??= new T[n])[i] = v`: it crashes the ILLink trim analyzer (AD0001) in clean Release builds while incremental builds hide it.
- New `Caly.`-namespaced code: `Avalonia` resolves to `Caly.Avalonia`; use `global::Avalonia`.
- A **new project** referenced by Caly must be added to `Caly.slnx` (with the `BuildType Solution="Portable|*" Project="Release"` mapping its neighbours have) and to Scene3D's own `.slnx`; every Caly-referenced project must be in `Caly.slnx` or Visual Studio will not build it.
- Keep layout flat: projects at the repo root, only `tools/` is a subfolder.
