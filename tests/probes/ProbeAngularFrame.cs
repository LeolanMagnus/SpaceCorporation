using Godot;

namespace SpaceCorporation;

/// <summary>Проба v2: в какой СИСТЕМЕ движок (Godot 4.7 / Jolt) предварительное
/// вращает базис при CustomIntegrator. Пишем на выходе 2-го шага базис =
/// поворот 90° вокруг мировой Y; ω держим мировой X = W. На следующем входе
/// проверяем две гипотезы:
///   A (мировая):  entry = Basis(ω·dt) × written
///   B (локальная): entry = written × Basis(ω·dt)
/// Выигрывает та, у которой угол расхождения ≈ 0.</summary>
public partial class ProbeAngularFrame : RigidBody3D
{
    private Quaternion _lastWritten = Quaternion.Identity;
    private int _calls;
    private const float W = 1f;

    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        _calls++;
        float dt = sd.GetStep();
        Quaternion entry = sd.Transform.Basis.GetRotationQuaternion();
        if (_calls >= 3 && _calls <= 5)
        {
            var dW = new Basis(new Quaternion(new Vector3(W, 0f, 0f), W * dt));
            // Гипотеза A: доворот слева (мировая система).
            float angA = entry.AngleTo((dW * new Basis(_lastWritten)).GetRotationQuaternion());
            // Гипотеза B: доворот справа (локальная система тела).
            float angB = entry.AngleTo((new Basis(_lastWritten) * dW).GetRotationQuaternion());
            GD.Print($"call {_calls}: A(мировая)={angA * 180f / Mathf.Pi:F5}° " +
                     $"B(локальная)={angB * 180f / Mathf.Pi:F5}° " +
                     $"|w|={sd.AngularVelocity.Length():F4} w={sd.AngularVelocity}");
        }
        sd.AngularVelocity = new Vector3(W, 0f, 0f); // мировой X
        if (_calls == 2)
        {
            var b = new Basis(Vector3.Up, Mathf.Pi / 2f); // 90° вокруг мировой Y
            sd.Transform = new Transform3D(b, sd.Transform.Origin);
        }
        _lastWritten = sd.Transform.Basis.GetRotationQuaternion();
        if (_calls >= 6) GetTree().Quit();
    }
}
