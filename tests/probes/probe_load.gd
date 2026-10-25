extends SceneTree
func _init() -> void:
	var tres = ResourceLoader.load("res://scenes/ShipReplication.tres")
	print("tres=", tres)
	if tres != null:
		print("props=", (tres as SceneReplicationConfig).GetProperties().Length)
	var sc = ResourceLoader.load("res://scenes/Ship.tscn")
	print("scene=", sc)
	quit()
