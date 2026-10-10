# Passages not used as sources

> The saved issues named below (`research/pdf-issues-prc*`) and `research/issue-images` are not in this repository; `tools/scripts/read-issue.py` reads them through the filter if you save them again.

The PRC reader is written from the ISO text held in this workspace, the 2009 draft, prc-rs (MIT), the
corpus, and the PDF Association's PRC issues used as errata and observed behaviour. Two things are never
sources: **nanoPRC** (AGPL; never opened, links never followed) and **the text of US patent 8,207,965**.
Passages in the issues whose stated basis is either are skipped. This file records them.

Owner's decisions: 2026-10-10 (nanoPRC), 2026-10-11 (patent text; issues as errata only). See
`OPEN-QUESTIONS.md`.

## How the issues are read

- `pdf-issues-prc/*.md` holds every open issue with its comments as fetched on 2026-10-11.
- When reading them, lines that mention nanoPRC or the patent are filtered out first
  (`grep -v -i "nanoprc\|patent"`), and a passage that argues from either is not read further.
- Counts at fetch time: nanoPRC is named in 16 of the 52 issues (41 mentions, by name or repository link,
  no source file quoted); the patent in 1 (#727, 4 lines).
- `pdf-issues-prc-closed/*.md` holds every closed issue with its comments (53 issues; bodies and the
  comments of 15 fetched on 2026-10-11, the rest later the same day with `fetch-closed-comments.py` once
  GitHub's request limit allowed). The owner asked for them on 2026-10-11; they are read in the same way.
  Counts: nanoPRC or the patent is named in 2 of the 53 (#532 and #654, one line each), both about exact
  geometry, which Phase 3 does not read.

## Log

| Date | Issue | What was skipped | Why |
|---|---|---|---|
| 2026-10-11 | #805, #810, #816, #818, #820 (bodies read for the Phase 3 spec) | Lines naming nanoPRC, removed by the filter before reading (#810: 2, #816: 2; none in the others) | Stated basis nanoPRC |
| 2026-10-11 | closed #405, #406, #407, #409, #411, #430, #503, #504, #575, #724 (read for Phase 3) | Nothing: none of them names nanoPRC or the patent | - |
| 2026-10-11 | closed #532, #654 | Not read: exact geometry (topological context, a blend surface), outside Phase 3; one filtered line each | Not needed; would be read through the filter |
| 2026-10-11 | closed #416, #420, #421, #426, #427, #429, #436, #489, #490, #540, #675, #729, #748 | Not read beyond their titles: compressed tessellation, Phase 4. (The field order they led to is in prc.json, which the walker of Phase 3 follows.) | Phase 4, with the filter |
| 2026-10-11 | #727 | Not read at all yet. It is the compressed-tessellation thread (Phase 4) and argues partly from the patent | To be read for Phase 4 with the filter, and passage by passage |

Also noted (2026-10-11, Phase 3 task 5): to read past compressed tessellations, three functions of
prc-rs's `src/tess_3d_compressed.rs` were read (`enter`, `number_of_referenced_points`, `number_of_faces`
with `number_of_faces_stored_in_mesh`): they count array entries and nothing else. The printed line range
also showed three small maths helpers (a normal from three points, Taylor series of sine and cosine),
which were not used. The reconstruction of the mesh in that file was not opened.

Also noted: prc-rs's README links the patent and issue #727 beside its unfinished compressed-tessellation
items. prc-rs's code for that part (`src/tess_3d_compressed.rs`) is to be looked at for Phase 4 only after
checking how it says it was derived.
| 2026-10-11 | Phase 4, task 0: closed #416, #421, #427, #429, #748; open #774, #775 (first 70 lines), #822 | Nothing: no line of them names nanoPRC or the patent | - |
| 2026-10-11 | #727 | Still not read. Held back: to be opened only if the draft and the corpus do not settle a reading, then in slices through `read-issue.py` | 16 filtered lines, 4 naming the patent |
| 2026-10-11 | prc-db (`ralovich/prc-db`, pointed to by the owner) | Only its `prcs` folder of sample payloads is used, as data. Its README was read through the filter (no line dropped) | Test data; nothing in it is a source for the decoder |
| 2026-10-11 | Phase 4, tasks 3/4/7: #773, #771, #540, #774, #772, #750, #436, #748, #729, #812, #489, #426 | #773: 3 lines removed by the filter (around its 'evidence' sentences). The others: none | #773's finding (frame turned half a turn about Z; an orientation test after MakeOrthoRep) had both been found from the corpus before it was read (probe-geometry.txt, probe-fallback-angles.txt, earlier the same day) |
| 2026-10-11 | #727, comment of 2026-06-04 08:02 ('CR-14') | Skipped as a whole. Its header says its claims are 'verified against the nanoPRC reference implementation' (that one attribution line was looked at, links removed, to learn the stated basis). SEEN BY ACCIDENT while slicing: a paragraph on the stack push order quoting three lines of a comment from that decoder's source (right pushed first, left second, so left is treated first). Nothing taken: 'left next, right on the stack' is the 2009 draft's own wording and was in use since Task 3's first probes | Stated basis nanoPRC's code |
| 2026-10-11 | #727, comment of 2026-06-04 16:33 ('Pseudo-code Additions', 12 functions) | Not read. SEEN BY ACCIDENT in an outline listing of the issue's headings: the function names and seven comment lines of function 7 (initial left/right edges, swap when the normal was reversed, ascending index order on an edge, push right then left). Nothing taken: the same rules were already in the prototype | #772 says these functions were read from a reference decoder's source; CR-14 names that decoder as nanoPRC. Whether to read it is the owner's decision |
| 2026-10-11 | #727, comments after 2026-06-04 18:41 | Not read | Same thread; 13 filtered lines; to be read through the filter only if the owner keeps the rule and a specific question needs them |
| 2026-10-11 | Screenshots in issues (saved in research/issue-images) | #436 (working draft's 7.8.9.4 pseudo-code and narrative), #748 (Table 175) fetched and read | Pages of the standard's working draft; no reference to nanoPRC or the patent |
| 2026-10-11 | #727 'Pseudo-code Additions' and the comments discussing it | READ from here on, by the owner's decision of this date (see the ledger). Still filtered: lines naming nanoPRC or the patent; still never followed: links to either | Owner: "Read the pseudo-code (Recommended)" |
| 2026-10-16 | #532 (TopologyContext, FileStructureExactGeometry) | READ through the filter, by the owner's clearance of this date (owner checked it: nanoPRC is named once, in a line saying the analysis does not depend on it). 1 line dropped by the filter, not read | Owner-cleared; container layout of the exact-geometry section only, nothing on compressed B-rep decoding |
| 2026-10-16 | #654 (PRC_TYPE_SURF_Blend03 counts) | READ through the filter, by the owner's clearance of this date. 1 line dropped by the filter, not read. SEEN in the 2026-09-05 comment: two lines of C quoted from an implementation (both counts read as signed 32-bit, with a '/* Unsigned int ?? */' comment); its attribution line was the dropped one. Nothing taken: the ruling (both counts UnsignedInteger) is the issue's own and prc-rs's schema already reads number_of_elements so | Owner-cleared; Blend03 surface only, nothing on compressed B-rep |
| 2026-10-16 | #752, #583, #666, #663, #692, #705, #706, #715, #696, #582 (compressed B-rep, Phase 5 Task 0) | READ through the filter, whole, by the owner's standing clearance of this date for the exact-geometry issues; 0 lines dropped in each. #705 and #706 carry change requests (CR-6 – CR-13) whose basis is "implementor testing" and "confirmed by @socrat74"; no passage argued from the patent or from a named outside decoder. In #696 a commenter's C++ pseudo-code of the loop-reading algorithm was read as that commenter's own description of the format (it names no outside decoder) | Owner-cleared; used for: bit widths, tolerances (tol/100 for compressed points), NumberOfBitsThenUnsignedInteger counts, dual-cache numbering, sentinel loops of ana faces, IsoNurbs trim curves, point count of compressed NURBS |
| 2026-10-16 | #581, #753 (compressed B-rep, Phase 5 Task 0) | Filtered (0 lines dropped), not read in full: #581 is superseded by #706 for CompressedPoint (its proposal is marked buggy there); #753 concerns SingleWireBodyCompress, which this phase only walks | Not needed for the compressed solid body |
