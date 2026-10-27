extends Node

# Стадия 2, шаг 2.1 — проба поведения RigidBody3D с custom_integrator = 1.
#
# Вопросы, на которые отвечает проба (от них зависит конструкция FlightModel):
#  1. Применяет ли движок linear_damp/angular_damp сам? Если да — наша модель
#     посчитает демпфирование ВТОРОЙ раз (корабль тормозит вдвое сильнее).
#  2. Применяет ли движок гравитацию?
#  3. Интегрирует ли движок ПОЗИЦИЮ из linear_velocity при custom_integrator = 1?
#     Если да — писать позицию руками нельзя (двойное интегрирование), надо
#     писать только скорость и повторять правило pos += v*dt в модели предсказания.
#  4. Доезжают ли до движка записи, сделанные внутри _integrate_forces через
#     PhysicsDirectBodyState3D, и какой там dt.

const BodyScript := preload("res://tests/probes/probe_ci_body.gd")

const DAMP_LIN := 2.0
const DAMP_ANG := 3.0
const STEPS := 10


func _ready() -> void:
	print("---- probe_custom_integrator ----")
	print("physics_ticks_per_second = ", Engine.physics_ticks_per_second,
		  "  expected dt = ", 1.0 / Engine.physics_ticks_per_second)
	await _damping_and_gravity(false)
	await _damping_and_gravity(true)
	await _position_integration()
	print("-------------------------------------")
	get_tree().quit()


func _make_body(custom: bool) -> RigidBody3D:
	var b: RigidBody3D = BodyScript.new()
	b.gravity_scale = 0.0
	b.linear_damp = DAMP_LIN
	b.angular_damp = DAMP_ANG
	b.can_sleep = false
	b.custom_integrator = custom
	add_child(b)
	return b


# --- 1..2: демпфирование и гравитация ---------------------------------------
func _damping_and_gravity(custom: bool) -> void:
	var b := _make_body(custom)
	b.gravity_scale = 1.0
	b.linear_velocity = Vector3(10.0, 0.0, 0.0)
	b.angular_velocity = Vector3(0.0, 6.0, 0.0)
	b.reset_physics_interpolation()
	for i in STEPS:
		await get_tree().physics_frame
	print("custom_integrator=", custom)
	print("   if_calls = ", b.if_calls, " (за ", STEPS, " physics_frame; 1 кадр — регистрация тела)")
	print("   last dt  = ", b.last_dt)
	print("   |v| после ", STEPS, " шагов = ", "%.5f" % b.linear_velocity.length(),
		  "   (10.0 => damp НЕ применяется, 7.2568 => применяется)")
	print("   |w| после ", STEPS, " шагов = ", "%.5f" % b.angular_velocity.length(),
		  "   (6.0  => damp НЕ применяется, 3.7222 => применяется)")
	print("   v.Y при gravity_scale=1 и default_gravity=0 = ", "%.5f" % b.linear_velocity.y)
	b.queue_free()
	await get_tree().process_frame


# --- 3: интегрирует ли движок позицию из скорости ---------------------------
func _position_integration() -> void:
	print("---- position integration ----")
	var b := _make_body(true)
	b.linear_damp = 0.0
	b.angular_damp = 0.0
	var start := Vector3(1.0, 2.0, 3.0)
	var vel := Vector3(4.0, 5.0, 6.0)
	b.global_position = start
	b.linear_velocity = vel
	b.reset_physics_interpolation()
	# Кадр на регистрацию тела в физическом мире.
	await get_tree().physics_frame
	var p0: Vector3 = b.global_position
	var calls0: int = b.if_calls
	for i in STEPS:
		await get_tree().physics_frame
	var p1: Vector3 = b.global_position
	var steps_done: int = b.if_calls - calls0
	var moved: Vector3 = p1 - p0
	print("custom=true, v=", vel, " damp=0")
	print("   шагов интегратора = ", steps_done)
	print("   смещение тела = ", moved, "   |смещение| = ", "%.5f" % moved.length())
	print("   v*dt*шагов        = ", vel * steps_done / Engine.physics_ticks_per_second)
	print("   => движок сам двигает тело из скорости: ",
		  moved.distance_to(vel * steps_done / Engine.physics_ticks_per_second) < 0.001)
	print("   v после = ", b.linear_velocity, " (damp=0 => не меняется)")
	b.queue_free()
	await get_tree().process_frame

	# 4: запись внутри _integrate_forces через direct state.
	print("---- write inside _integrate_forces ----")
	var b2 := _make_body(true)
	b2.linear_damp = 0.0
	b2.angular_damp = 0.0
	b2.write_mode = 2
	b2.write_pos = Vector3(100.0, 0.0, 0.0)
	b2.write_vel = Vector3(0.0, 0.0, 1.0)
	b2.reset_physics_interpolation()
	for i in 3:
		await get_tree().physics_frame
	print("   pos_after_integrate = ", b2.pos_after_integrate,
		  "  (100,0,0 => запись позиции из integrate_forces доехала)")
	print("   v = ", b2.linear_velocity, "  (0,0,1 => запись скорости доехала)")
	print("   if_calls = ", b2.if_calls)
	b2.queue_free()
