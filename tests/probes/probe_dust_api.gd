extends Node

# Проба API партиклов для «космической пыли»вокруг игрока. Проверяем в 4.7 то,
# что собираемся писать на C#: имена свойств ParticleProcessMaterial (emission
# shape, velocity, angle, min/max, scale_min/max), наличие ParticleMaterial.
# Запуск: ./run-tests.sh res://tests/probes/probe_dust_api.tscn
func _ready() -> void:
	var m := ParticleProcessMaterial.new()
	var props := [
		"emission_shape", "emission_sphere_radius",
		"direction", "spread", "initial_velocity_min", "initial_velocity_max",
		"angular_velocity_min", "angular_velocity_max",
		"gravity", "scale_min", "scale_max",
		"angle_min", "angle_max", "radial_accel_min", "radial_accel_max",
		"color_ramp",
	]
	var ok := true
	for p in props:
		if not (p in m):
			print("[FAIL] ParticleProcessMaterial нет свойства: ", p)
			ok = false
	print("ParticleProcessMaterial: есть все ", props.size(), " свойств = ", ok)

	var has_pm := ClassDB.class_exists("ParticleMaterial")
	print("ParticleMaterial существует = ", has_pm)

	# Константы формы эмиссии: у GPUParticles3D они продублированы?
	var pm := ParticleProcessMaterial.EMISSION_SHAPE_SPHERE_SURFACE if "EMISSION_SHAPE_SPHERE_SURFACE" in ParticleProcessMaterial else -1
	print("EMISSION_SHAPE_SPHERE_SURFACE = ", pm)

	# GPUParticles3D ключевые поля
	var gp := GPUParticles3D.new()
	for p in ["amount", "lifetime", "local_coords", "visibility_aabb", "process_material", "draw_pass_1", "trail_lifetime"]:
		if not (p in gp):
			print("[FAIL] GPUParticles3D нет свойства: ", p)
			ok = false
	print("GPUParticles3D поля проверены, ok = ", ok)

	# Curve: в 4.7 у Curve нет set_point_color (цвета точек удалены) — значит
	# color_ramp строим только через GradientTexture1D (градиент с прозрачными
	# концами): color_ramp читает ЦВЕТА пикселей текстуры по жизни частицы.
	var g := Gradient.new()
	g.colors = PackedColorArray([Color(1,1,1,0), Color(1,1,1,0.9), Color(1,1,1,0.9), Color(1,1,1,0)])
	g.offsets = PackedFloat32Array([0.0, 0.2, 0.8, 1.0])
	var gt := GradientTexture1D.new()
	gt.gradient = g
	print("GradientTexture1D width = ", gt.get_width())
	var tex: Texture2D = gt
	ok = ok and tex is GradientTexture1D
	var m2 := ParticleProcessMaterial.new()
	m2.color_ramp = gt
	print("color_ramp принят = ", m2.color_ramp != null)
	ok = ok and m2.color_ramp != null
	print("color_ramp = ", ok)
	get_tree().quit(0 if ok else 1)
