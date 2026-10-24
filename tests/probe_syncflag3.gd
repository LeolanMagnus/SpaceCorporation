extends SceneTree

# Сериализуется ли ключ sync в .tres? (probe_syncflag2.tres написан вручную)
func _init() -> void:
	var back := load("res://tests/probe_syncflag2.tres") as SceneReplicationConfig
	if back == null:
		print("load failed")
		quit()
		return
	print("count=", back.get_properties().size())
	for p in back.get_properties():
		print("path=", p, " spawn=", back.property_get_spawn(p),
			  " sync=", back.property_get_sync(p), " mode=", back.property_get_replication_mode(p))
	quit()
