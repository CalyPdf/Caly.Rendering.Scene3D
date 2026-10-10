---
name: clean-room-3d-sources
description: Use before researching or reading any source for U3D, PRC or 3D-PDF work — specs, GitHub repos, PDF Association issues, patents, web search results, other decoders — and before adding a script engine or any copyleft/AGPL/GPL code to the 3D libraries.
---

# Clean-room rules for the 3D readers

Caly and Scene3D are MIT. The owner set these rules; they are not negotiable by inference. Violating the letter violates the spirit.

## Allowed sources

- ISO 14739-1 (PRC; the 2009 working draft SC2N570 is incomplete and misprinted in places), ECMA-363 (U3D), ISO 32000-2. The repository holds none of them (`tools/scripts/README.md`); on the owner's machine their text is in `C:\caly-work\3d\claude\spec-text`.
- **prc-rs** (`references/prc-rs`, MIT): its reader, schema and tests — **except** its compressed-tessellation code (`src/tess_3d_compressed.rs`): not read, not used, and prc-rs is not run to compare meshes (owner, 2026-10-11).
- **Universal 3D Sample Software** (`references/u3d`, Apache-2.0): reference decoder; allowed and used for sets/predictions ECMA-363 leaves unsaid. Notices in `THIRD-PARTY-NOTICES.md`.
- The sample corpora and the owner's problem files.
- **PDF Association PRC issues** (open and closed, saved in `research/pdf-issues-prc*`): errata and observed behaviour, preferred over the 2009 draft where they differ. #727's decoding pseudo-code may be read and implemented from (owner's explicit choice).

## Never

| Source | Rule |
|---|---|
| **nanoPRC** (AGPL, github mvrhel/nanoPRC and forks) | Never clone, open, read, run — not even as a black-box oracle. Do not follow links to it. If a search or fetch surfaces its source, stop reading it. |
| **US patent 8,207,965** (expired but still off limits) | Never read its text; never use passages whose stated basis is the patent. |
| Any other AGPL/GPL 3D decoder | **Ask the owner** before opening. |
| A JavaScript engine | Scripts are never executed. If needed: Jint (sebastienros/jint) only, and ask first. |

## Reading PDF Association issues

Filter first, always: `python tools/scripts/read-issue.py <issue>` (saved issues: `PRC_ISSUES_DIR`; owner's copy `C:\caly-work\3d\claude\research`) (or `grep -v -i "nanoprc\|patent"`). A passage arguing from nanoPRC or the patent is not read further, and the skip is logged in `docs/clean-room-log.md` (date, issue, what, why). Links to either are never followed.

## Red flags — stop

- "I'll just peek at nanoPRC to check one rule" / "as a black box it's harmless"
- "The patent expired, so reading it is fine" (**still avoid its text**)
- "prc-rs has this part already, I'll read its compressed code"
- "A web result has the algorithm" — check where it comes from before reading
- "Jint is obviously right, I'll add it"

If a rule seems to block necessary work, ask the owner with the specific source and reason; do not decide alone. Rulings go in the workspace `OPEN-QUESTIONS.md` decisions log.
