extends SceneTree

# Список API SceneReplicationConfig (имена методов и число аргументов).
func _init() -> void:
	var cfg := SceneReplicationConfig.new()
	for m in cfg.get_method_list():
		if "propert" in m["name"] or "path" in m["name"]:
			print(m["name"], "/", m["args"].size())
	quit()
