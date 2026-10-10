"""Prints a saved PRC issue for reading, through the filter described in docs/clean-room-log.md: lines that name nanoPRC or the
patent are dropped (and counted), never shown.

  python read-issue.py <number> [first line] [line count]

The saved issues are not part of the repository (third-party discussion text): save the PDF Association's PRC
issues as <number:04d>.md in a folder named by PRC_ISSUES_DIR (default tools/scripts/issues), in sub-folders
matching pdf-issues-prc*.
"""
import glob
import os
import re
import sys

here = os.environ.get('PRC_ISSUES_DIR') or os.path.join(os.path.dirname(os.path.abspath(__file__)), 'issues')
number = int(sys.argv[1])
start = int(sys.argv[2]) if len(sys.argv) > 2 else 0
count = int(sys.argv[3]) if len(sys.argv) > 3 else 10 ** 9
paths = glob.glob(os.path.join(here, 'pdf-issues-prc*', f'{number:04d}.md'))
if not paths:
    sys.exit(f'no saved issue {number}')
bad = re.compile(r'nanoprc|patent|8,?207,?965', re.I)
lines = open(paths[0], encoding='utf-8').read().split('\n')
kept = [l for l in lines if not bad.search(l)]
print(f'[{os.path.relpath(paths[0], here)}: {len(lines)} lines, {len(lines) - len(kept)} dropped by the filter; showing {start}..{min(start + count, len(kept))} of {len(kept)}]')
for line in kept[start:start + count]:
    print(line if len(line) <= 600 else line[:600] + ' …')
