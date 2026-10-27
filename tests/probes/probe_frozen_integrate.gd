extends RigidBody3D

# Проба: вызывается ли _integrate_forces у замороженного (freeze=true/Static)
# тела с custom_integrator=true? Если да — пилот-клиент (заморожен, но
# custom_integrator=true) получил бы ДВОЙНУЮ интеграцию: раз в _physics_process
# (предсказание) и раз в _integrate_forces. Тогда надо ставить
# custom_integrator = false для не-авторитета.

var calls := 0

func _ready() -> void:
	freeze = true
	freeze_mode = RigidBody3D.FREEZE_MODE_STATIC
	custom_integrator = true
	var shape := CollisionShape3D.new()
	shape.shape = BoxShape3D.new()
	add_child(shape)

func _integrate_forces(_state: PhysicsDirectBodyState3D) -> void:
	calls += 1

func _physics_process(_d: float) -> void:
	if Engine.get_physics_frames() >= 5:
		print("PROBE integrate_forces_frozen: calls=", calls,
			" fires_when_frozen=", calls > 0)
		get_tree().quit()
