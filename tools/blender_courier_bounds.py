import bpy
for o in bpy.data.objects:
    if o.type=='MESH':
        bb=[o.matrix_world @ __import__('mathutils').Vector(c) for c in o.bound_box]
        mn=[min(v[i] for v in bb) for i in range(3)]
        mx=[max(v[i] for v in bb) for i in range(3)]
        print(o.name, 'min',[round(x,2) for x in mn],'max',[round(x,2) for x in mx])
    else:
        print(o.name, o.type, tuple(round(x,2) for x in o.location))
