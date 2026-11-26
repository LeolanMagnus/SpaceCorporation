import bpy, bmesh
from mathutils.bvhtree import BVHTree
names=['SC_Courier_Hull','SC_Courier_Frame','SC_Courier_Rails','SC_Courier_Details','SC_Courier_RCS']
deps=bpy.context.evaluated_depsgraph_get()
trees={}
for n in names:
    o=bpy.data.objects[n]
    bm=bmesh.new(); bm.from_object(o, deps)
    trees[n]=BVHTree.FromBMesh(bm, epsilon=0.0)
    bm.free()
for i in range(len(names)):
    for j in range(i+1,len(names)):
        ov = trees[names[i]].overlap(trees[names[j]])
        print(names[i],'<->',names[j],'pairs=',len(ov))
