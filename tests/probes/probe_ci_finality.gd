extends Node

# Стадия 2, шаг 2.1 — проба №3: финальны ли наши записи позиции/скорости внутри
# _integrate_forces для данного шага?
#
# Если pos_at_entry (то, что застал наш вызов) уже сдвинуто движком на v_prev*dt, а
# pos_after_step (после полного шага) равно ровно нашей записи — значит вызов идёт
# ПОСЛЕ пред-интегрирования движка, и модель может считать шаг целиком сама:
# пред-интегрирование безвредно, потому что мы перетираем позицию.

const BodyScript := preload("res://tests/probes/probe_ci_body.gd")


func _ready() -> void:
	print("---- probe_ci_finality ----")
	var b: RigidBody3D = BodyScript.new()
	b.gravity_scale = 0.0
	b.linear_damp = 0.0
	b.angular_damp = 0.0
	b.can_sleep = false
	b.custom_integrator = true
	add_child(b)
	b.linear_velocity = Vector3(4.0, 0.0, 0.0)
	b.reset_physics_interpolation()
	await get_tree().physics_frame  # кадр на регистрацию тела

	b.write_pos_each_step = true
	b.write_pos = Vector3(100.0, 0.0, 0.0)
	b.write_vel = Vector3(0.0, 0.0, 1.0)
	for i in 3:
		await get_tree().physics_frame
	print("   pos_at_entry  (что застал наш вызов) = ", b.pos_at_entry)
	print("   pos_after_step(после полного шага)   = ", b.pos_after_step)
	print("   v                                    = ", b.linear_velocity)
	print("   => запись позиции финальна для шага: ",
		  b.pos_after_step.distance_to(Vector3(100.0, 0.0, 0.0)) < 0.0001)
	print("   => движок пред-интегрировал до нас:  ",
		  b.pos_at_entry.distance_to(Vector3(100.0, 0.0, 0.0)) > 0.0001)
	b.queue_free()
	print("-------------------------------------")
	get_tree().quit()
