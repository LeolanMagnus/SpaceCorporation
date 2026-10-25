extends SceneTree

# Проверка исправленного ShipReplication.tres: 6 свойств с корректными spawn/mode.
func _init() -> void:
	var cfg := load("res://scenes/ShipReplication.tres") as SceneReplicationConfig
	var props: Array = cfg.get_properties()
	print("count=", props.size())
	for p in props:
		print("path=", p, " spawn=", cfg.property_get_spawn(p),
			  " mode=", cfg.property_get_replication_mode(p),
			  " sync=", cfg.property_get_sync(p))
	quit()
