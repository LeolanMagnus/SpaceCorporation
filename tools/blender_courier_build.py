# F2.5 (docs/habitable-deck-phase2-plan.md, шаг 9 фазы 1): единый manifold-меш
# корпуса «Курьера» с тремя переходами под магнитные ботинки: 50° и 30° —
# проходимы (порог SuitModel.CanStepOn = 60°), 80° — блокируется. Углы заданы
# координатами профиля (dy/dx каждого сегмента), приёмка печатает фактические
# углы каждой грани относительно опорной плоскости — приёмка по цифрам.
#
# Оси: Blender X — ширина, Y — длина (нос = -Y => Godot -Z), Z — вверх
# (экспорт glTF Y-up даёт Godot Y-up; нос уходит в Godot -Z, как у корпусов).
#
# Запуск (приёмка, ничего не пишет):
#   blender --background sc_courier.blend --python tools/blender_courier_build.py
# Применение (перезапись sc_courier.blend + экспорт assets/models/courier.glb):
#   blender --background sc_courier.blend --python tools/blender_courier_build.py -- --apply
import bpy, bmesh, math, sys
from mathutils import Vector, Matrix
from mathutils.bvhtree import BVHTree

APPLY = '--apply' in sys.argv

YTOP, YBOT = 0.5, -0.5   #half-height of the mid-section (Godot Y)
t50 = 0.150 / math.tan(math.radians(50))   # 0.1260
w80 = 0.300 / math.tan(math.radians(80))   # 0.0531
r30 = 0.173                                 # 0.100/tan(30) = 0.1732

# Правая половина профиля: крыша -> фаска 50 -> стенка -> фаска 80 -> стенка
# -> фаска 30 -> днище. Углы сегментов ровно 50/80/30 к горизонти.
# Максимальная полуширина 1.0 при scale=1 — стенки кабины (±0.97) внутри.
prof = [(0.000, YTOP),
        (1.000 - t50, YTOP),        # (0.8740, 0.5)
        (1.000, YTOP - 0.150),      # фаска 50°: dy=-0.150, dx=+0.1260
        (1.000, -0.100),            # стенка отвесная
        (1.000 - w80, -0.400),      # фаска 80°: dy=-0.300, dx=-0.0531
        (1.000 - w80 - r30, YBOT),  # фаска 30°: dy=-0.100, dx=-0.1732
        (0.000, YBOT)]
LEFT = [(-x, y) for (x, y) in reversed(prof[1:-1])]
LOOP = prof + LEFT                  # замкнутый контур сечения (12 точек)

# Станции по длине (Y): Blender +Y = Godot -Z (glTF Y-up): НОС (суженный) —
# при +Y; корма (там шлюз) — при -Y. Масштаб одинаковый по X и Z — углы
# фасок не плывут.
STATIONS = [(-2.00, 0.90), (-1.70, 1.00), (-1.40, 1.00),
            (1.40, 0.90), (1.70, 0.65), (2.00, 0.45)]

me = bpy.data.meshes.new('SC_Courier')
ob = bpy.data.objects.new('SC_Courier', me)
bpy.context.collection.objects.link(ob)

bm = bmesh.new()
rings = []
for y, s in STATIONS:
    ring = [bm.verts.new((x * s, y, z * s)) for (x, z) in LOOP]
    rings.append(ring)
n = len(LOOP)
for a, b in zip(rings[:-1], rings[1:]):
    for i in range(n):
        j = (i + 1) % n
        bm.faces.new((a[i], a[j], b[j], b[i]))
bm.faces.new(tuple(reversed(rings[0])))
bm.faces.new(rings[-1])
bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
bm.normal_update()
bm.to_mesh(me)
me.update()

# --- карман под шлюз в кормовой стенке (визуальный проём Airlock.tscn) -----
if True:
    cutter_me = bpy.data.meshes.new('cutter')
    cob = bpy.data.objects.new('HatchCutter', cutter_me)
    bpy.context.collection.objects.link(cob)
    cbm = bmesh.new()
    bmesh.ops.create_cube(cbm, size=1.0)
    for v in cbm.verts:
        v.co = Vector((v.co.x * 1.40, v.co.y * 0.80, v.co.z * 0.35))
        v.co += Vector((0.0, -2.05, 0.0))  # проём шлюза: Godot +Z(корма) = Blender -Y
    cbm.to_mesh(cutter_me)
    cbm.free()
    mod = ob.modifiers.new('HatchCutter', 'BOOLEAN')
    mod.operation = 'DIFFERENCE'
    mod.object = cob
    for prop in ('solver_mode', 'solver'):
        if hasattr(mod, prop):
            setattr(mod, prop, 'EXACT')
            break
    bpy.context.view_layer.update()
    ob.update_tag()
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    bm2 = bmesh.new()
    bm2.from_mesh(ev.to_mesh())
    ob.modifiers.remove(mod)
    bpy.data.objects.remove(cob, do_unlink=True)
    bm.clear()
    tmp_me = bpy.data.meshes.new('tmp_cut')
    bm2.to_mesh(tmp_me)
    bm.from_mesh(tmp_me)
    bpy.data.meshes.remove(tmp_me)
    bm2.free()
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.normal_update()

# --- приёмка ---------------------------------------------------------------
nonman = [e for e in bm.edges if len(e.link_faces) != 2]
holes = [e for e in bm.edges if len(e.link_faces) < 2]
print('VERTS', len(bm.verts), 'FACES', len(bm.faces),
      'NONMANIFOLD', len(nonman), 'HOLES', len(holes))
me_tmp = bpy.data.meshes.new('chk')
bm.to_mesh(me_tmp)
bm2c = bmesh.new(); bm2c.from_mesh(me_tmp)
t1 = BVHTree.FromBMesh(bm); t2 = BVHTree.FromBMesh(bm2c)
faces1 = [f for f in bm.faces]
raw = t1.overlap(t2)
bad = 0
for i, j in raw:
    if i == j:
        continue
    fi, fj = faces1[i], faces1[j]
    if {v.index for v in fi.verts} & {v.index for v in fj.verts}:
        continue  # соседние грани делят вершину/ребро — не пересечение
    bad += 1
print('SELF_OVERLAP_PAIRS', bad)
bm2c.free(); bpy.data.meshes.remove(me_tmp)
cm = sum((v.co for v in bm.verts), Vector()) / len(bm.verts)
inward = sum(1 for f in bm.faces if f.normal.dot(f.calc_center_median() - cm) <= 0)
print('INWARD_FACES', inward, 'CENTROID', tuple(round(x, 3) for x in cm))
# Надёжнее эвристики центроида: знаковый объём (дивергенция). >0 ⇒ нормали наружу.
vol = sum(f.calc_center_median().dot(f.normal) * f.calc_area() for f in bm.faces) / 3.0
print('SIGNED_VOLUME', round(vol, 3), '=> normals OUTWARD' if vol > 0 else '=> normals INWARD')

# Фактический уклон каждой грани к опорной плоскости (Godot XZ / Blender XY):
# angle between the face and the horizontal plane = angle between face normal
# and Blender Z. Группируем по значениям с точностью 0.1°.
slopes = {}
for f in bm.faces:
    a = round(math.degrees(f.normal.angle(Vector((0, 0, 1)))), 1)
    if a > 90:
        a = 180 - a
    slopes[a] = slopes.get(a, 0) + 1
print('FACE_SLOPE_TO_DECK(deg, count):',
      ', '.join(f'{k}:{v}' for k, v in sorted(slopes.items())))
for w in (50.0, 80.0, 30.0):
    hit = [k for k in slopes if abs(k - w) < 0.3]
    print(f'FACET_{int(w)}DEG faces={sum(slopes[k] for k in hit)} slopes={hit}')

bm.to_mesh(me)
me.update()

# --- применение: removing старых мешей курьера, экспорт одного объекта ------
if APPLY:
    for o in list(bpy.data.objects):
        if o.type == 'MESH' and o.name.startswith('SC_Courier') and o is not ob:
            bpy.data.objects.remove(o, do_unlink=True)
    for o in bpy.data.objects:
        o.select_set(o is ob)
    ob.select_set(True)
    glb = r'F:\petprojects\SpaceCorporation\assets\models\courier.glb'
    bpy.ops.export_scene.gltf(filepath=glb, use_selection=True,
                              export_apply=True, export_yup=True)
    print('EXPORTED', glb)
    bpy.ops.wm.save_as_mainfile(
        filepath=r'F:\petprojects\SpaceCorporation\sc_courier.blend')
    print('BLEND SAVED')
bm.free()
