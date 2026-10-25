@tool
extends SceneTree

# Разведка формата сериализации SceneReplicationConfig: пишем конфиг и читаем .tres.
func _init() -> void:
	var cfg := SceneReplicationConfig.new()
	cfg.add_property(NodePath("NetState/NetPosition"))
	cfg.property_set_spawn(NodePath("NetState/NetPosition"), true)
	cfg.property_set_replication_mode(NodePath("NetState/NetPosition"),
		SceneReplicationConfig.REPLICATION_MODE_ON_CHANGE)
	cfg.add_property(NodePath("NetState/SimTick"))
	cfg.property_set_sync(NodePath("NetState/SimTick"), false)
	cfg.property_set_replication_mode(NodePath("NetState/SimTick"),
		SceneReplicationConfig.REPLICATION_MODE_ON_CHANGE)
	var err := ResourceSaver.save(cfg, "res://tests/probes/probe_replcfg.tres")
	print("save err=", err)
	print("count=", cfg.get_property_list().size())
	for i in cfg.get_property_list().size():
		print(i, " path=", cfg.get_property_list()[i])
	quit()
