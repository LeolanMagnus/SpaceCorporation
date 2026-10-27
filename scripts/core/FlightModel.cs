using Godot;

namespace SpaceCorporation;

/// <summary>
/// Состояние симуляции одного корабля в «чистом» виде (stage2-plan §1.1).
/// Никаких узлов, трансформ и времени — только числа. Именно поэтому один и тот
/// же код считается на сервере (истина) и на клиенте (предсказание/переигровка)
/// и сходится побитово (тест п.1).
///
/// `Rotation` — ориентация корпуса, из неё модель берёт локальные оси тяги.
/// `AngularVelocity` — угловая скорость в МИРОВЫХ осях (как у `RigidBody3D`),
/// момент от клавиш задаётся в локальных осях и переводится базисом.
/// </summary>
public struct FlightState
{
    public Vector3 Position;
    public Quaternion Rotation;
    /// <summary>Линейная скорость в мировых осях.</summary>
    public Vector3 LinearVelocity;
    /// <summary>
    /// Угловая скорость в МИРОВЫХ осях — так же, как её хранит `RigidBody3D`
    /// (`GetAngularVelocity()`), поэтому запись в тело и чтение из тела не требуют
    /// пересчёта. Момент от клавиш задаётся в локальных осях корпуса и переводится
    /// в мировые базисом (`FlightModel.Step`), локальные оси нужны только для ввода.
    /// </summary>
    public Vector3 AngularVelocity;

    /// <summary>Покой в мире (0,0,0) без ориентации — стартовое состояние прогона.</summary>
    public static FlightState Park()
        => new() { Position = Vector3.Zero, Rotation = Quaternion.Identity,
                   LinearVelocity = Vector3.Zero, AngularVelocity = Vector3.Zero };

    /// <summary>Снять состояние с узла (только на авторитете, см. Ship).
    /// `GlobalRotation` в C# — это эйлеровы углы (Vector3), поэтому ориентацию
    /// берём из базиса: `GlobalTransform.Basis.GetRotationQuaternion()`.</summary>
    public static FlightState FromNode(Node3D n, Vector3 lin, Vector3 angWorld)
        => new()
        {
            Position = n.GlobalPosition,
            Rotation = n.GlobalTransform.Basis.GetRotationQuaternion(),
            LinearVelocity = lin,
            AngularVelocity = angWorld,
        };

    /// <summary>
    /// Смешать два состояния (для мягкой реконсиляции и для интерполяции чужого
    /// корабля). Углы интерполируются сферически, скорости — линейно.
    /// </summary>
    public static FlightState Lerp(FlightState a, FlightState b, float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return new FlightState
        {
            Position = a.Position.Lerp(b.Position, t),
            Rotation = a.Rotation.Slerp(b.Rotation, t),
            LinearVelocity = a.LinearVelocity.Lerp(b.LinearVelocity, t),
            AngularVelocity = a.AngularVelocity.Lerp(b.AngularVelocity, t),
        };
    }
}

/// <summary>
/// Параметры движения корабля (stage2-plan §1.1). Значения — настройки МОДЕЛИ, а
/// не движка: при `CustomIntegrator = true` движок не применяет ни демпфирование,
/// ни гравитацию (пробы `tests/probes/probe_custom_integrator.*`,
/// `probe_ci_angular.*`), поэтому всё считает `FlightModel`.
/// </summary>
public struct FlightParams
{
    /// <summary>Масса, кг. Нужна для импульсов (этап 4/5) и для тяги в ньютонах.</summary>
    public float Mass;
    /// <summary>Тяга по каждой оси, Н. Ускорение = LinearThrust / Mass.</summary>
    public float LinearThrust;
    /// <summary>Момент по каждой оси, Н·м.</summary>
    public float AngularThrust;
    /// <summary>Линейное демпфирование, 1/с (экспоненциальное затухание).</summary>
    public float LinearDamping;
    /// <summary>Угловое демпфирование, 1/с.</summary>
    public float AngularDamping;
    /// <summary>Множитель тяги при форсаже (бит Boost).</summary>
    public float BoostMultiplier;

    /// <summary>Значения по умолчанию = то, что сейчас стоит на Ship.cs:86-87.</summary>
    public static FlightParams Default()
        => new()
        {
            Mass = 1200f,
            LinearThrust = 90000f,
            AngularThrust = 40000f,
            LinearDamping = 0.15f,
            AngularDamping = 0.4f,
            BoostMultiplier = 2f,
        };
}

/// <summary>
/// Чистый интегратор движения (stage2-plan §1.1/§1.2). Один вызов = один
/// физический шаг. Обязателен порядок операций (пробы №4/№5: движок Godot
/// двигает тело скоростью ПРЕДЫДУЩЕГО шага — explicit Euler):
///
///   1. позиция += линейная скорость (до изменений) * dt
///   2. ориентация *= поворот от угловой скорости (до изменений) * dt
///   3. скорости меняются: тяга/момент, затем демпфирование exp(-k*dt)
///
/// Демпфирование считается моделью, а не движком, и обязательно: без него
/// корабль разгоняется до бесконечности.
/// </summary>
public static class FlightModel
{
    /// <summary>Шаг физики = physics_ticks_per_second (project.godot:48).</summary>
    public const float FixedDt = 1f / 60f;

    /// <summary>
    /// Шаг на величину dt. Возвращает новое состояние; вход не меняется
    /// (struct-семантика нужна для буфера предсказания и переигровки).
    /// </summary>
    public static FlightState Step(FlightState s, FlightParams p, InputFrame f, float dt)
    {
        if (dt <= 0f) return s;

        Basis basis = new Basis(s.Rotation);

        // --- 1-2. Интегрирование положения/ориентации ТЕКУЩИМИ скоростями ---
        FlightState n = s;
        n.Position = s.Position + s.LinearVelocity * dt;

        if (s.AngularVelocity != Vector3.Zero)
        {
            // Угловая скорость — в мировых осях (как у RigidBody3D), поэтому
            // прирост поворота строится прямо от неё, без пересчёта в базис.
            Vector3 w = s.AngularVelocity;
            float wLen = w.Length();
            if (wLen > 1e-8f)
            {
                Quaternion dq = new Quaternion(w / wLen, wLen * dt);
                // Нормализация обязательна: без неё кватернион ползёт по длине,
                // а Basis(kv) изненормализованного кватерниона даёт масштаб —
                // тяга и запись в тело поехали бы по модулю (тест п.1).
                n.Rotation = (dq * s.Rotation).Normalized();
            }
        }

        // --- 3. Тяга: локальные оси, мировое ускорение ---
        float boost = ((f.Mask & InputMask.Boost) != 0) ? Mathf.Max(1f, p.BoostMultiplier) : 1f;
        float accelScale = (p.Mass > 0f ? p.LinearThrust / p.Mass : 0f) * boost;

        // Ось Z знакоразвёрнута: «вперёд» в контенте Godot — это локальная -Z
        // (см. Ship.tscn, импульс в ProbeNest идёт по -Z).
        Vector3 localThrust = new Vector3(
            f.AxisR - f.AxisL,
            f.AxisU - f.AxisD,
            f.AxisB - f.AxisF);
        // Ограничение диагонали, чтобы два одновременных ввода не давали √2 тяги.
        if (localThrust.LengthSquared() > 1f) localThrust = localThrust.Normalized();

        Vector3 worldThrust = basis * localThrust * accelScale;

        // --- 4. Момент: задаётся в ЛОКАЛЬНЫХ осях корпуса (X — тангаж, Y —
        // рыскание, Z — крен), но угловая скорость хранится в мировых, поэтому
        // момент переводится в мировые тем же базисом. Иначе предсказание пилота
        // и сервер сошлись бы только при identity-ориентации.
        float angAccelScale = (p.Mass > 0f ? p.AngularThrust / p.Mass : 0f) * boost;
        Vector3 localTorque = new Vector3(
            f.AxisPitchUp - f.AxisPitchDown,
            f.AxisYawRight - f.AxisYawLeft,
            0f);
        if (localTorque.LengthSquared() > 1f) localTorque = localTorque.Normalized();

        // --- 5. Скорости ---
        n.LinearVelocity = s.LinearVelocity + worldThrust * dt;
        n.AngularVelocity = s.AngularVelocity + basis * localTorque * angAccelScale * dt;

        // --- 6. Демпфирование (модель, не движок) ---
        if (p.LinearDamping > 0f)
            n.LinearVelocity *= Mathf.Exp(-p.LinearDamping * dt);
        if (p.AngularDamping > 0f)
            n.AngularVelocity *= Mathf.Exp(-p.AngularDamping * dt);

        return n;
    }

    /// <summary>
    /// Переигровка: прогнать состояние через набор кадров ввода (реконсиляция на
    /// пилоте, stage2-plan §1.6). Кадры идут строго по возрастанию Tick.
    /// </summary>
    public static FlightState Replay(FlightState from, FlightParams p,
                                     System.Collections.Generic.IReadOnlyList<InputFrame> frames,
                                     uint afterTick, float dt)
    {
        FlightState s = from;
        foreach (InputFrame f in frames)
        {
            if (f.Tick <= afterTick) continue;
            s = Step(s, p, f, dt);
        }
        return s;
    }
}
