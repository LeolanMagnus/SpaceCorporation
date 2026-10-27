extends Node

# Стадия 2, шаг 2.1 — проба №2: из какой скорости движок сдвигает тело при
# custom_integrator = 1: из записанной НАМИ внутри _integrate_forces или из той,
# что была до нашего вызова?
#
# Это определяет, как модель предсказания обязана считать pos, чтобы совпасть с
# сервером: pos += v_new*dt или pos += v_old*dt.

const BodyScript := preload("res://tests/probes/probe_ci_body.gd")
const STEPS := 10


func _ready() -> void:
	print("---- probe_ci_order ----")
	await _case("не пишем скорость (v=const)", false, Vector3.ZERO, Vector3(4.0, 0.0, 0.0))
	await _case("пишем скорость 0 внутри", true, Vector3.ZERO, Vector3(4.0, 0.0, 0.0))
	await _case("пишем скорость (0,0,9) внутри", true, Vector3(0.0, 0.0, 9.0), Vector3(4.0, 0.0, 0.0))
	print("-------------------------------------")
	get_tree().quit()


func _case(label: String, write: bool, written: Vector3, initial: Vector3) -> void:
	var b: RigidBody3D = BodyScript.new()
	b.gravity_scale = 0.0
	b.linear_damp = 0.0
	b.angular_damp = 0.0
	b.can_sleep = false
	b.custom_integrator = true
	add_child(b)
	b.linear_velocity = initial
	b.reset_physics_interpolation()
	await get_tree().physics_frame  # кадр на регистрацию тела в физическом мире
	b.write_vel = write
	b.new_vel = written
	var calls0: int = b.if_calls
	var p0: Vector3 = b.global_position
	for i in STEPS:
		await get_tree().physics_frame
	var steps: int = b.if_calls - calls0
	var moved: Vector3 = b.global_position - p0
	var dt: float = 1.0 / Engine.physics_ticks_per_second
	print(label)
	print("   v_до=", initial, "  v_записано=", (written if write else Vector3.INF))
	print("   шагов=", steps, "  смещение=", moved, "  |смещение|=", "%.5f" % moved.length())
	print("   v_на входе в integrate_forces=", b.vel_at_entry)
	print("   итоговая v=", b.linear_velocity)
	print("   гипотеза A (после нашей записи): ", (written if write else initial) * steps * dt)
	print("   гипотеза B (до нашей записи):    ", initial * steps * dt)
	b.queue_free()
	await get_tree().process_frame
