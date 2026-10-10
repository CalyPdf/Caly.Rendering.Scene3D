# Caly.Rendering.Scene3D.Tests

```
dotnet test Caly.Rendering.Scene3D.Tests -c Release
```

## What runs with nothing set up

Everything the tests need to run is in this repository, and a plain `dotnet test` (on a developer machine or in
GitHub Actions) runs every test against it:

- `TestData/TestScenes`: the 54 sample scenes of the Universal 3D Sample Software (`.u3d` and the `.idtf` text
  each was made from; Apache-2.0, see `THIRD-PARTY-NOTICES.md`). The tests parse the IDTF independently and
  compare it with what the U3D reader produced.
- `TestData/Prc`: the bit-reader test vectors of prc-rs (MIT).
- PRC files the tests write themselves, bit by bit (`PrcBitWriter`, `PrcBrepWriter`, the seed files of
  `PrcFuzzTests`), and the fuzz tests' mutations of all of these.

The corpus tests (below) need real files that cannot be shipped here. Without them they have nothing to do and
pass.

## Real-world samples (optional, local)

No real-world 3D PDF we know of comes with a licence that allows it to be committed to a public repository, so
none is included. They are where most of the PRC reader's coverage comes from (compressed tessellation and
compressed B-rep are written only by commercial CAD exporters), and are worth having locally before changing a
reader.

### Where to get them

Sample PDF libraries:

- **PDF Association 3D PDF Showcase**: member-contributed examples across BOMs, technical data packages, service
  documentation, work instructions and product-data quality.
  <https://pdfa.org/3d-pdf-showcase/>
- **Tech Soft 3D sample documents**: interactive 3D PDFs including a bill of materials, work instruction,
  technical data package, model viewer and PMI analysis.
  <https://www.techsoft3d.com/enterprise/spinfire-convert/3d-pdf/sample-documents/>
- **Tetra 4D samples**: industry examples such as a turbine engine, chassis, carburetor, gearbox, design review
  and engineering data release.
  <https://tetra4d.com/pdf-samples/>
- **SimLab 3D PDF gallery**: template-based examples with smart scene states and animation (U3D; the downloads
  are zip archives).
  <https://www.simlab-soft.com/SimlabArt/gallery/3d-pdf-gallery/>

A file containing several examples:

- **PDF in Manufacturing**: this PDF holds 3D PDFs as attachments, from Anark, PROSTEP, Tetra 4D and Tech Soft 3D,
  including a hydraulic-manifold additive-manufacturing TDP and a hammer-drill model. Save the attachments first
  (`qpdf --list-attachments PDF-in-Manufacturing.pdf`, then `qpdf --show-attachment=<key> PDF-in-Manufacturing.pdf > <name>.pdf`).
  <https://pdfa.org/wp-content/uploads/2020/05/PDF-in-Manufacturing.pdf>

Collections of payloads already extracted (no licence stated; the PDFs they come from were found by web search):

- **ralovich/prc-db**: 310 `.prc` payloads. <https://github.com/ralovich/prc-db>
- **ralovich/u3d-db**: 375 `.u3d` payloads (1.7 GB). <https://github.com/ralovich/u3d-db>

For U3D specifically:

- **Universal 3D Sample Software**: U3D-producing sample software and an IDTF-to-U3D converter, useful for raw
  `.u3d` test data rather than finished PDFs; the scenes in `TestData/TestScenes` come from it.
  <https://github.com/ningfei/u3d>

### From PDFs to payloads

The tests read raw `.prc` and `.u3d` streams, not PDFs. `scripts/extract3d.sh` of **ralovich/prc-rs**
(<https://github.com/ralovich/prc-rs>, MIT; needs `qpdf` and `jq`) dumps the 3D streams of a PDF next to it:

```
for i in *.[pP][dD][fF]; do path/to/prc-rs/scripts/extract3d.sh "$i"; done
```

(Do not read prc-rs's `src/tess_3d_compressed.rs`: the compressed-tessellation decoder here is written without
it. Its other code, its schema and its tools may be used; see `docs/clean-room-log.md`.)

### Pointing the tests at them

Each variable takes one folder or several, separated by `;`, searched with their subfolders.

| Variable | Files | Used by |
|---|---|---|
| `SCENE3D_PRC_CORPUS` | `.prc` payloads from the sample PDFs | `PrcContainerTests`, `PrcWalkTests`, `PrcReaderTests`, `PrcMesherCorpusTests`, `PrcUnitsCorpusTests`, `PrcCompressedCorpusTests`, `PrcBrepCorpusTests`, `PrcBrepDataCorpusTests`, `PrcFuzzTests` (as seeds), `PrcReadBenchmarks` |
| `SCENE3D_PRC_CORPUS_EXTRA` | ralovich/prc-db's `prcs` folder | `PrcCompressedCorpusTests` (the extra corpus), `PrcBrepCorpusTests`, `PrcBrepDataCorpusTests` |
| `SCENE3D_U3D_CORPUS` | `.u3d` payloads (SimLab's, ralovich/u3d-db's `u3ds`) | `U3DCorpusTests`, `U3DReadBenchmarks` |
| `CALY_3D_SAMPLES` | searched as well for `.prc` and `.u3d` files below it (a folder of PDFs alone adds nothing) | as `SCENE3D_PRC_CORPUS` and `SCENE3D_U3D_CORPUS` |
| `SCENE3D_FUZZ_MUTATIONS` | a number: mutations per fuzzed file, for deeper fuzzing (defaults are set in each fuzz test) | the fuzz tests |
| `SCENE3D_PRC_SEED_FOLDER` | a folder to write the small seed PRC files into, as test data for other projects | `PrcFuzzTests.TheSeedFilesAreWrittenOutOnRequest` |
| `SCENE3D_PRC_BENCH` | file names to keep, `;`-separated | `PrcReadBenchmarks` |

Most corpus tests check properties that hold for any file (every payload opens, decodes or is refused, every
array is used up exactly, every closed spline closes). A few pin the numbers of the corpus they were written
against, so that a change of arithmetic shows, and fail on any other set of files without anything being wrong:

- `PrcCompressedCorpusTests.TheCorpusDecodes`: the 38 PRC payloads of the PDFs listed above as they were in
  October 2026 (the showcase, Tetra 4D, Tech Soft 3D and the attachments of PDF in Manufacturing) in
  `SCENE3D_PRC_CORPUS`.
- `PrcBrepCorpusTests.TheCompressedBodiesOfTheCorpusHoldWhatTheyHeldWhenTheyWereFirstRead`: those 38 plus
  ralovich/prc-db at commit `b1a62f6` in `SCENE3D_PRC_CORPUS_EXTRA` (348 payloads).
- `PrcReaderTests` and `PrcBrepCorpusTests.TheLandingGearIsCutFinelyAndQuickly` pin what some files give, by file
  name, when those files are present.
