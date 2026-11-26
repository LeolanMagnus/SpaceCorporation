import bpy, bmesh, math
o = bpy.data.objects['SC_Courier_Hull']
print('dims', tuple(round(v,3) for v in o.dimensions), 'loc', tuple(round(v,3) for v in o.location))
bm = bmesh.new(); bm.from_mesh(o.data)
# loose parts count
seen=set(); islands=0
for f in bm.faces:
    if f.index in seen: continue
    islands+=1; stack=[f]
    while stack:
        cf=stack.pop()
        if cf.index in seen: continue
        seen.add(cf.index)
        for e in cf.edges:
            for lf in e.link_faces:
                if lf.index not in seen: stack.append(lf)
print('islands', islands, 'verts', len(bm.verts), 'faces', len(bm.faces))
# candidate 90-deg long edges
cands=[]
for e in bm.edges:
    if len(e.link_faces)!=2: continue
    a=math.degrees(e.calc_face_angle())
    L=e.calc_length()
    if 85<a<95 and L>1.0:
        c=e.calc_center_median()
        cands.append((round(a,1), round(L,2), tuple(round(x,2) for x in c)))
cands.sort(key=lambda t:-t[1])
print('long 90deg edges:', len(cands))
for t in cands[:20]: print(t)
bm.free()
