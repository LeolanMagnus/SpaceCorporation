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
    /// <summary>
    /// Линейное ускорение тяги, м/с² (Этап 6, Р2). > 0 ⇒ модель берёт ускорение
    /// НАПРЯМУЮ, минуя LinearThrust/Mass — это путь модулей и ассиста (модуль
    /// описывает м/с²). 0 (структурный ноль) ⇒ как раньше: LinearThrust/Mass,
    /// поэтому существующие прогоны считают ровно те же числа.
    /// </summary>
    public float ThrustAccel;
    /// <summary>Угловое ускорение момента, рад/с². > 0 ⇒ напрямую; 0 ⇒ AngularThrust/Mass.</summary>
    public float AngularAccel;
    /// <summary>Линейное демпфирование, 1/с (экспоненциальное затухание).</summary>
    public float LinearDamping;
    /// <summary>Угловое демпфирование, 1/с.</summary>
    public float AngularDamping;
    /// <summary>Множитель тяги при форсаже (бит Boost).</summary>
    public float BoostMultiplier;
    /// <summary>
    /// Максимальная угловая скорость корабля, рад/с (модуль `AngularVelocity`,
    /// т.е. любая ось: тангаж/рысканье/крен). 0 или меньше = без ограничения.
    /// Это характеристика корабля: у тяжёлого freighter она меньше, у лёгкого
    /// истребителя больше. Ограничивает и ручное управление (Q/E, Z/X/C/V), и
    /// мышь (см. <see cref="ClampLookRadians"/>), и доводку при стыковке.
    ///
    /// Поле НЕ задаётся в `Default()` и в тестовых `Params()`: структурный
    /// ноль = «без ограничения», поэтому существующие прогоны (детерминизм,
    /// тандем с телом, затухание демпфером) считают ровно те же числа.
    /// </summary>
    public float MaxAngularSpeed;

    // --- Этап 6: пилот-ассист и потолок линейной скорости («жидкий космос») ---
    // Все четыре поля имеют «структурный ноль = прежнее поведение»: старые
    // тестовые прогоны (Params() в Stage2Test и пр.) оставляют их нулевыми и
    // считают ровно те же числа, что и до Этапа 6.

    /// <summary>
    /// Поддержка пилотирования ВКЛЮЧЕНА. true ⇒ при отсутствии линейного ввода
    /// скорость гасится постоянным ускорением `AssistDragAccel` («жидкий космос»).
    /// false (структурный ноль) ⇒ инерция сохраняется вечно — текущее поведение.
    /// </summary>
    public bool PilotAssist;
    /// <summary>
    /// Величина торможения ассиста, м/с². По условию равна `ThrustAccel`:
    /// «падает с таким же ускорением, как набирается». Действует ТОЛЬКО при
    /// пустом линейном вводе (см. шаг 5b в `Step`) — иначе при равных ускорениях
    /// тяга и торможение обнулили бы друг друга и корабль не сдвинулся бы.
    /// 0 = ассист не тормозит.
    /// </summary>
    public float AssistDragAccel;
    /// <summary>
    /// Потолок линейной скорости («макс. скорость стрейфа»), м/с. 0/меньше =
    /// без ограничения (структурный ноль = прежнее поведение). Клиппится
    /// ОСЕВОЙ клиппинг по мировым осям (направление сохраняется; при общем
    /// лимите на все оси это то же, что покомпонентная проекция на локальные).
    /// </summary>
    public float MaxLinearSpeed;
    /// <summary>
    /// «Текучесть» при выключенном РСУ/питании: экспоненциальное демпфирование
    /// линейной скорости, 1/с. Отдельно от `AssistDragAccel` (это торможение) и
    /// от `LinearDamping` (на нём висят старые тесты). В прототипе = 0 и в `Step`
    /// пока не участвует — задел под «выключенное РСУ = космос чуть густой».
    /// </summary>
    public float FreeDriftDamping;

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
            MaxAngularSpeed = 0f, // 0 = без ограничения (см. документацию поля)
        };
}

/// <summary>
/// Чистый интегратор движения (stage2-plan §1.1/§1.2). Один вызов = один
/// физический шаг. Обязателен порядок операций (пробы №4/№5: движок Godot
/// двигает тело скоростью ПРЕДЫДУЩЕГО шага — explicit Euler):
///
///   1. позиция += линейная скорость (до изменений) * dt
///   2. ориентация *= поворот от угловой скорости (до изменений) * dt
///   3. скорости меняются: тяга/момент, затем [Этап 6] торможение пилот-ассиста
///      (только при пустом линейном вводе), затем демпфирование exp(-k*dt),
///      затем лимиты: MaxAngularSpeed, [Этап 6] MaxLinearSpeed (осевой клиппинг)
///
/// Демпфирование считается моделью, а не движком: без него
/// корабль разгоняется до бесконечности (в режиме ассиста — см. 5b).
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

        // Лимит угловой скорости корабля (макс. рад/с по ЛЮБОЙ оси — модуль
        // мирового `AngularVelocity`). Гейтим и вход шага, и выход:
        //  • вход — чтобы интегрирование поворота ниже шло скоростью, которая
        //    уже под лимитом (иначе допущенное сверху состояние за этот шаг
        //    провернуло бы нос быстрее предельной скорости);
        //  • выход — чтобы момент с клавиш (Q/E, Z/X/C/V) или доводка стыковки
        //    не раскрутили сверх лимита.
        // `p.MaxAngularSpeed <= 0` = без ограничения (так стоят все старые
        // тестовые прогоны: структурный ноль меняет поведение ровно на «нет»).
        Vector3 w = s.AngularVelocity;
        if (p.MaxAngularSpeed > 0f && w.LengthSquared() > p.MaxAngularSpeed * p.MaxAngularSpeed)
            w = w.Normalized() * p.MaxAngularSpeed;

        Basis basis = new Basis(s.Rotation);

        // --- 1-2. Интегрирование положения/ориентации ТЕКУЩИМИ скоростями ---
        FlightState n = s;
        n.Position = s.Position + s.LinearVelocity * dt;

        if (w != Vector3.Zero)
        {
            // Угловая скорость — в мировых осях (как у RigidBody3D), поэтому
            // прирост поворота строится прямо от неё, без пересчёта в базис.
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
        // Этап 6 (Р2): ThrustAccel > 0 ⇒ ускорение берётся напрямую (путь модулей
        // и ассиста); 0 ⇒ как раньше, LinearThrust/Mass (старые тесты побитово целы).
        float accelScale = (p.ThrustAccel > 0f ? p.ThrustAccel
                         : (p.Mass > 0f ? p.LinearThrust / p.Mass : 0f)) * boost;

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
        float angAccelScale = (p.AngularAccel > 0f ? p.AngularAccel
                              : (p.Mass > 0f ? p.AngularThrust / p.Mass : 0f)) * boost;
        // BUG-010: Q/E — крен вокруг локальной Z (продольной) оси корпуса. Знак:
        // «вперёд» у корпуса это −Z, поэтому положительный момент вокруг +Z по
        // правилу правой руки (X→Y) поднимает правый борт = крен ВЛЕВО.
        Vector3 localTorque = new Vector3(
            f.AxisPitchUp - f.AxisPitchDown,
            f.AxisYawRight - f.AxisYawLeft,
            f.AxisRollLeft - f.AxisRollRight);
        if (localTorque.LengthSquared() > 1f) localTorque = localTorque.Normalized();

        // --- 5. Скорости ---
        n.LinearVelocity = s.LinearVelocity + worldThrust * dt;
        n.AngularVelocity = s.AngularVelocity + basis * localTorque * angAccelScale * dt;

        // --- 5b. Этап 6 (Р1): торможение пилот-ассиста, «жидкий космос» ---
        // Кинематическое (постоянное ускорение), а НЕ экспонента: требование
        // заказчика «падает с таким же ускорением, как набирается». Гейт по
        // ОТСУТСТВИЮ линейного ввода обязателен: при AssistDragAccel == ThrustAccel
        // (а по условию они равны!) постоянно включённое торможение компенсировало
        // бы тягу, и корабль не сдвинулся бы вообще. Порядок «тяга → торможение →
        // демпфирование → потолок» даёт: зажатая клавиша = разгон до потолка,
        // отпущенная = чистое падение до нуля без проскока.
        if (p.PilotAssist && p.AssistDragAccel > 0f && (f.Mask & InputMask.LinearBits) == 0u)
            n.LinearVelocity = BrakeToZero(n.LinearVelocity, p.AssistDragAccel, dt);

        // --- 6. Демпфирование (модель, не движок) ---
        if (p.LinearDamping > 0f)
            n.LinearVelocity *= Mathf.Exp(-p.LinearDamping * dt);
        if (p.AngularDamping > 0f)
            n.AngularVelocity *= Mathf.Exp(-p.AngularDamping * dt);

        // --- 7. Потолок угловой скорости (характеристика корабля) ---
        // После демпфирования, чтобы момент с клавиш (Q/E, Z/X/C/V) и внешняя
        // доводка не могли выйти за предел ни при каком шаге. Клиппится МОДУЛЬ
        // вектора, а не каждая ось: направление вращения сохраняется.
        if (p.MaxAngularSpeed > 0f &&
            n.AngularVelocity.LengthSquared() > p.MaxAngularSpeed * p.MaxAngularSpeed)
            n.AngularVelocity = n.AngularVelocity.Normalized() * p.MaxAngularSpeed;

        // --- 7b. Этап 6 (Р3): потолок линейной скорости («макс. скорость стрейфа») ---
        // В САМОМ КОНЦЕ шага: ни тяга, ни импульсы/внешние записи скорости, ни
        // доводка не могут оставить состояние выше лимита (ровно та же логика,
        // что у гейта MaxAngularSpeed на входе/выходе). ОСЕВОЙ клиппинг: режем
        // только вышедшие за лимит компоненты, вектор не схлопываем в сферу —
        // иначе диагональное движение тормозило бы вдоль разрешённых осей.
        if (p.MaxLinearSpeed > 0f)
            n.LinearVelocity = ClampLinearSpeed(n.LinearVelocity, p.MaxLinearSpeed);

        return n;
    }

    /// <summary>
    /// Кинематическое торможение до нуля (Этап 6, Р1): |Δv| = dragAccel·dt,
    /// строго к нулю без проскока через него (при |v| ≤ dragAccel·dt отдаём
    /// ровно Vector3.Zero). Чистая функция, тот же стиль, что `ClampLookRadians`:
    /// детерминирована, без состояния.
    /// </summary>
    public static Vector3 BrakeToZero(Vector3 v, float dragAccel, float dt)
    {
        if (dragAccel <= 0f || dt <= 0f) return v;
        float len = v.Length();
        if (len <= dragAccel * dt) return Vector3.Zero;
        return v - v * (dragAccel * dt / len);
    }

    /// <summary>
    /// Осевой клиппинг скорости к лимиту (Этап 6, Р3): каждая мировая ось
    /// ограничивается ±maxSpeed независимо. Направление сохраняется, вектор НЕ
    /// схлопывается в сферу. maxSpeed ≤ 0 = без ограничения (structural-off).
    /// </summary>
    public static Vector3 ClampLinearSpeed(Vector3 v, float maxSpeed)
    {
        return new Vector3(
            Mathf.Clamp(v.X, -maxSpeed, maxSpeed),
            Mathf.Clamp(v.Y, -maxSpeed, maxSpeed),
            Mathf.Clamp(v.Z, -maxSpeed, maxSpeed));
    }

    /// <summary>
    /// Пропорциональное ограничение мышиной дельты угла (радианы, из
    /// `InputFrame.LookYaw`/`LookPitch`): не более `p.MaxAngularSpeed` радиан
    /// за шаг `dt`, т.е. мышь крутит нос не быстрее предельной угловой скорости
    /// корабля. Ограничивается ВЕКТОР (yaw, pitch) целиком — при резком диагональном
    /// движении обе компоненты режутся в одно число, порядок доворота сохраняется.
    ///
    /// Мышь не проходит через `AngularVelocity` (это прямой доворот носа), поэтому
    /// гейт обязателен и здесь, и внутри `Step`. `MaxAngularSpeed <= 0` = без
    /// ограничения: функция возвращает дельту побитово как пришла (тесты п.6b/п.9
    /// сверяют точное значение поворота от мыши).
    /// </summary>
    public static Vector2 ClampLookRadians(Vector2 look, float maxAngularSpeed, float dt)
    {
        if (maxAngularSpeed <= 0f || dt <= 0f)
            return look;
        float maxAngle = maxAngularSpeed * dt;
        float len = look.Length();
        if (len <= maxAngle || len <= 0f)
            return look;
        return look * (maxAngle / len);
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
