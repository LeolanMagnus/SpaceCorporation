using Godot;
using System;

namespace SpaceCorporation.Tests;

/// <summary>
/// Тело-проба для теста п.2 (тандем «RigidBody3D с CustomIntegrator ↔ FlightModel»).
///
/// Смысл класса: у `RigidBody3D` нельзя переопределить `_IntegrateForces` «снаружи»,
/// поэтому здесь просто есть делегат, который тест назначает извне. Всё остальное —
/// как у Ship на авторитете: `CustomIntegrator = true`, `GravityScale = 0`,
/// демпфирование движка = 0 (его считает модель), тело заморожено в Static, чтобы
/// движок не двигал его сам.
///
/// Проба отвечает на вопрос, который нельзя проверить чистой математикой: применяются
/// ли записанные внутри `_IntegrateForces` позиция/ориентация/скорости как финальные
/// для шага (пробы probe_ci_finality/probe_ci_order — про движок; здесь — про то,
/// что наша модель и тело сходятся после N кадров).
/// </summary>
public partial class ProbeBody : RigidBody3D
{
    /// <summary>Вызывается из `_IntegrateForces`. Тест сам считает шаг и сам
    /// пишет состояние в `PhysicsDirectBodyState3D`.</summary>
    public Action<PhysicsDirectBodyState3D> StepHandler;

    /// <summary>Счётчик вызовов интегратора: равен числу физических кадров, в
    /// которых тело было в дереве и активно (тест п.2 сверяет с ним модель).</summary>
    public int IntegrateCalls = 0;

    /// <summary>dt последнего шага, как его видит движок (ожидание = 1/60).</summary>
    public double LastStep = -1.0;

    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        IntegrateCalls++;
        LastStep = sd.GetStep();
        StepHandler?.Invoke(sd);
    }
}
