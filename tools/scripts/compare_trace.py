# Compares how Scene3D.Prc and prc-rs walk PRC payloads: every entity, in order, with the bit it starts
# at, and where each section ends. Both tools are run and their outputs compared as they are produced.
#
#   python compare_trace.py <payload.prc>...            one line per payload, first difference shown (globals, tree, tessellation,\n#                                                       exact geometry, extra geometry and model file are compared)
#   python compare_trace.py --all                       every payload of SCENE3D_PRC_CORPUS; writes oracle.md
#   python compare_trace.py --keep <payload.prc>        also keeps both normalised traces in the current folder
#
# Environment: PRC_DESCRIBE (path of prc-describe, "cargo build --release" in a prc-rs checkout at commit
# 5595499), SCENE3D_PRC_CORPUS (folders of .prc payloads, ;-separated), SCENE3D_TOOL (optional: a built scene3d-tool).
#
# Normalisation of the reference's log: only its "X::from_reader() bp=N" lines (the byte-aligned
# container lines, "byp=", are not part of a section) and its section end lines are kept; section start
# lines are dropped on both sides, because the reference prints them after the schemas it has already
# read.
import glob
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
REFERENCE = os.environ.get('PRC_DESCRIBE', 'prc-describe')   # prc-rs's prc-describe, built from commit 5595499
REPO = os.path.dirname(os.path.dirname(HERE))
OURS = os.environ.get('SCENE3D_TOOL') or glob.glob(os.path.join(REPO, 'tools', 'Caly.Rendering.Scene3D.Tool', 'bin', 'Release', 'net*', 'scene3d-tool.exe'))[-1]
CORPUS = [d for d in os.environ.get('SCENE3D_PRC_CORPUS', '').split(';') if d]

ENTITY = re.compile(r'^\s*TRACE\s+\S+\s+> (\s*[A-Za-z_0-9]+::from_reader\(\) bp=\d+)\s*$')
END = re.compile(r'--"?([A-Za-z]+)"? ENDOK remaining: (\d+) bits.*?consumed: (\d+) bits.*?of (\d+) bits')


def reference_lines(path):
    env = dict(os.environ, RUST_LOG='trace')
    p = subprocess.Popen([REFERENCE, '--gl', '--tr', '--te', '--ge', '--ex', '--mf', path], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         env=env, text=True, encoding='utf-8', errors='replace', bufsize=1 << 20)
    for line in p.stdout:
        m = ENTITY.match(line)
        if m:
            yield m.group(1)
            continue
        m = END.search(line)
        if m:
            yield '--%s ENDOK remaining: %s bits, consumed: %s bits of %s bits--' % m.groups()
    p.wait()
    yield '--EXIT %d--' % (0 if p.returncode == 0 else 1)


def our_lines(path):
    p = subprocess.Popen([OURS, 'trace', path], stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                         text=True, encoding='utf-8', errors='replace', bufsize=1 << 20)
    for line in p.stdout:
        line = line.rstrip('\r\n')
        if ' START ' in line and line.startswith('--'):
            continue
        yield line
    p.wait()
    yield '--EXIT %d--' % (0 if p.returncode == 0 else 1)


def compare(path, keep=False):
    """Returns (same, lines compared, description of the first difference, section ends of ours)."""
    name = os.path.splitext(os.path.basename(path))[0]
    ref_out = open(os.path.join(os.getcwd(), name + '.reference.trace'), 'w', encoding='utf-8') if keep else None
    our_out = open(os.path.join(os.getcwd(), name + '.ours.trace'), 'w', encoding='utf-8') if keep else None
    ref, ours = reference_lines(path), our_lines(path)
    recent = []
    count = 0
    difference = None
    ends = []
    while True:
        a = next(ref, None)
        b = next(ours, None)
        if ref_out and a is not None:
            ref_out.write(a + '\n')
        if our_out and b is not None:
            our_out.write(b + '\n')
        if a is None and b is None:
            break
        if b is not None and b.startswith('--') and 'ENDOK' in b:
            ends.append(b.strip('-'))
        if a != b and difference is None:
            difference = 'line %d\n      reference: %s\n      ours:      %s\n      before:    %s' % (
                count + 1, a, b, ' | '.join(x.strip() for x in recent[-4:]))
            if not keep:
                break
        count += 1
        recent.append(a or '')
        if len(recent) > 8:
            del recent[:4]
    for f in (ref_out, our_out):
        if f:
            f.close()
    return difference is None, count, difference, ends


def main():
    args = sys.argv[1:]
    keep = '--keep' in args
    everything = '--all' in args
    paths = [a for a in args if not a.startswith('--')]
    if everything:
        for folder in CORPUS:
            paths += sorted(glob.glob(os.path.join(folder, '*.prc')))
    rows = []
    failed = 0
    for path in paths:
        start = time.time()
        same, count, difference, ends = compare(path, keep)
        seconds = time.time() - start
        name = os.path.basename(path)
        print('%s  %s  %d lines  %.1f s' % ('SAME' if same else 'DIFF', name, count, seconds), flush=True)
        if not same:
            failed += 1
            print('    ' + difference, flush=True)
        rows.append((name, same, count, seconds, difference, ends))
    if everything:
        with open(os.path.join(os.getcwd(), 'oracle.md'), 'w', encoding='utf-8') as f:
            f.write('# Walking the corpus in step with prc-rs\n\n')
            f.write('From `compare_trace.py --all`: for each payload, every entity read in the globals, tree and\n'
                    'tessellation sections of every file structure and in the model file, with the bit it starts at,\n'
                    'and the bit each section ends at, compared with the trace of prc-rs (`prc-describe`, commit 5595499).\n\n')
            f.write('%d payloads, %d the same, %d different.\n\n' % (len(rows), len(rows) - failed, failed))
            f.write('| Payload | Result | Lines compared | Seconds |\n|---|---|---:|---:|\n')
            for name, same, count, seconds, difference, ends in rows:
                f.write('| %s | %s | %d | %.1f |\n' % (name, 'same' if same else 'DIFFERENT', count, seconds))
            for name, same, count, seconds, difference, ends in rows:
                if not same:
                    f.write('\n## %s\n\n```\n%s\n```\n' % (name, difference))
        json.dump([{'name': r[0], 'same': r[1], 'lines': r[2], 'ends': r[5]} for r in rows],
                  open(os.path.join(os.getcwd(), 'oracle.json'), 'w', encoding='utf-8'), indent=1)
    return 1 if failed else 0


sys.exit(main())
