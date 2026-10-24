extends SceneTree

# Разведка API MultiplayerSynchronizer / SceneReplicationConfig в Godot 4.7:
# какие методы есть для visibility-фильтров и как читаются spawn-флаги конфига.
func _init() -> void:
	var res := load("res://scenes/Ship.tscn")
	var ship = res.instantiate()
	get_root().add_child(ship)
	var sync: MultiplayerSynchronizer = ship.get_node("Synchronizer")
	print("PublicVisibility=", sync.public_visibility)
	var names := []
	for m in sync.get_method_list():
		if "visib" in m["name"] or "filter" in m["name"]:
			names.append(m["name"])
	print("sync visibility methods=", names)
	print("visibility_affects_enabled=", sync.has_method("set_visibility_for"))
	var cfg := sync.replication_config
	print("cfg count=", cfg.get_property_list().size())
	for p in cfg.get_property_list():
		print("entry=", p)
	print("spawn(NetState/NetPosition)=", cfg.property_get_spawn(NodePath("NetState/NetPosition")))
	print("spawn(bogus)=", cfg.property_get_spawn(NodePath("NetPosition")))
	sync.set_visibility_for(2, false)
	print("after set_visibility_for: queued_for_deletion=", ship.is_queued_for_deletion(),
		  " public=", sync.public_visibility)
	print("has_visibility_filter methods=", sync.has_method("get_visibility_filter_count"))
	quit()
