extends SceneTree

# Разведка: какой формат пути принимает SceneReplicationConfig.add_property и как
# он сериализуется. Подозрение: нужен subname-синтаксис `NetState:NetPosition`,
# а не `NetState/NetPosition` (иначе _set отбрасывает запись, конфиг пуст).
func _init() -> void:
	var cfg := SceneReplicationConfig.new()
	cfg.add_property(NodePath("NetState:NetPosition"))
	cfg.property_set_spawn(NodePath("NetState:NetPosition"), true)
	cfg.property_set_replication_mode(NodePath("NetState:NetPosition"),
		SceneReplicationConfig.REPLICATION_MODE_ON_CHANGE)
	cfg.add_property(NodePath("NetState/SimTick"))  # сломанный вариант для сравнения
	print("spawn(NetState:NetPosition)=", cfg.property_get_spawn(NodePath("NetState:NetPosition")))
	print("spawn(NetState/SimTick)=", cfg.property_get_spawn(NodePath("NetState/SimTick")))
	var err := ResourceSaver.save(cfg, "res://tests/probe_fmt.tres")
	print("save err=", err)
	var back := load("res://tests/probe_fmt.tres") as SceneReplicationConfig
	print("reload spawn=", back.property_get_spawn(NodePath("NetState:NetPosition")))
	quit()
