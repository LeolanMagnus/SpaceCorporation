using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    // --- Этап 4, шаг 4.7: серверный учёт контакта со станцией ----------------
    // Урон и отскок считает ТОЛЬКО авторитет в _IntegrateForces (клиент их не
    // трогает — шаг 4.11). Обнаружение — PhysicsDirectSpaceState3D.IntersectShape
    // по маске Station (проба tests/probes/ProbeStationPen.cs: там же в C#-биндинге
    // НЕТ ComputePenetration, а в результате IntersectShape есть только ключи
    // rid/collider_id/collider/shape — глубины/нормали нет, поэтому нормаль
    // берём геометрически: из центра станции в корабль).
    /// <summary>Пауза (сек) между повторным учётом урона тому же узлу-пересечению,
    /// чтобы корабль, застрявший в цилиндре станции, не получал урон каждый кадр
    /// (таблица «ломается», строка «застрять → урон капает»).</summary>
    [Export] public float CollisionCooldownSec = 0.5f;
    /// <summary>Сетевой адрес станции, по которому корабль таранит в этом же
    /// физкадре — чтобы тот же контакт в одном кадре не считался дважды.</summary>
    private int _lastContactStationNetId;
    private ulong _lastContactPhysicsFrame;
    /// <summary>Серверные часы последнего учёта урона конкретной станции
    /// (кулдаун CollisionCooldownSec).</summary>
    private double _lastCollisionTimeSec;
    private bool _hasLastCollisionTime;
    /// <summary>Форма корпуса корабля для shape-запроса (кэш CollisionShape3D).</summary>
    private CollisionShape3D? _hullShapeNode;
    /// <summary>Флаг «отскок применён» — метод выставил новое n.LinearVelocity,
    /// инкрематор обязан записать его в `sd` перед шагом 3.</summary>
    private bool _collisionApplied;
    /// <summary>Новая линейная скорость после упругого отскока (валидна, если
    /// <see cref="_collisionApplied"/>).</summary>
    private Vector3 _collisionOutVelocity;

    /// <summary>
    /// Собственный интегратор. Вызывается только там, где включён
    /// `CustomIntegrator`, то есть только авторитету (остальные пиры заморожены,
    /// а у замороженного тела движок `_integrate_forces` не зовёт вовсе).
    ///
    /// Порядок шага установлен пробами `tests/probes/probe_ci_order.tscn` и
    /// `probe_ci_finality.tscn`:
    ///   1) движок сам сдвигает тело скоростью ПРЕДЫДУЩЕГО шага (explicit Euler:
    ///      `pos_at_entry = p0 + v0*dt`), угловой скоростью НЕ вращает
    ///      (`probe_ci_angular.tscn`);
    ///   2) затем зовёт нас, и то, что мы запишем в `sd`, финально для шага
    ///      (`pos_after_step` = ровно наша запись).
    /// Поэтому модель считает шаг ЦЕЛИКОМ сама, а пред-интегрирование движка
    /// отменяется (`t.Origin -= v0 * dt`) — иначе шаг считается дважды и
    /// траектория тела разошлась бы с траекторией чистой модели, по которой
    /// клиент предсказывает и переигрывает (шаг 2.4).
    /// </summary>
    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        if (!HasAuthority)
            return; // страховка: CustomIntegrator включён только авторитету
        if (_testStep)
            return; // шаг ведёт TestStepForTest, физкадры пропускаем (иначе двойной шаг)

        float dt = (float)sd.GetStep();
        if (dt <= 0f) dt = FlightModel.FixedDt;

        // --- 1. Состояние НАЧАЛА шага: отменяем пред-интегрирование движка ---
        Vector3 v0 = sd.LinearVelocity;
        Transform3D start = sd.Transform;
        start.Origin -= v0 * dt;

        FlightState s = new FlightState
        {
            Position = start.Origin,
            Rotation = start.Basis.GetRotationQuaternion(),
            LinearVelocity = v0,
            AngularVelocity = sd.AngularVelocity,
        };

        // --- 2. Ввод пилота за этот шаг + шаг модели + тик ---
        // Сервер берёт кадр из очереди пилота (или читает клавиши, если пилот он же);
        // у offline-игры сервер = локальный пир, и поведение стадии 1 сохраняется.
        FlightState n = AdvanceFlight(s, FrameForStep(), dt);

        // --- 2.5. Этап 4, шаг 4.7: серверный учёт контакта со станцией ---
        // Запрашиваем пересечение КОРПУСА в позе КОНЦА шага (n.Position): если
        // корпус вошёл в цилиндр станции — урон по |vn| и отскок. Только на
        // авторитете (метод уже гейтнут HasAuthority выше), клиент сюда не
        // заходит (шаг 4.11). Результат отскока — в n.LinearVelocity ДО записи.
        HandleStationContacts(n.Position, n.LinearVelocity);
        if (_collisionApplied) { n.LinearVelocity = _collisionOutVelocity; _collisionApplied = false; }

        // --- 3. Запись в тело (финальна для шага) ---
        sd.Transform = new Transform3D(new Basis(n.Rotation), n.Position);
        sd.LinearVelocity = n.LinearVelocity;
        sd.AngularVelocity = n.AngularVelocity;
    }

    /// <summary>Общая часть шага: мышь → ориентация, кадр ввода → тяга, тик.
    /// И `_IntegrateForces`, и тестовый шаг идут через неё, иначе тест проверял
    /// бы не тот код, который работает в игре (§3.8).</summary>
    private FlightState AdvanceFlight(FlightState s, InputFrame frame, float dt)
    {
        _localInput = frame;
        // Шаг 5.8: на dock-фазе тяга пилота заменена серверной пружиной —
        // оба шага (боевой и тестовый) идут через этот общий код.
        FlightState n;
        if (Net != null && Docking.IsCapturePhase((DockState)Net.DockStateValue))
        {
            n = StepDockSpring(s, dt);
        }
        else if (_captureParentNetId != 0)
        {
            // frame-plan шаг 3: захват — интеграция В КАДРЕ родителя (§3):
            // тяга, инерция и лимит MaxLinearSpeed работают по локальной
            // скорости v_rel, а перенос родителем добавляется аддитивно.
            // КЛЮЧЕВАЯ ДЕТАЛЬ (ловушка, найтая живым тестом CaptureLiveTest):
            // v_rel снимается НЕ с ТЕКУЩЕЙ, а с ПРЕДЫДУЩЕЙ пробы кадра
            // (_captureFrameV — кадр, применённый на прошлом шаге этого тела),
            // а заново внедряется ТЕКУЩЕЙ. Разница (vFrame − vFramePrev) —
            // фиктивное ускорение переноса: разгоняясь или тормозя, родитель
            // МЕНЯЕТ мировую скорость пассажира на Δv_frame, поэтому свободный
            // объект без тяги едет ВМЕСТЕ с родителем и держит дистанцию (§1:
            // «B, двигаясь, переносит A с собой»). С одинаковой пробкой туда-
            // сюда (v_frame в обоих местах одна и та же) правило тождественно
            // для тела без сил: пассажир отставал бы от тормозящего флагмана
            // на Δv·t и вылетал из пузыря — «перенос» означал бы только
            // переименование скорости.
            // Детерминизм: пробы кадра — чистые функции состояний A и B на
            // этом шаге; переходы захвата — только от авторитета (шаг 6).
            // ПОЗИЦИЯ сносится ПРЕДЫДУЩЕЙ пробой, а не текущей: FlightModel
            // шагает позицией СКОРОСТИ НАЧАЛА кадра (explicit Euler, Step:
            // pos += v·dt до тяги), а в причинном порядке (родитель шагает
            // раньше пассажира) vFramePrev — это ровно финал родителя с
            // прошлого кадра = его ВХОДНАЯ скорость в этом. Значит снос
            // vFramePrev·dt побитово равен перемещению родителя за кадр, а
            // внедрение vFrame догоняет скорость пассажира по финалу родителя:
            // и дистанция, и v_rel точны (живой тест: Δd=0, |v_rel|=0).
            Vector3 vFramePrev = _captureFrameV;
            Vector3 vFrame = CaptureFrameVelocityAt(s.Position);
            if (vFrame == Vector3.Zero && vFramePrev != Vector3.Zero)
                vFrame = vFramePrev; // родитель пропал из реестра: без пинка,
                                     // правило снимет захват на ближайшей проверке
            _captureFrameV = vFrame;
            FlightState fin = s;
            fin.LinearVelocity = s.LinearVelocity - vFramePrev;
            n = StepPrediction(fin, frame, dt);
            n.Position += vFramePrev * dt;
            n.LinearVelocity += vFrame;
        }
        else
        {
            n = StepPrediction(s, frame, dt);
        }
        _tick++;
        return n;
    }

    /// <summary>Единственная точка приложения импульса (§3.9 п.10): публичных
    /// `ApplyImpulse`/`ApplyTorque` у `Ship` быть не должно — не-авторитет, дёрнув
    /// их, рассинхронит картинку с истиной, а `Freeze` это не лечит (заморозка
    /// игнорирует силы только у себя, а не у того, кто их приложил). В offline
    /// авторитет есть всегда — гейтинг прозрачен.</summary>
    public void ApplyImpulseAuthorityOnly(Vector3 impulse)
    {
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net?.ShipNetId ?? 0}: импульс запросил не авторитет " +
                        $"(авторитет = {GetMultiplayerAuthority()}) — отказ");
            return;
        }
        ApplyImpulse(impulse);
    }

    /// <summary>То же для крутящего импульса (§3.9 п.10).</summary>
    public void ApplyTorqueImpulseAuthorityOnly(Vector3 torqueImpulse)
    {
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net?.ShipNetId ?? 0}: крутящий импульс запросил не " +
                        $"авторитет (авторитет = {GetMultiplayerAuthority()}) — отказ");
            return;
        }
        ApplyTorqueImpulse(torqueImpulse);
    }

}
