extends Node
func _ready() -> void:
	var s := MultiplayerSynchronizer.new()
	add_child(s)
	print("has SetVisibilityFor = ", s.has_method("set_visibility_for"))
	print("has GetVisibilityFor = ", s.has_method("get_visibility_for"))
	print("has AddVisibilityFilter = ", s.has_method("add_visibility_filter"))
	print("PublicVisibility = ", s.public_visibility)
	var cfg := SceneReplicationConfig.new()
	print("cfg has_property = ", cfg.has_method("has_property"), " property_get_spawn = ", cfg.has_method("property_get_spawn"), " get_properties = ", cfg.has_method("get_properties"))
	get_tree().quit()
