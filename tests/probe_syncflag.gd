extends SceneTree

# Что делает add_property со свойством sync (входит ли свойство в дельта-рассылку,
# а не только в спавн-снимок)? Если sync=false по умолчанию — реплика ходит только
# при спавне, и вся схема репликации NetState молча ломается.
func _init() -> void:
	var cfg := SceneReplicationConfig.new()
	cfg.add_property(NodePath("NetState:NetPosition"))
	print("in-memory: spawn=", cfg.property_get_spawn(NodePath("NetState:NetPosition")),
		  " sync=", cfg.property_get_sync(NodePath("NetState:NetPosition")),
		  " mode=", cfg.property_get_replication_mode(NodePath("NetState:NetPosition")))
	cfg.add_property(NodePath("NetState:NetRotation"), true)
	print("add_property(path,true): spawn=", cfg.property_get_spawn(NodePath("NetState:NetRotation")),
		  " sync=", cfg.property_get_sync(NodePath("NetState:NetRotation")))
	cfg.property_set_sync(NodePath("NetState:NetPosition"), true)
	print("after set_sync(true): sync=", cfg.property_get_sync(NodePath("NetState:NetPosition")))
	var err := ResourceSaver.save(cfg, "res://tests/probe_syncflag.tres")
	print("save err=", err)
	var back := load("res://tests/probe_syncflag.tres") as SceneReplicationConfig
	print("reload: sync(pos)=", back.property_get_sync(NodePath("NetState:NetPosition")),
		  " sync(rot)=", back.property_get_sync(NodePath("NetState:NetRotation")))
	quit()
