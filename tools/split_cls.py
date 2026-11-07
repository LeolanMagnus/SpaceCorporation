#!/usr/bin/env python3
"""Split a C# class file into partial-class files by member buckets.
Usage: python tools/split_cls.py <mapfile> <srcfile> <bucketrules>
Rules file: lines "MEMBER_START BUCKET" (default bucket CORE, rules in order).
Member block = [start..end] plus gap lines up to the next member (excluding the
class-closing brace). CORE bucket rewrites the original file; other buckets get
<src>.<BUCKET>.cs with usings + namespace + `partial class` decl."""
import sys, re, os

mapfile, src, rulefile = sys.argv[1:4]
lines = open(src, encoding='utf-8-sig').read().splitlines(keepends=True)

def code_of(ln):
    s = re.sub(r'@"(?:[^"]|"(?="))*"', 'S', ln)
    s = re.sub(r'"(\\.|[^"\\\n])*"?', 'S', s)
    s = re.sub(r"'(?:\\.|[^'\\\n])*'?", 'C', s)
    s = re.sub(r'//.*', '', s)
    return s

# locate class decl and body depth + closing brace line
class_line = next(i for i, l in enumerate(lines) if re.search(r'\b(class|struct)\s+\w+', code_of(l)))
depth_before = [0] * len(lines)
d = 0
for i, l in enumerate(lines):
    depth_before[i] = d
    c = code_of(l)
    d += c.count('{') - c.count('}')
cbd, open_idx = None, None
for i in range(class_line, len(lines)):
    c = code_of(lines[i])
    if '{' in c:
        cbd = depth_before[i] + c.count('{') - c.count('}')
        open_idx = i
        break
class_close = next(i for i in range(len(lines) - 1, open_idx, -1)
                   if depth_before[i] == cbd and code_of(lines[i]).strip() == '}')

members = []
for ln in open(mapfile, encoding='utf-8', errors='replace'):
    if ln.strip():
        s, e, h = ln.rstrip('\n').split('\t', 2)
        members.append((int(s), int(e)))

# extend member start upward over contiguous doc/comment/attr lines (stop at blank)
def is_comment(t):
    return t.startswith(('///', '//', '/*', '*', '['))
ext = []
for s, e in members:
    j = s - 1  # 0-based line index of member start
    while j - 1 >= 0 and is_comment(lines[j - 1].strip()) and lines[j - 1].strip():
        j -= 1
    ext.append((j + 1, s, e))
members = ext

# rule lines: "START BUCKET" (exact member) or "FROM-TO BUCKET" (member start in range)
exact, ranges = {}, []
for ln in open(rulefile, encoding='utf-8'):
    ln = ln.split('#', 1)[0].strip()
    if not ln:
        continue
    a, b = ln.split()
    if '-' in a:
        f, t = (int(x) for x in a.split('-'))
        ranges.append((f, t, b))
    else:
        exact[int(a)] = b
def bucket_for(s):
    if s in exact:
        return exact[s]
    for f, t, b in ranges:
        if f <= s <= t:
            return b
    return 'CORE'

first = members[0][0]
header = lines[:first - 1]

by_bucket = {}
for i, (s, memstart, e) in enumerate(members):
    nxt = members[i + 1][0] - 1 if i + 1 < len(members) else class_close  # excl. class close
    nxt = min(nxt, class_close)
    by_bucket.setdefault(bucket_for(memstart), []).extend(lines[s - 1:nxt])

srcbase = src[:-3]
usings = [l for l in header if l.strip().startswith('using')]
nsline = next((l for l in header if l.strip().startswith('namespace')), None)
clsline = next(l for l in header if re.search(r'\bclass\s+\w+', l))
clsname = re.search(r'\bclass\s+(\w+)', clsline).group(1)
decl = clsline.rstrip('\n')
if 'partial' not in decl:
    decl = decl.replace('class', 'partial class', 1)

for b, block in by_bucket.items():
    if b == 'CORE':
        body = header + block + lines[class_close:]
        out = src
    else:
        body = list(usings) + ['\n']
        if nsline:
            body += [nsline.rstrip('\n') + '\n', '\n']
        body += [f'// partial {clsname}: вырезано из {os.path.basename(src)} при partial-декомпозиции.\n',
                 decl + '\n', '{\n'] + block + ['}\n']
        out = f"{srcbase}.{b}.cs"
    if body and not body[-1].endswith('\n'):
        body[-1] += '\n'
    open(out, 'w', encoding='utf-8').writelines(body)
    print(f"{out}: {len(body)} lines")
