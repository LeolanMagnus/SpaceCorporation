extends SceneTree
func _init() -> void:
	print("min=", ResourceLoader.load("res://tests/probe_min.tres"))
	quit()
