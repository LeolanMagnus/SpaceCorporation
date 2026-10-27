extends Node3D

# Проба: можно ли двигать RigidBody3D с Freeze=true/Static, записывая
# GlobalTransform напрямую? Нужно для шага 2.4: пилот-клиент заморожен
# (не имеет права двигать истину), но обязан вести КАРТИНКУ и камеру по
# собственному предсказанию. Если запись в замороженное тело не приживается —
# придётся двигать ShipHull+камеру отдельно.

func _ready() -> void:
	var body := RigidBody3D.new()
	body.name = "FrozenBody"
	add_child(body)
	body.freeze = true
	body.freeze_mode = RigidBody3D.FREEZE_MODE_STATIC
	var shape := CollisionShape3D.new()
	var box := BoxShape3D.new()
	shape.shape = box
	body.add_child(shape)

	# Ждём один физкадр, чтобы тело «осело» в дереве.
	await PhysicsFrames(2)

	var target := Vector3(10, 0, 0)
	body.global_position = target
	var after_write := body.global_position

	await PhysicsFrames(3)
	var after_frames := body.global_position

	print("PROBE frozen_move: after_write=", after_write, " after_3_frames=", after_frames)
	print("PROBE result: write_sticks=", after_write.is_equal_approx(target),
		" persists=", after_frames.is_equal_approx(target))
	get_tree().quit()

func PhysicsFrames(n: int) -> void:
	for _i in n:
		await get_tree().physics_frame
