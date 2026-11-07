#!/usr/bin/env python3
"""Map top-level members of the FIRST class in a C# file.
Prints: start_line\tend_line\theader (1-based, inclusive; block start extended
up over contiguous attribute/doc/comment lines immediately above)."""
import sys, re

path = sys.argv[1]
raw = open(path, encoding='utf-8-sig').read().splitlines()

def code_of(ln):
    s = re.sub(r'@"(?:[^"]|"(?="))*"', 'S', ln)
    s = re.sub(r'"(\\.|[^"\\\n])*"?', 'S', s)
    s = re.sub(r"'(?:\\.|[^'\\\n])*'?", 'C', s)
    s = re.sub(r'//.*', '', s)
    return s

class_line = None
for i, ln in enumerate(raw):
    if re.search(r'\b(class|struct)\s+\w+', code_of(ln)):
        class_line = i
        break
if class_line is None:
    sys.exit('no class found')

# depth_before[i] = brace depth before line i
depth_before = [0] * len(raw)
d = 0
for i, ln in enumerate(raw):
    depth_before[i] = d
    c = code_of(ln)
    d += c.count('{') - c.count('}')

# class body depth = depth after the line that opens it
cbd = None
for i in range(class_line, len(raw)):
    c = code_of(raw[i])
    if '{' in c:
        cbd = depth_before[i] + c.count('{') - c.count('}')
        open_line = i
        break
if cbd is None:
    sys.exit('class body brace not found')

members = []
i = open_line + 1
cur_start = None
while i < len(raw):
    d0 = depth_before[i]
    c = code_of(raw[i])
    d1 = d0 + c.count('{') - c.count('}')
    stripped = raw[i].strip()
    if cur_start is None:
        if d0 == cbd and stripped and not stripped.startswith(
                ('//', '/*', '*', '#', 'region', 'endregion')):
            cur_start = i
    if cur_start is not None and d1 == cbd and (stripped.endswith(';') or stripped.endswith('}')):
        s = cur_start
        hdr = ''
        for j in range(cur_start, min(cur_start + 5, i + 1)):
            t = raw[j].strip()
            if t and not t.startswith(('///', '//')):
                hdr = t
                break
        members.append((s + 1, i + 1, hdr[:120]))
        cur_start = None
    i += 1

for s, e, h in members:
    print(f"{s}\t{e}\t{h}")
