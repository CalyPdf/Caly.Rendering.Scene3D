---
name: prc-compressed-tessellation
description: Use when touching or debugging PRC compressed tessellation (PRC_TYPE_TESS_3D_Compressed) — PrcCompressedDecoder, PrcCompressedMesher, PrcBasis, PrcBits — or when PRC meshes come out as shards, spikes, wrong normals, off their origin, "prc-compressed-tessellation" diagnostics, or PrcReader.IsComplete is false.
---

# PRC compressed tessellation decoder

The decoder is **bit-exact arithmetic, not an approximation.** It reproduces the writer's own floating-point operations; a one-ulp difference corrupts meshes.

**REQUIRED FIRST:** `clean-room-3d-sources` (the patent text and nanoPRC are never sources; prc-rs's compressed code is not read).

Full settled rules, with their sources (D = 2009 draft, C = corpus, I = PDF Association issues): `docs/prc-compressed-tessellation.md` in the repository. Read it before changing anything.

## Never "simplify" the arithmetic

Doubles, no fused multiply-add, sums in the order written, `Length` = Newton's root of a sum of squares **rounded to single precision** (the standard's routine). A correctly rounded root, a reciprocal multiply, a reordered sum, `Math.FusedMultiplyAdd`, JIT/vectorisation of those sums, or "tidying" `PrcBasis` each lose meshes. After a triangle of no area a decision hangs on the last bit of earlier results; on tubes of long thin triangles one ulp grows to whole tolerances within a few hundred points (the decode is chaotic). The last rule found — after a flat triangle keep only Z of `MakeOrthoRep`'s axes and take `Y = unit(Z ^ X)` with the edge's own X, before the side test — was exactly one such bit, and 46 sample meshes were shards until then.

## Shape of the algorithm (details in the rules file)

One pass: triangles, points and each triangle's side decoded together; gate-edge stack (last in, first out); stored normals decide the side via the midpoint cross product against the first corner's normal; a normal reference counts back from the last one stored on `bits(stored − 1)` bits; texture-coordinate references count from the first pair; colours on points are five bytes a corner. **A tessellation counts as decoded only when every array is used up exactly**; otherwise it is left out (`prc-compressed-tessellation`) and `PrcReader.IsComplete` is false. The draft misprints the normal frame's first case (X = V1, not V3) and prints `PRC_TYPE_TESS` as ROOT+230 (real: ROOT+170).

Whether a mesh sits on the origin its file gives (`PrcCompressedGeometry.OffOrigin`) is a **test check, not a reason to drop a mesh**: other writers put the origin elsewhere (295 of 34,464 in the second corpus).

## When a mesh decodes wrong

1. Get the corpus facts first: `SCENE3D_PRC_CORPUS` (the 38 sample payloads; on this machine `C:\caly-work\3d\claude\survey\streams`) (38 payloads: 3,780 tessellations, 27 payloads whole) and `SCENE3D_PRC_CORPUS_EXTRA=C:\caly-work\3d\samples-prc-db\prcs` (310 payloads: 34,464). Both must stay at **all consumed exactly**.
2. Look for a **twin**: the same model twice in one file (or in two documents). "All points within ~1 tolerance of the twin" is a *sharp* test; "lands on its origin" is *weak* (many wrong decodes pass it).
3. Suspect rounding? **Add noise before reading more of the walk**: perturb every new point by ~1e-15 relative and see whether results move by tolerances (chaotic) — then move one early point by one ulp. A failure "starting at a flat triangle" may be decided hundreds of points earlier.
4. Try variants of the suspect step and score them on the whole corpus and on the twin; keep the one that wins both. Write the finding in `docs/prc-compressed-tessellation.md` ("How the last rule was found" is the model).

## Rules for changes

- Test first (`PrcCompressedDecoderTests`, `PrcBasisTests`), then both corpora, then the trace comparison (`verifying-3d-changes`).
- Performance work elsewhere must not touch the order or type of these operations; benchmark, but correctness on both corpora is the gate.
- Do not use patent text or nanoPRC to "explain" a failure. Evidence comes from the corpus.
