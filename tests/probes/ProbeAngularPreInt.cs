using Godot;

namespace SpaceCorporation;

/// <summary>Проба: при CustomIntegrator движок ДО вызова _integrate_forces
/// сдвигает тело линейной скоростью (подтверждено) — а ВРАЩАЕТ ли базис
/// угловой скоростью? Меряем угол между базисом на входе текущего вызова и
/// базисом, который мы записали на выходе прошлого.</summary>
public partial class ProbeAngularPreInt : RigidBody3D
{
    private Quaternion _lastWritten = Quaternion.Identity;
    private int _calls;
    private const float W = 1f; // рад/с вокруг мировой Y

    public override void _Ready()
    {
        CustomIntegrator = true;
        ContinuousCd = false;
        ContactMonitor = false;
        Sleeping = false;
        _lastWritten = GlobalTransform.Basis.GetRotationQuaternion();
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        _calls++;
        Vector3 w0 = sd.AngularVelocity;
        Quaternion entry = sd.Transform.Basis.GetRotationQuaternion();
        if (_calls >= 2)
        {
            float drift = entry.AngleTo(_lastWritten) * (180f / Mathf.Pi);
            GD.Print($"call {_calls}: дрейф базиса на входе = {drift:F5}° " +
                     $"(ожидание 0 если нет пре-вращения; ~{W * sd.GetStep():F5}° если движок вращает) " +
                     $"|w0|={w0.Length():F4}");
        }
        if (_calls == 1)
        {
            sd.AngularVelocity = new Vector3(0f, W, 0f);
        }
        // Пишем базис обратно как есть, но запомним его как «наш выход».
        sd.Transform = new Transform3D(new Basis(entry), sd.Transform.Origin);
        _lastWritten = sd.Transform.Basis.GetRotationQuaternion();
        if (_calls >= 6)
        {
            GD.Print($"ПРОБЕЖАЛИ: итог — дрейф на входе {entry.AngleTo(_lastWritten) * (180f / Mathf.Pi):F5}° за шаг");
            GetTree().Quit();
        }
    }
}
