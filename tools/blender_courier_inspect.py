import bpy, bmesh

for name in ['SC_Courier_Hull', 'SC_Courier_Frame', 'SC_Courier_Rails',
             'SC_Courier_Details', 'SC_Courier_RCS']:
    o = bpy.data.objects.get(name)
    if o is None:
        print(name, 'MISSING')
        continue
    mods = []
    for m in o.modifiers:
        info = m.type
        if m.type == 'BEVEL':
            info += f" angle={round(m.angle, 3)} segs={m.segments} width={m.width}"
        mods.append(info)
    bm = bmesh.new()
    bm.from_mesh(o.evaluated_get(bpy.context.evaluated_depsgraph_get()).to_mesh())
    bm.normal_update()
    nonman = sum(1 for e in bm.edges if len(e.link_faces) != 2)
    holes = sum(1 for e in bm.edges if len(e.link_faces) < 2)
    ngons = sum(1 for f in bm.faces if len(f.verts) > 4)
    print(f"{name}: eval_verts={len(bm.verts)} nonmanifold_edges={nonman} holes={holes} ngons={ngons} mods={mods}")
    bm.free()
    o.to_mesh_clear()
