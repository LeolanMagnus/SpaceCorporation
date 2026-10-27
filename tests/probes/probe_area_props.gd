extends Node
func _ready() -> void:
	var a := Area3D.new()
	for p in a.get_property_list():
		var n: String = p["name"]
		if n.find("grav") >= 0 or n.find("space_override") >= 0:
			print(n, " = ", a.get(n))
	get_tree().quit()
