extends SceneTree
func _init() -> void:
	print("min=", ResourceLoader.load("res://tests/probes/probe_min.tres"))
	quit()
