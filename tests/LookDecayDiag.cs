using Godot;

namespace SpaceCorporation;

/// <summary>Разовая диагностика: при монотонном спаде ω (команда ноль) нос
/// ушёл на 2× интеграла ∫ω dt. Печатает Δyaw и |ω| почастотно.</summary>
public partial class LookDecayDiag : Node3D
{
    public override void _Ready()
    {
        float dt = FlightModel.FixedDt;
        PackedScene scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        Ship s = scene.Instantiate<Ship>();
        AddChild(s);
        s.TestSetPilotForTest(1);
        s.TestSetVelocity(Vector3.Zero);
        // Тип по умолчанию в NetState — Courier (каталог: 3.5 рад/с, τ-набор 0.6 с).

        // Мышиный канал: кадр несёт look в радианах за шаг. Полная команда
        // (курсор за кругом полной скорости) = MaxAngularSpeed·dt на ось.
        float fullLook = s.Flight.MaxAngularSpeed * dt;
        var f = new InputFrame { LookYaw = fullLook };
        // Разгон через look-доворот на потолок (2 с).
        for (int i = 0; i < 120; i++)
        {
            f.Tick = (uint)(i + 1);
            s.TestSetInputForTest(f);
            s.TestStepForTest(dt);
        }
        GD.Print($"w before decay = {s.AngularVelocity.Length():F4}");

        Vector3 fwd0 = s.GlobalTransform.Basis.Z;
        float wPrev = s.AngularVelocity.Length();
        float sumWdt = 0f;
        for (int i = 0; i < 40; i++)
        {
            var empty = new InputFrame { Tick = (uint)(200 + i) };
            s.TestSetInputForTest(empty);
            Vector3 fwdBefore = s.GlobalTransform.Basis.Z;
            s.TestStepForTest(dt);
            float w = s.AngularVelocity.Length();
            sumWdt += w * dt;
            float dAng = fwdBefore.AngleTo(s.GlobalTransform.Basis.Z);
            if (i % 5 == 0 || i < 3)
                GD.Print($"frame {i}: ω {wPrev:F4} → {w:F4}, Δyaw-за-шаг={dAng:F5}, ω·dt={(w * dt):F5}");
            wPrev = w;
        }
        float turned = fwd0.AngleTo(s.GlobalTransform.Basis.Z);
        GD.Print($"итог: угол={turned:F4} рад, сумма ω·dt={sumWdt:F4} рад");
        GetTree().Quit();
    }
}
