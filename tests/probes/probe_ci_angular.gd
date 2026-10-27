extends Node

# Стадия 2, шаг 2.1 — проба №4: интегрирует ли движок ОРИЕНТАЦИЮ из angular_velocity
# при custom_integrator = 1? Если да — модель предсказания обязана повторить тот же
# шаг поворота, иначе Prediction Error по углу появится даже при нулевом лаге.
#
# Заодно фиксируем точное правило перемещения позиции: pos_N = pos_{N-1} + v_{N-1}*dt
# (скорость ПРЕДЫДУЩЕго шага, то есть explicit Euler), при damping = 0.

const BodyScript := preload("res://tests/probes/probe_ci_body.gd")
const DT := 1.0 / 60.0


func _ready() -> void:
	print("---- probe_ci_angular ----")
	var b: RigidBody3D = BodyScript.new()
	b.gravity_scale = 0.0
	b.linear_damp = 0.0
	b.angular_damp = 0.0
	b.can_sleep = false
	b.custom_integrator = true
	add_child(b)
	b.angular_velocity = Vector3(0.0, 3.0, 0.0)  # 3 рад/с вокруг Y
	b.linear_velocity = Vector3(0.0, 0.0, 6.0)
	b.reset_physics_interpolation()
	await get_tree().physics_frame  # кадр на регистрацию тела

	var calls0: int = b.if_calls
	for i in 10:
		await get_tree().physics_frame
	var steps: int = b.if_calls - calls0

	var basis: Basis = b.global_transform.basis
	var ang: float = basis.get_euler().y
	var expected: float = 3.0 * steps * DT
	print("   шагов интегратора = ", steps)
	print("   угловая скорость   = ", b.angular_velocity)
	print("   euler.Y тела       = ", "%.6f" % ang, "   (0 => движок НЕ вращает)")
	print("   ожидаемо w*dt      = ", "%.6f" % expected)
	print("   => движок сам вращает из angular_velocity: ", abs(ang - expected) < 0.01)

	var moved: Vector3 = b.global_position
	print("   позиция            = ", moved, "   |z|=", "%.6f" % moved.z)
	print("   v*dt*шагов         = ", "%.6f" % (6.0 * steps * DT))
	print("   => движок сам двигает из linear_velocity:  ", abs(moved.z - 6.0 * steps * DT) < 0.001)
	b.queue_free()
	print("-------------------------------------")
	get_tree().quit()
