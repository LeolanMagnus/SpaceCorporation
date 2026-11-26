import bpy, bmesh, math

def face_angles(o):
    bm = bmesh.new()
    bm.from_mesh(o.to_mesh())  # base mesh (no modifiers)
    # Dihedral angles histogram of "shallow" edges (30..85 deg from flat)
    counts = {}
    for e in bm.edges:
        if len(e.link_faces) != 2:
            continue
        a = math.degrees(e.calc_face_angle())
        key = round(a)
        if 20 <= key <= 89:
            counts[key] = counts.get(key, 0) + 1
    top = sorted(counts.items(), key=lambda kv: -kv[1])[:12]
    print(o.name, "base_verts=", len(bm.verts), "dihedral_hist(top)=", top)
    bm.free()
    o.to_mesh_clear()

for name in ['SC_Courier_Hull', 'SC_Courier_Frame', 'SC_Courier_Rails',
             'SC_Courier_Details', 'SC_Courier_RCS']:
    o = bpy.data.objects.get(name)
    if o:
        face_angles(o)
