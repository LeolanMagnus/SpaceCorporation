extends Node

# Проба: звёздный скайбокс собран правильно. Проверяет три вещи, которые
# легко сломать, не заметив глазами в headless:
#   1) `Environment.tscn` грузится, у `WorldEnvironment` стоит режим Sky;
#   2) небом висит `PanoramaSkyMaterial` с непустой панорамой формата 2:1
#      (не equirect → небо схлопнется в «трубу» или растянется швом);
#   3) сама карта тёмная и с яркими точками: генератор мог выдать и серую
#      кашу, и чёрную заглушку, и обе ошибки видны только по числам.
#
# Запуск: ./run-tests.sh res://tests/probes/probe_skybox.tscn
func _ready() -> void:
	var packed: PackedScene = load("res://scenes/Environment.tscn")
	if packed == null:
		print("[FAIL] res://scenes/Environment.tscn не грузится")
		get_tree().quit(1)
	var inst := packed.instantiate()
	var we: WorldEnvironment = inst.find_child("WorldEnvironment", true, false)
	if we == null or we.environment == null:
		print("[FAIL] в сцене нет WorldEnvironment с environment")
		get_tree().quit(1)
	var env: Environment = we.environment
	# background_mode: 0 Clear Color, 1 Custom Color, 2 Sky (в 4.7 константы
	# BG_MODE_* в GDScript-биндингах отсутствуют, потому цифра).
	var ok: bool = env.background_mode == 2
	print("background_mode = ", env.background_mode, " (ожидание 2 = Sky)")
	var sky: Sky = env.sky
	ok = ok and sky != null and sky.sky_material is PanoramaSkyMaterial
	if not ok:
		print("[FAIL] небо не PanoramaSkyMaterial: ", sky)
		get_tree().quit(1)
	var psm: PanoramaSkyMaterial = sky.sky_material
	var tex: Texture2D = psm.panorama
	if tex == null:
		print("[FAIL] panorama пустая — ext_resource не подхватился")
		get_tree().quit(1)
	var sz := tex.get_size()
	print("panorama = ", tex.resource_path, "  размер = ", sz)
	ok = ok and sz.x == sz.y * 2
	print("background_energy_multiplier = ", env.background_energy_multiplier)

	# Яркость смотрим по исходному PNG: get_image() на ввезённой текстуре в
	# headless распаковывает BC-блок целиком и дороже, чем вся проба.
	var raw := Image.load_from_file("res://assets/sky/starmap_equirect.png")
	if raw == null:
		print("[FAIL] исходник assets/sky/starmap_equirect.png не читается")
		get_tree().quit(1)
	# PNG записан в sRGB, а сэмплируется скайбоксом как линейные значения,
	# поэтому порог проверяем в линейном пространстве.
	var chans_of := func(c: Color) -> Array[float]: return [c.r, c.g, c.b]
	var mx := 0.0
	var sum := 0.0
	var n := 0
	for y in range(0, raw.get_height(), 61):
		for x in range(0, raw.get_width(), 61):
			var l := 0.0
			for v in chans_of.call(raw.get_pixel(x, y)):
				l += maxf(v, 0.0) ** 2.4
			l /= 3.0
			mx = maxf(mx, l)
			sum += l
			n += 1
	var mean := sum / n
	print("исходник (линейно): max=", mx, " mean=", mean, " (выборок ", n, ")")
	ok = ok and mx > 0.3 and mean < 0.03
	print("=== ", "OK" if ok else "FAIL", " ===")
	get_tree().quit(0 if ok else 1)
