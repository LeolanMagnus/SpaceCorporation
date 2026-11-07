#!/usr/bin/env python3
"""Split Ship.cs into partial-class files by member buckets.
Usage: python tools/split_ship.py <mapfile> <srcfile> <bucketfile>
bucketfile lines: START_LINE<TAB>BUCKET ; every member whose start line >= START
and < next START inherits BUCKET until overridden (default bucket = CORE).
Each member block = its line range plus the following blank/comment lines up to
the next member's block. Output: <srcfile>.<BUCKET>.cs (lowercased bucket),
CORE overwrites a copy at src path. Nothing but moving lines is done."""
import sys, re

mapfile, src, bucketfile = sys.argv[1:4]
lines = open(src, encoding='utf-8-sig').read().splitlines(keepends=True)
members = []
for ln in open(mapfile, encoding='utf-8'):
    if not ln.strip():
        continue
    s, e, h = ln.rstrip('\n').split('\t', 2)
    members.append((int(s), int(e), h))

# bucket rules: sorted (member_start, bucket)
rules = []
for ln in open(bucketfile, encoding='utf-8'):
    ln = ln.split('#', 1)[0].strip()
    if not ln:
        continue
    start, b = ln.split()
    rules.append((int(start), b))
rules.sort()
def bucket_for(start):
    b = 'CORE'
    for s, bb in rules:
        if start >= s:
            b = bb
        else:
            break
    return b

# member block = [start, end] + trailing gap lines until next member start
bounds = []
for i, (s, e, h) in enumerate(members):
    nxt = members[i + 1][0] if i + 1 < len(members) else len(lines) + 1
    bounds.append((s, nxt - 1, bucket_for(s), h))

# header: everything before first member block (usings, namespace, doc, class decl)
first = members[0][0]
header = lines[:first - 1]

import os
by_bucket = {}
for s, e, b, h in bounds:
    by_bucket.setdefault(b, []).extend(lines[s - 1:e])

srcbase = src[:-3]  # strip .cs
for b, block in by_bucket.items():
    if b == 'CORE':
        out = src  # keep core at original path
        body = header + block
    else:
        out = f"{srcbase}.{b}.cs"
        body = header_for_part() if False else None
    if body is None:
        ns = ''
        # derive namespace line and class partial decl from header
        nsline = next((l for l in header if l.strip().startswith('namespace')), None)
        clsline = next(l for l in header if re.search(r'\bclass\s+\w+', l))
        cls = re.search(r'\bclass\s+(\w+)', clsline).group(1)
        openline = clsline.rstrip('\n')
        if 'partial' not in openline:
            openline = openline.replace('class', 'partial class', 1)
        parts = []
        usings = [l for l in header if l.strip().startswith('using')]
        parts.extend(usings)
        parts.append('\n')
        if nsline:
            parts.append(nsline if nsline.rstrip().endswith(';') else nsline.rstrip('\n') + '\n')
            parts.append('\n')
        parts.append(f'// Автоматически вырезано из {os.path.basename(src)} (partial-декомпозиция).\n')
        parts.append(openline + '\n{\n')
        parts.extend(block)
        parts.append('}\n')
        body = parts
    with open(out, 'w', encoding='utf-8') as f:
        f.writelines(body if body[-1].endswith('\n') or out == src else body)
    print(f"{out}: {sum(1 for _ in open(out, encoding='utf-8'))} lines")
