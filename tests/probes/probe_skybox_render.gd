extends Node

# Проба-рендер: реально рисует скайбокс в оффскрин-вьюпорт и сохраняет PNG,
# чтобы увидеть небо глазами, а не только проверить числа. Headless-режим для
# этого не годится (там нет рендера), поэтому проба запускается с настоящим
# окном, которое тут же прячется.
#
# Запуск: ./run-tests.sh res://tests/probes/probe_skybox_render.tscn
func _ready() -> void:
	# Окно нужно: в headless-режиме нет рендера. Прячем его, чтобы проба не
	# мигала на экране.
	get_window().hide()
	var packed: PackedScene = load("res://scenes/Environment.tscn")
	var env: Environment = packed.instantiate().find_child("WorldEnvironment", true, false).environment
	var vp := SubViewport.new()
	vp.size = Vector2i(640, 480)
	vp.render_target_update_mode = SubViewport.UPDATE_ALWAYS
	add_child(vp)
	var we := WorldEnvironment.new()
	we.environment = env
	vp.add_child(we)
	var cam := Camera3D.new()
	cam.fov = 73.0
	cam.current = true
	vp.add_child(cam)

	var shots := {
		"horizon_forward": Vector3(0, 0, -90),   # камера прямо в пояс? смещение пояса видно по крену
		"tilt_up": Vector3(25, 0, -90),
		"sideways": Vector3(0, 55, -90),
	}
	var i := 0
	for key in shots:
		cam.look_at(shots[key], Vector3.UP)
		for _f in 6:
			await RenderingServer.frame_post_draw
		var img := vp.get_texture().get_image()
		var path := "user://skybox_%s.png" % key
		var err := img.save_png(path)
		print("shot ", key, " -> ", ProjectSettings.globalize_path(path), " err=", err)
		i += 1
	print("=== shots done: ", i, " ===")
	get_tree().quit(0)
