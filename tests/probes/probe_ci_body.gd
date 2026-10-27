extends RigidBody3D

# Проба порядка (стадия 2, шаг 2.1): пишем внутри _integrate_forces позицию и
# скорость КАЖДЫЙ шаг. Если после шага позиция ровно наша — значит вызов идёт
# ПОСЛЕ того, как движок сам сдвинул тело, и наши записи финальны для шага
# (то есть модель может считать шаг целиком сама, а пред-интегрирование движка
# безвредно, потому что мы перетираем позицию).

var if_calls := 0
var last_dt := -1.0
var write_pos_each_step := false
var write_pos := Vector3(100.0, 0.0, 0.0)
var write_vel := Vector3(0.0, 0.0, 1.0)
var pos_at_entry := Vector3.INF   # позиция, которую застал наш вызов (после пред-интегрирования движком)
var pos_after_step := Vector3.INF # позиция после полного шага (из _physics_process)


func _integrate_forces(sd: PhysicsDirectBodyState3D) -> void:
	if_calls += 1
	last_dt = sd.get_step()
	pos_at_entry = sd.transform.origin
	if write_pos_each_step:
		sd.transform.origin = write_pos
		sd.linear_velocity = write_vel


func _physics_process(_delta: float) -> void:
	pos_after_step = global_position
