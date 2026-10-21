# Scene3D: the 3D inside your PDFs, in pure .NET
[![Windows](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-windows.yml/badge.svg)](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-windows.yml)
[![Linux](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-linux.yml/badge.svg)](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-linux.yml)
[![macOS](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-macos.yml/badge.svg)](https://github.com/CalyPdf/Caly.Rendering.Scene3D/actions/workflows/unit-tests-macos.yml)

Click a 3D model in a PDF and something has to read it. Scene3D is that something.

Engineering PDFs ship whole products inside them (assemblies, exploded views, PMI, animated work
instructions) in two binary formats almost nothing outside Acrobat can open: **U3D** (ECMA-363) and **PRC**
(ISO 14739-1). Scene3D decodes both into one clean, GPU-ready scene, in managed code you can drop into any
.NET app. It powers the 3D view of the [Caly](https://github.com/BobLd/Caly) PDF reader.

- **Zero dependencies.** Base class library only. `net8.0` and `net10.0`, AOT- and trim-compatible.
- **Fast.** Vertex arrays are read straight out of the file into pooled buffers: nothing is allocated per
  vertex or per face.
- **Checked against the references.** Every node, material and triangle of the 54 U3D test scenes is compared
  with the IDTF text the scene was made from, and the PRC reader matches an independent implementation,
  prc-rs, entity for entity.
- **Exact geometry, too.** CAD solids stored as B-rep (surfaces and trimming curves, no mesh) are rebuilt and
  triangulated.
- **Safe on hostile files.** Bounded allocations, enforced limits, fuzzed readers, one exception type.

## Install

```
dotnet add package Caly.Rendering.Scene3D.U3D
dotnet add package Caly.Rendering.Scene3D.Prc
```

Both bring in `Caly.Rendering.Scene3D`, the scene model and glTF writer.

## Five lines to a model

```csharp
using Caly.Rendering.Scene3D;
using Caly.Rendering.Scene3D.Prc;
using Caly.Rendering.Scene3D.U3D;

byte[] payload = File.ReadAllBytes("part.prc");      // or the 3D stream of a PDF annotation

using Scene scene = PrcReader.IsPrc(payload) ? PrcReader.Read(payload) : U3DReader.Read(payload);

using var glb = File.Create("part.glb");
GlbWriter.Write(scene, glb);                          // open it in any glTF viewer
```

Rendering it yourself? A `Scene` is flat arrays linked by index: walk `scene.Nodes` (parents first), take
each node's `scene.GetWorldMatrix(i)` and its `scene.Meshes[node.Mesh]`, whose vertices live in
`Positions`, `Normals`, `TexCoords` and `Colors` and whose triangles in `Indices`, each part with its
material. Upload, call `scene.ReleaseGeometry()` to drop the vertex data and keep the tree, and dispose the
scene when you are done. `Scene.Diagnostics` tells you anything a reader skipped.

## What it reads

| | U3D | PRC |
|---|:---:|:---:|
| Scene tree with names and placements | ✅ | ✅ |
| Triangle meshes | ✅ full detail, progressive meshes included | ✅ plain and compressed |
| Exact geometry (B-rep) triangulated | — | ✅ planes, cylinders, cones, spheres, tori, NURBS; colours of single faces of compressed bodies |
| Lines and points | ✅ line sets, point sets | ❌ wires and points |
| Materials, colours, transparency | ✅ | ✅ with PRC's style inheritance, and the colours, removals and placements an assembly gives the items (and colours the single faces) of its parts |
| Vertex colours | ✅ diffuse | ✅ |
| Textures (JPEG, PNG; TIFF in U3D) | ✅ first layer | ✅ with their 2D transform and wrapping |
| Lights | ✅ | ❌ |
| Cameras | ✅ from view nodes | ❌ (a PDF carries its own views) |
| Node animation | ✅ | — |
| Skeletal animation and skinning | ✅ | — |
| PMI notes and dimensions | — | ✅ as hidden nodes PDF views can show |
| Units (`Scene.UnitScale`) | ✅ | ✅ |
| Acrobat 3D vendor mesh (`RHAdobeMeshResource`) | ✅ | — |

✅ read · ❌ in the format, not read · — not part of the format

### U3D: not supported

- Subdivision, CLOD-level and glyph modifiers
- External file references (never opened)
- Extension blocks other than `RHAdobeMeshResource`
- Specular vertex colours, texture layers after the first, normals and texture coordinates of line and point sets
- Queued second motions, bone joints and limits, inverse kinematics, blending between motions
- Scripts, and the PDF's own animation settings

### PRC: not supported

- Wires and points
- B-rep faces on blend, revolution, extrusion and offset surfaces, and the rare face whose edges do not lie on
  its surface (counted in `Scene.Diagnostics`)
- Markup text, symbol and picture entities, leaders, and markups of parts and of PRC views
- The views and cameras stored in the PRC file itself
- Pictures stored as raw pixels; line styles and patterns
- Colours of single faces of uncompressed B-rep bodies (those of compressed bodies are read), and those an assembly gives single
  faces of solids drawn from their B-rep rather than their mesh (counted in `Scene.Diagnostics`)
- Projected and surface-mapped textures, blend and decal texture functions

A PRC part whose compressed mesh cannot be decoded is left out, and `PrcReader.IsComplete(scene)` says so.

## Built for untrusted input

3D streams come out of PDFs from anywhere. Before anything is allocated, every count is checked against
what the stream can still hold. `SceneLimits` caps vertices, indices, nodes, meshes, textures and total
bytes (inflated PRC sections included), and loops, runaway nesting and cancellation are handled. Whatever
is wrong with a file, you get a `Scene3DException` saying what, never a crash. The readers and the
compressed decoders are fuzzed in the test suite.

## Speed

Measured with the benchmarks in `Caly.Rendering.Scene3D.Benchmarks` on files in this repository (.NET 8,
BenchmarkDotNet):

| What | Time |
|---|---:|
| `U3DReader.Read` of `plane64x64_base.u3d`: whole file to scene, one 4,096-vertex mesh, 329 KB | 1.5 ms (217 MB/s, 28 KB of garbage) |
| PRC bit stream: one variable-length integer | 6.7 ns |
| PRC bit stream: one double in PRC's own encoding | 37 ns |

Point the benchmarks at your own `.u3d` and `.prc` files to measure them (see the test project's README).

## In the box

| | |
|---|---|
| `Caly.Rendering.Scene3D` | The scene model, `SceneView` (a PDF 3D view: camera, lighting, what is shown), limits, `GlbWriter` |
| `Caly.Rendering.Scene3D.U3D` | `U3DReader` |
| `Caly.Rendering.Scene3D.Prc` | `PrcReader`, `PrcNodeNames` (matches the part names PDF views use to the scene) |
| `tools/Caly.Rendering.Scene3D.Tool` | `scene3d-tool`: `summary`, `glb`, `png` preview, `trace` of PRC entities |
| `Caly.Rendering.Scene3D.Tests` | Runs anywhere with `dotnet test`; [bring your own 3D PDFs](Caly.Rendering.Scene3D.Tests/README.md) for more |

## Built with

| | |
|---|---|
| [prc-rs](https://github.com/ralovich/prc-rs) by Kristóf Ralovich (MIT) | The machine-readable PRC schema the PRC entity readers are generated from, ported bit-level decoders, test vectors, and an independent reader the PRC reader is checked against |
| [Universal 3D Sample Software](https://github.com/ningfei/u3d) (Apache-2.0) | The U3D test scenes, and the reference decoder's arithmetic where ECMA-363 is silent |
| Adobe, *U3D Supported Elements* | The layout of Acrobat 3D's vendor mesh |
| [PDF Association](https://pdfa.org) PRC issues | Errata to ISO 14739-1 and the compressed-mesh decoding outline |
| [xUnit](https://xunit.net), [BenchmarkDotNet](https://benchmarkdotnet.org) | Tests and benchmarks (not shipped) |

Details in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Licence

MIT
