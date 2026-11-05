using Godot;
using System;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест стадии 2 (сетевое пилотирование). Запуск:
///   ./run-tests.sh res://tests/Stage2Test.tscn
/// Проверяет шаги 2.1–2.6 плана stage2-plan.md: чистый интегратор FlightModel,
/// связку «тело с CustomIntegrator ↔ FlightModel», формат кадра ввода, 6DoF-ввод,
/// серверную авторитетную симуляцию по InputFrame, предсказание с reconciliation
/// и интерполяцию удалённого.
///
/// Тест НЕ поднимает реальный MultiplayerPeer: сетевые сценарии (п.7–13) делает
/// tests/NetTest.cs, где уже есть живое соединение. Здесь же проверяется то, что
/// не требует сети: математика, формат, гейты и связка «тело ↔ модель».
/// </summary>
public partial class Stage2Test : Node3D
{
    private int _pass = 0;
    private int _fail = 0;
    private readonly List<string> _failures = new();

    private static readonly Color Ok = new(0.45f, 0.85f, 0.45f);
    private static readonly Color Bad = new(0.95f, 0.4f, 0.4f);

    private Label _label;
    private Camera3D _camera;
    private Node3D _root;
    private readonly List<string> _lines = new();

    /// <summary>Параметры, общие для всех прогонов этого файла. Значения — из
    /// Ship.tscn/Ship.cs (масса тела по умолчанию Godot = 1.0), чтобы проба
    /// тандема (п.2) считала ровно те же числа, что и тело.</summary>
    private static FlightParams Params() => new()
    {
        Mass = 1f,
        LinearThrust = 60f,
        AngularThrust = 24f,
        LinearDamping = 0f,     // как LinearDamp на Ship (project.godot/Ship.cs:86)
        AngularDamping = 1.5f,  // как AngularDamp на Ship (Ship.cs:87)
        BoostMultiplier = 2f,
    };

    public override void _Ready()
    {
        GD.Print("=== Stage2Test: начало ===");

        _label = new Label();
        _label.Position = new Vector2I(16, 16);
        AddChild(_label);

        _camera = new Camera3D();
        _camera.Position = new Vector3(0f, 6f, 14f);
        AddChild(_camera);
        // LookAt — только после AddChild: до добавления в дерево узел вне дерева
        // и движок отвечает "Node not inside tree. Use look_at_from_position()".
        _camera.LookAt(Vector3.Zero);
        _camera.MakeCurrent();

        _root = new Node3D();
        _root.Name = "TestRoot";
        AddChild(_root);

        // П.1 и п.3 считаются сразу, без физических кадров. П.2 требует реального
        // физического шага (живой RigidBody3D с CustomIntegrator) — он доделывается
        // в _PhysicsProcess, а итог печатается там же и только потом Finish().
        // Пилотирование 2.0 доворачивает нос из ПОЗИЦИИ курсора: слепая зона
        // 100px, полный радиус = ¼ ВЫСОТЫ окна. В headless окно по умолчанию
        // 64×64 → полный радиус 16px < слепой зоны 100px, и довернуть нос было
        // бы физически невозможно на любом пикселе. Поэтому поднимаем окно до
        // 1280×720 (полный радиус = 180px, слепая зона в него влезает).
        GetWindow().Size = new Vector2I(1280, 720);

        RunFlightModelTests();
        RunAngularSpeedLimitTests();
        RunInputFrameTests();
        RunPilotAimTests();
        StartIntegratorProbe();
    }

    // ==================================================================
    // П.1 — FlightModel: детерминизм и базовые свойства
    // ==================================================================
    private void RunFlightModelTests()
    {
        GD.Print("[Stage2Test] п.1: FlightModel — детерминизм");
        FlightParams p = Params();

        // Один и тот же набор кадров, прогнанный дважды, обязан сойтись побитово:
        // иначе сервер (истина) и клиент (предсказание) разойдутся, и реконсиляция
        // будет дёргать корабль на каждом пакете.
        FlightState a = FlightState.Park();
        FlightState b = FlightState.Park();
        InputFrame f = new() { Tick = 0 };
        for (int i = 0; i < 600; i++)
        {
            f.Tick = (uint)i;
            uint mask = (i % 7 == 0) ? 0u
                : (InputMask.ThrustF | InputMask.ThrustR | InputMask.TorquePitchUp | InputMask.TorqueYawLeft);
            f = InputFrame.FromMask(f.Tick, mask, yaw: (i % 3 == 0) ? 0.01f : 0f, pitch: -0.005f);
            a = FlightModel.Step(a, p, f, FlightModel.FixedDt);
            b = FlightModel.Step(b, p, f, FlightModel.FixedDt);
        }

        Check(a.Position == b.Position, "п.1: позиция совпадает побитово после 600 кадров");
        Check(a.Rotation == b.Rotation, "п.1: кватернион совпадает побитово после 600 кадров");
        Check(a.LinearVelocity == b.LinearVelocity, "п.1: линейная скорость совпадает побитово");
        Check(a.AngularVelocity == b.AngularVelocity, "п.1: угловая скорость совпадает побитово");

        // Дискретность шага — часть детерминизма. Explicit Euler: 10 шагов по 1/60
        // и 1 шаг по 10/60 дают РАЗНЫЕ позиции (при тяге большой шаг не успевает
        // набрать скорость до сдвига). Отсюда правило: шаг всегда FixedDt, а не
        // «сколько намеряло время» — иначе предсказание пилота и сервер разойдутся
        // уже на математике, а не на сети.
        InputFrame big = InputFrame.FromMask(1, InputMask.ThrustF | InputMask.ThrustR | InputMask.TorquePitchUp,
            yaw: 0.01f, pitch: -0.005f);
        FlightState c = FlightState.Park();
        for (int i = 0; i < 10; i++) c = FlightModel.Step(c, p, big, FlightModel.FixedDt);
        FlightState c2 = FlightState.Park();
        for (int i = 0; i < 10; i++) c2 = FlightModel.Step(c2, p, big, FlightModel.FixedDt);
        Check(c.Position == c2.Position && c.Rotation == c2.Rotation,
            "п.1: 10 шагов по FixedDt воспроизводимы побитово");
        FlightState d = FlightModel.Step(FlightState.Park(), p, big, 10f * FlightModel.FixedDt);
        float drift = c.Position.DistanceTo(d.Position);
        Check(drift > 0.01f,
            $"п.1: шаг ≠ FixedDt даёт другое состояние (расхождение {drift:F5} м) — шаг обязан быть фиксированным");
        Check(c.Position.Length() > 0f,
            $"п.1: тяга реально сдвигает корабль (|pos| = {c.Position.Length():F4} м за 10 кадров)");

        // П.2 (по плану 2.1): поворот не меняет модуль скорости при пустой маске.
        FlightState spin = new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = new Vector3(0f, 0f, 10f),
            AngularVelocity = new Vector3(0f, 0f, 2f),
        };
        float lenBefore = spin.LinearVelocity.Length();
        for (int i = 0; i < 120; i++)
            spin = FlightModel.Step(spin, p, new InputFrame { Tick = (uint)i }, FlightModel.FixedDt);
        Check(Mathf.Abs(spin.LinearVelocity.Length() - lenBefore) < 1e-3f,
            $"п.1: пустая маска — модуль скорости не меняется ({lenBefore:F4} → {spin.LinearVelocity.Length():F4})");
        Check(spin.Rotation != Quaternion.Identity,
            "п.1: пустая маска, но есть угловая скорость — ориентация меняется");
        Check(spin.AngularVelocity.Length() < 2f,
            $"п.1: angular_damp гасит вращение (|w| 2 → {spin.AngularVelocity.Length():F4})");

        // П.3 (по плану 2.1): |w| убывает монотонно.
        FlightState mono = new()
        {
            Position = Vector3.Zero, Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero, AngularVelocity = new Vector3(0f, 3f, 0f),
        };
        // 240 кадров = 4 с: exp(-1.5·4) = 0.0025 от начальных 3 рад/с. Порог
        // 0.02 оставлен с запасом — монотонность важнее точного числа.
        bool monotonic = true;
        float prev = mono.AngularVelocity.Length();
        for (int i = 0; i < 240; i++)
        {
            mono = FlightModel.Step(mono, p, new InputFrame { Tick = (uint)i }, FlightModel.FixedDt);
            float now = mono.AngularVelocity.Length();
            if (now > prev + 1e-6f) monotonic = false;
            prev = now;
        }
        Check(monotonic && mono.AngularVelocity.Length() < 0.02f,
            $"п.1: |w| убывает монотонно до 0 (итог {mono.AngularVelocity.Length():E2})");

        // Нулевой ввод из покоя = покой; нулевой ввод в движении = инерция.
        FlightState coast = new()
        {
            Position = Vector3.Zero, Rotation = Quaternion.Identity,
            LinearVelocity = new Vector3(10f, 0f, 0f), AngularVelocity = Vector3.Zero,
        };
        FlightState after = FlightModel.Step(coast, p, new InputFrame { Tick = 1 }, FlightModel.FixedDt);
        Check(after.Position.X > 0.16f && after.Position.X < 0.17f,
            $"п.1: инерция — смещение за кадр = v*dt (x={after.Position.X:F4})");
        Check(after.Rotation == Quaternion.Identity && after.AngularVelocity == Vector3.Zero,
            "п.1: нулевой момент — ориентация не меняется");

        // Тяга: 60 Н при массе 1 кг, 60 кадров без демпфирования → |v| ≈ 60 м/с.
        FlightState thrust = FlightState.Park();
        InputFrame fwd = InputFrame.FromMask(1, InputMask.ThrustF);
        for (int i = 0; i < 60; i++) thrust = FlightModel.Step(thrust, p, fwd, FlightModel.FixedDt);
        Check(Mathf.Abs(thrust.LinearVelocity.Length() - 60f) < 0.5f,
            $"п.1: тяга даёт ожидаемое ускорение (|v|={thrust.LinearVelocity.Length():F2} ≈ 60 м/с)");
        Check(thrust.LinearVelocity.Z < 0f,
            "п.1: «вперёд» = локальная -Z (Godot-контент), а не +Z");

        // Форсаж усиливает тягу ровно в BoostMultiplier раз.
        FlightState boost = FlightState.Park();
        InputFrame boostFwd = InputFrame.FromMask(1, InputMask.ThrustF | InputMask.Boost);
        for (int i = 0; i < 60; i++) boost = FlightModel.Step(boost, p, boostFwd, FlightModel.FixedDt);
        Check(Mathf.Abs(boost.LinearVelocity.Length() - 120f) < 1f,
            $"п.1: Boost = ×{p.BoostMultiplier} к тяге (|v|={boost.LinearVelocity.Length():F2})");

        // Встречный ввод: скорость не растёт бесконечно (противодействие + damp).
        FlightState term = FlightState.Park();
        InputFrame spin2 = InputFrame.FromMask(1, InputMask.ThrustF | InputMask.Boost | InputMask.TorquePitchDown);
        for (int i = 0; i < 6000; i++) term = FlightModel.Step(term, p, spin2, FlightModel.FixedDt);
        Check(Mathf.Abs(term.LinearVelocity.Z) < 1e4f && term.AngularVelocity.Length() < 100f,
            $"п.1: значения конечны на длинном прогоне (|vz|={Mathf.Abs(term.LinearVelocity.Z):F1}, |w|={term.AngularVelocity.Length():F2})");
        Check(!term.Rotation.IsNormalized() == false, "п.1: кватернион остаётся нормализованным");
    }

    // ==================================================================
    // П.3 — формат кадра ввода (InputFrame)
    // ==================================================================
    private void RunInputFrameTests()
    {
        GD.Print("[Stage2Test] п.3: InputFrame — формат");

        // Размер: 4 (tick) + 4 (mask) + 4 + 4 (дельта мыши) = 16 байт, не больше
        // лимита 32 байта на unreliable-пакет канала ввода.
        Check(InputFrame.Size == 16, $"п.3: размер кадра = {InputFrame.Size} байт (≤ 32)");

        InputFrame f = InputFrame.FromMask(0xDEADBEEF,
            InputMask.ThrustF | InputMask.ThrustU | InputMask.TorqueYawRight | InputMask.Boost,
            yaw: 0.025f, pitch: -0.0125f);
        byte[] b = f.ToBytes();
        Check(b.Length == InputFrame.Size, $"п.3: ToBytes даёт ровно {InputFrame.Size} байт");

        InputFrame g = InputFrame.FromBytes(b);
        Check(g.Tick == f.Tick && g.Mask == f.Mask, "п.3: tick и mask проходят round-trip");
        Check(g.LookYaw == f.LookYaw && g.LookPitch == f.LookPitch,
            "п.3: дельта мыши проходит round-trip побитово");

        // Оси обязаны восстановиться из маски: сервер получает только маску и
        // иначе интегрировал бы пустую тягу.
        Check(g.AxisF == 1f && g.AxisU == 1f && g.AxisYawRight == 1f,
            "п.3: оси восстановлены из маски (F/U/YawRight = 1)");
        Check(g.AxisB == 0f && g.AxisL == 0f && g.AxisPitchUp == 0f,
            "п.3: неотмеченные биты дают ось 0");

        // Побитовое равенство повторной сериализации — иначе «тот же кадр» на
        // сервере мог бы считаться иначе, чем на клиенте.
        byte[] b2 = g.ToBytes();
        bool same = b.Length == b2.Length;
        for (int i = 0; same && i < b.Length; i++) same = b[i] == b2[i];
        Check(same, "п.3: повторная сериализация даёт те же байты");

        // Пустой кадр: маска 0, все оси 0 — сервер обязан получить «нет ввода».
        InputFrame empty = InputFrame.FromBytes(InputFrame.FromMask(7, 0u).ToBytes());
        Check(empty.Mask == 0u && empty.AxisF == 0f && empty.AxisYawLeft == 0f,
            "п.3: пустая маска → все оси 0 (отпущенная клавиша не залипает)");

        // Merge: объединение битов и ограничение осей единицей.
        InputFrame m = InputFrame.Merge(
            InputFrame.FromMask(1, InputMask.ThrustF | InputMask.ThrustR, 0.01f, 0f),
            InputFrame.FromMask(2, InputMask.ThrustF | InputMask.ThrustU, 0f, 0.02f));
        Check(m.Tick == 2u && m.Mask == (InputMask.ThrustF | InputMask.ThrustR | InputMask.ThrustU),
            "п.3: Merge — объединение битов и старший tick");
        Check(m.AxisF == 1f && m.AxisR == 1f && m.AxisU == 1f,
            "п.3: Merge — оси ограничены сверху единицей (1+1 → 1)");
        Check(Mathf.Abs(m.LookYaw - 0.01f) < 1e-6f && Mathf.Abs(m.LookPitch - 0.02f) < 1e-6f,
            "п.3: Merge — дельта мыши складывается");

        // Линейные/угловые биты разделяются: «только вращение» не даёт тяги.
        InputFrame rot = InputFrame.FromMask(1, InputMask.AngularBits);
        Check((rot.Mask & InputMask.LinearBits) == 0u,
            "п.3: AngularBits не пересекаются с LinearBits");
        Check(rot.AxisF == 0f && rot.AxisPitchUp == 1f,
            "п.3: чистый момент → нулевая линейная тяга");
    }

    // ==================================================================
    // П.3b — PilotAim: чистая математика доворота носа на курсор
    // ==================================================================

    /// <summary>Юнит-тест статики <see cref="PilotAim"/> без дерева сцены и
    /// без мыши: слепая зона, линейная рампа, потолок скорости и вырожденная
    /// геометрия микроскопического окна. Это тот же код, что крутит нос в
    /// живом пилот-режиме — крыть его отдельно значит не зависеть от физки.</summary>
    private void RunPilotAimTests()
    {
        GD.Print("[Stage2Test] п.3b: PilotAim — математика доворота");

        Vector2 c = new Vector2(500f, 300f);   // центр окна (нос смотрит сюда)
        float h = 720f;                          // высота окна
        float R = PilotAim.FullSpeedRadiusPx(h); // полный радиус = ¼·720 = 180
        float dz = PilotAim.DeadzoneRadiusPx;    // слепая зона = 100px

        Check(Mathf.Abs(R - 180f) < 1e-4f && Mathf.Abs(dz - 100f) < 1e-4f,
            $"п.3b: радиусы — полный {R:F0}px, слепая зона {dz:F0}px");
        Check(R > dz, "п.3b: полный радиус больше слепой зоны (рампа существует)");

        // Точно в центре — направление неопределено → не крутим.
        Check(PilotAim.TurnFactor(c, c, R) == Vector2.Zero,
            "п.3b: курсор в центре (r=0) → ноль");

        // Внутри слепой зоны — не крутим (микродрожание руки).
        Check(PilotAim.TurnFactor(c + new Vector2(60f, 0f), c, R) == Vector2.Zero,
            "п.3b: r=60 < слепой зоны → ноль");

        // Ровно на границе слепой зоны (r = dz) — включительно → ноль.
        Check(PilotAim.TurnFactor(c + new Vector2(dz, 0f), c, R) == Vector2.Zero,
            "п.3b: r=dz на границе → ноль (зона включительно)");

        // Ровно на окружности полной скорости → фактор = 1 по направлению.
        Vector2 atR = PilotAim.TurnFactor(c + new Vector2(R, 0f), c, R);
        Check(Mathf.Abs(atR.X - 1f) < 1e-4f && Mathf.Abs(atR.Y) < 1e-4f,
            "п.3b: r=R → фактор 1 (вправо)");

        // За окружностью — потолок: фактор всё ещё 1 (не растёт).
        Vector2 beyond = PilotAim.TurnFactor(c + new Vector2(4f * R, 0f), c, R);
        Check(Mathf.Abs(beyond.X - 1f) < 1e-4f && Mathf.Abs(beyond.Y) < 1e-4f,
            "п.3b: r≫R → фактор ограничен 1");

        // Середина рампы (r = (dz+R)/2 = 140) → фактор 0.5 ровно.
        Vector2 mid = PilotAim.TurnFactor(c + new Vector2(140f, 0f), c, R);
        Check(Mathf.Abs(mid.X - 0.5f) < 1e-4f && Mathf.Abs(mid.Y) < 1e-4f,
            $"п.3b: середина рампы r=140 → фактор 0.5 (получено {mid.X:F3})");

        // Вниз по экрану → фактор по +Y (экранная ось вниз = pitch+ в look-канале).
        Vector2 down = PilotAim.TurnFactor(c + new Vector2(0f, R), c, R);
        Check(Mathf.Abs(down.X) < 1e-4f && Mathf.Abs(down.Y - 1f) < 1e-4f,
            "п.3b: курсор ниже центра → фактор по +Y");

        // Вырожденное микроокно: полный радиус меньше слепой зоны (headless
        // 64×64: R=16 < dz=100) — внутри зоны ноль, за ней сразу полная скорость.
        float tinyR = PilotAim.FullSpeedRadiusPx(64f); // = 16
        Check(PilotAim.TurnFactor(c + new Vector2(50f, 0f), c, tinyR) == Vector2.Zero,
            "п.3b: микроокно, r=50 ≤ dz → ноль");
        Vector2 tinyFull = PilotAim.TurnFactor(c + new Vector2(150f, 0f), c, tinyR);
        Check(Mathf.Abs(tinyFull.X - 1f) < 1e-4f && Mathf.Abs(tinyFull.Y) < 1e-4f,
            "п.3b: микроокно, r>dz → сразу полная скорость (рампы нет)");

        // LookRadiansPerStep: на окружности полной скорости команда = потолок
        // (MaxAngularSpeed=6 рад/с × dt=1/60 = 0.1 рад за шаг), направление вправо.
        Vector2 step = PilotAim.LookRadiansPerStep(c + new Vector2(R, 0f), c,
            R, 6f, 1f / 60f);
        Check(Mathf.Abs(step.X - 0.1f) < 1e-4f && Mathf.Abs(step.Y) < 1e-4f,
            $"п.3b: полная скорость за шаг = 0.1 рад (получено {step.X:F4})");

        // Внутри слепой зоны команда — ровно ноль (не «почти»).
        Vector2 dzStep = PilotAim.LookRadiansPerStep(c + new Vector2(50f, 0f), c,
            R, 6f, 1f / 60f);
        Check(dzStep == Vector2.Zero, "п.3b: внутри слепой зоны команда за шаг = 0");
    }

    // ==================================================================
    // П.2 — тандем «RigidBody3D с CustomIntegrator ↔ FlightModel»
    // ==================================================================
    // Проверяет то, чего чистая математика не проверяет: что запись состояния,
    // посчитанного FlightModel, в PhysicsDirectBodyState3D финальна для шага, и
    // что после N физических кадров тело стоит ровно там, где модель. Если бы
    // движок интегрировал тело сам (CustomIntegrator = false) либо если бы наша
    // запись терялась — расхождение накопилось бы за 90 кадров.

    private ProbeBody _body;
    private FlightState _model;
    private readonly FlightParams _p2 = Params();
    private InputFrame _p2Frame;
    private int _p2Steps = 0;
    private int _p2PhysFrames = 0;
    private bool _p2Done = false;
    private const int P2TotalSteps = 90;

    private void StartIntegratorProbe()
    {
        GD.Print("[Stage2Test] п.2: тандем CustomIntegrator + FlightModel");

        // Как у Ship на авторитете: CustomIntegrator = true, Freeze = false
        // (на авторитете корабль не мёрзнет — см. ApplyAuthority в шаге 2.3),
        // гравитация и демпфирование движка выключены: их считает модель.
        // Проба probe_ci_finality подтвердила: запись transform.origin и
        // linear_velocity внутри _integrate_forces финальна для шага.
        _body = new ProbeBody { Name = "P2Body" };
        _body.CustomIntegrator = true;
        _body.Freeze = false;
        _body.GravityScale = 0f;
        _body.LinearDamp = 0f;
        _body.AngularDamp = 0f;
        _body.Mass = _p2.Mass;
        _body.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1f, 1f, 1f) }
        });
        _root.AddChild(_body);
        _body.GlobalPosition = Vector3.Zero;

        // Кадр: тяга вперёд + момент рыскания; демпфирования в этом прогоне нет,
        // чтобы расхождение с телом нельзя было объяснить затуханием.
        _p2Frame = InputFrame.FromMask(1, InputMask.ThrustF | InputMask.TorqueYawRight);
        _model = FlightState.Park();
        _body.StepHandler = StepModelIntoBody;
    }

    /// <summary>Один шаг модели + полная запись состояния в тело (позиция,
    /// ориентация, обе скорости) — ровно то, что будет делать Ship на авторитете.</summary>
    private void StepModelIntoBody(PhysicsDirectBodyState3D sd)
    {
        _model = FlightModel.Step(_model, _p2, _p2Frame, (float)sd.GetStep());
        sd.Transform = new Transform3D(new Basis(_model.Rotation), _model.Position);
        sd.LinearVelocity = _model.LinearVelocity;
        sd.AngularVelocity = _model.AngularVelocity;
        _p2Steps++;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_finished) return;

        if (!_p2Done)
        {
            _p2PhysFrames++;

            if (_p2Steps >= P2TotalSteps)
            {
                FinishIntegratorProbe();
                StartPilotTest();   // п.4-6 идут после п.2, один прогон — один итог
                return;
            }
            // Интегратор обязан вызываться каждый физкадр. Если он не вызывается
            // вовсе (тело вне дерева/заморозка его «съедает») — это и есть провал
            // конструкции, и ждать его бессмысленно.
            if (_p2Steps == 0 && _p2PhysFrames > P2TotalSteps + 30)
            {
                Check(false, $"п.2: _IntegrateForces не вызван ни разу за {_p2PhysFrames} физкадров");
                Finish();
            }
            return;
        }

        if (_pilotStep >= 0)
            PilotStepMachine();
    }

    private void FinishIntegratorProbe()
    {
        _p2Done = true;

        Vector3 bodyPos = _body.GlobalPosition;
        Vector3 bodyVel = _body.LinearVelocity;
        Basis bodyBasis = _body.GlobalTransform.Basis;
        Basis modelBasis = new Basis(_model.Rotation);

        float posErr = bodyPos.DistanceTo(_model.Position);
        float velErr = bodyVel.DistanceTo(_model.LinearVelocity);
        // Угол рассогласования ориентаций через след относительного базиса A·Bᵗ:
        // Basis.AngleTo в C#-привязке 4.7 отсутствует (в GDScript есть), поэтому
        // считаем вручную: trace = 1 + 2cos(angle).
        Basis rel = bodyBasis * modelBasis.Transposed();
        float trace = rel.X.X + rel.Y.Y + rel.Z.Z;
        float angle = Mathf.Acos(Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f));

        Check(_body.LastStep > 0.016666 && _body.LastStep < 0.016667,
            $"п.2: dt внутри интегратора = 1/60 ({_body.LastStep:F8})");
        Check(posErr < 1e-4f,
            $"п.2: позиция тела = позиция модели (|err| = {posErr:E2} м)");
        Check(velErr < 1e-4f,
            $"п.2: линейная скорость тела = скорость модели (|err| = {velErr:E2})");
        Check(angle < 1e-3f,
            $"п.2: ориентация тела = ориентация модели (угол = {angle:E2} рад)");
        Check(_model.Position.Length() > 0.5f,
            $"п.2: тело реально сдвинуто моделью (|pos| = {_model.Position.Length():F3} м)");
        Check(_model.AngularVelocity.Length() > 0.5f,
            $"п.2: модель раскрутила тело (|w| = {_model.AngularVelocity.Length():F3} рад/с)");
        // Ориентация не должна «накапливать» масштаб: базис тела обязан остаться
        // ортонормированным, иначе запись кватерниона ломает геометрию.
        Check(Mathf.Abs(bodyBasis.X.Length() - 1f) < 1e-3f && Mathf.Abs(bodyBasis.Z.Length() - 1f) < 1e-3f,
            "п.2: базис тела остался ортонормированным");

        GD.Print($"[Stage2Test] п.2: calls={_body.IntegrateCalls} steps={_p2Steps} " +
                 $"bodyPos={Fmt(bodyPos)} modelPos={Fmt(_model.Position)}");
        // ВАЖНО: здесь НЕ Finish() — за п.2 идут п.4-6 на реальном Ship.tscn,
        // один прогон должен выдать один итог по всем проверкам.
    }

    private static string Fmt(Vector3 v) => $"({v.X:F3}, {v.Y:F3}, {v.Z:F3})";

    /// <summary>Угол рассогласования двух ориентаций в радианах (через след
    /// относительного базиса): `Basis.AngleTo` в C#-привязке 4.7 отсутствует.</summary>
    private static float AngleBetween(Quaternion a, Quaternion b)
    {
        Basis rel = new Basis(a) * new Basis(b).Transposed();
        float trace = rel.X.X + rel.Y.Y + rel.Z.Z;
        return Mathf.Acos(Mathf.Clamp((trace - 1f) * 0.5f, -1f, 1f));
    }

    // ==================================================================
    // П.4-п.6 + шаг 2.2.2: 6DoF-пилотирование в offline на РЕАЛЬНОМ Ship.tscn
    // ==================================================================
    //
    // Здесь проверяется то, что нельзя проверить чистой математикой: что реальный
    // `Ship` в реальном физическом шаге читает реальные действия из
    // `project.godot`, что тяга идёт вдоль ОСЕЙ КОРПУСА (а не по мировым осям),
    // что мышиная дельта доходит до корабля через узел-сборщик и что доворот
    // носа подчинён гейту прицела.
    //
    // Машинка шагов: каждый шаг — это «подождать N физических кадров и снять
    // замеры». Физика в Godot идёт своими кадрами, поэтому ждать приходится
    // кадрами, а не сном: `await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame)`
    // в тесте с десятками шагов делает код нечитаемым, а счётчик в
    // `_PhysicsProcess` — это тот же самый кадр, только видно, где именно тест
    // стоит, если он встал.
    //
    // Ввод подаётся через `Input.ParseInputEvent` (проба
    // `tests/probes/ProbeInputHeadless.tscn`: после пуша `InputEventKey` с
    // `PhysicalKeycode` действие `IsActionPressed` становится истинным со
    // СЛЕДУЮЩЕГО кадра, поэтому перед замером всегда ждём 3 кадра).

    private Ship _ship;
    private int _pilotStep = -1;
    private int _pilotFrames;
    private uint _pilotTickBase;

    private Vector3 _p4Start;
    private Vector3 _p4Forward;
    private uint _p4Tick;

    private Quaternion _p5Rot;
    private float _p5Speed;

    private float _p6Speed;
    private float _p6Spin;
    private uint _p6Tick;

    private Quaternion _mRot;
    private float _mYawBefore;
    // Пилотирование 2.0: позиции курсора-прицела (полная скорость / слепая зона).
    private Vector2 _mAimCursor;
    private Vector2 _mAimDz;

    private static InputEventKey KeyEvt(Key key, bool pressed) => new()
    {
        // PhysicalKeycode, а не Keycode: действия в project.godot привязаны к
        // физическому положению клавиши (см. шаг 2.2 плана), и именно так их
        // доводит реальная клавиатура.
        Keycode = key,
        PhysicalKeycode = key,
        Pressed = pressed,
    };

    private void StartPilotTest()
    {
        GD.Print("[Stage2Test] п.4-6: пилотирование на реальном Ship.tscn");

        PackedScene shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (shipScene == null)
        {
            Check(false, "п.4: не загрузился scenes/Ship.tscn");
            Finish();
            return;
        }

        _ship = shipScene.Instantiate<Ship>();
        _ship.Name = "PilotShip";
        _root.AddChild(_ship);

        // Офлайн-режим: авторитет = 1 = локальный пир, значит `IsLocalPilot`
        // истинно, и корабль сам читает клавиши и мышь.
        Check(_ship.IsLocalPilot,
            "п.4: офлайн-корабль признан пилотируемым (локальный пир = авторитет)");
        Check(_ship.CustomIntegrator && !_ship.Freeze,
            "п.4: у пилотируемого корабля включён CustomIntegrator и снята заморозка");

        _p4Start = _ship.GlobalPosition;
        _p4Forward = -_ship.GlobalTransform.Basis.Z; // «вперёд» корпуса = локальная -Z
        _p4Tick = _ship.Tick;

        Input.ParseInputEvent(KeyEvt(Key.W, true));
        _pilotFrames = 0;
        _pilotStep = 1;
    }

    private void PilotStepMachine()
    {
        _pilotFrames++;

        switch (_pilotStep)
        {
            // --- п.4: W на 30 кадров → смещение ВДОЛЬ ОСИ КОРПУСА ---
            case 1:
                if (_pilotFrames < 33) return; // 3 кадра на регистрацию действия + 30 кадров тяги

                Check(Input.IsActionPressed("thrust_f"),
                    "п.4: действие thrust_f читается из project.godot (W)");

                Vector3 disp = _ship.GlobalPosition - _p4Start;
                float along = disp.Dot(_p4Forward);
                Vector3 sideways = disp - _p4Forward * along;
                Check(along > 1f,
                    $"п.4: W сдвинул корабль вперёд на {along:F2} м (смещение {Fmt(_ship.GlobalPosition)})");
                // Мировые оси не участвуют: смещение обязано лежать вдоль оси
                // корпуса, иначе тяга считается в мировых осях и после поворота
                // корабль поедет «куда смотрит мир», а не «куда смотрит нос».
                Check(sideways.Length() < 0.05f,
                    $"п.4: смещение идёт вдоль оси корпуса, поперёк {sideways.Length():E2} м");
                Check(_ship.LinearVelocity.Length() > 1f,
                    $"п.4: |v| набрана {(_ship.LinearVelocity.Length()):F2} м/с");
                Check(_ship.Tick - _p4Tick >= 25u,
                    $"п.4: тик корабля продвинулся на {_ship.Tick - _p4Tick} за 33 физкадра");

                // П.5: момент крена с клавиатуры (Q). W отпускаем, чтобы
                // ускорение не мешало читать |w|.
                Input.ParseInputEvent(KeyEvt(Key.W, false));
                Input.ParseInputEvent(KeyEvt(Key.Q, true));
                _p5Rot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _p5Speed = _ship.LinearVelocity.Length();
                _pilotFrames = 0;
                _pilotStep = 2;
                return;

            // --- п.5: Q → |AngularVelocity| > 0 и корпус реально накренился ---
            case 2:
                if (_pilotFrames < 33) return;

                Check(Input.IsActionPressed("torque_roll_left"),
                    "п.5: действие torque_roll_left читается из project.godot (Q)");
                float spin = _ship.AngularVelocity.Length();
                Check(spin > 0.1f, $"п.5: |AngularVelocity| = {spin:F3} рад/с > 0");
                Quaternion rot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                float turned = AngleBetween(_p5Rot, rot);
                Check(turned > 0.05f, $"п.5: ориентация корпуса изменилась на {turned:F3} рад");
                // BUG-010: Q = крен ВЛЕВО = угловая скорость вдоль ЛОКАЛЬНОЙ Z
                // корпуса (продольной), а не мировой Y (рыскание). Переводим
                // мировую ω в систему корпуса транслированием базиса.
                Vector3 wLocal = _ship.GlobalTransform.Basis.Transposed() * _ship.AngularVelocity;
                Check(wLocal.Z > 0.1f * spin,
                    $"п.5: Q даёт крен ВЛЕВО: ω вдоль локальной Z (wLocal = {wLocal}, |w| = {spin:F3})");
                Check(Mathf.Abs(wLocal.Y) < 0.3f * spin,
                    $"п.5: Q не даёт рыскания: |wLocal.Y| = {Mathf.Abs(wLocal.Y):F3} мала");

                Input.ParseInputEvent(KeyEvt(Key.Q, false));
                _pilotFrames = 0;
                _pilotStep = 3;
                return;

            // --- п.6: клавиши отпущены → |w| гаснет демпфером, |v| — ассистом ---
            case 3:
                if (_pilotFrames < 3) return; // даём отпуску зарегистрироваться
                Check(!Input.IsActionPressed("thrust_f") && !Input.IsActionPressed("torque_roll_left"),
                    "п.6: отпущенные клавиши перестали быть нажатыми");
                // Этап 6: отсчёт торможения ведём от отпускания W (конец п.4):
                // тогда скорость была _p5Speed, а к этому шагу ассист (75 м/с²)
                // уже обязан был её добить — 33+3 кадра idle-фазы больше, чем
                // нужно на торможение ~37 м/с.
                Check(_ship.Loadout.PilotAssist,
                    "п.6: реальный корабль по умолчанию с поддержкой пилотирования");
                Check(_p5Speed > 1f && _ship.LinearVelocity.Length() < 0.2f,
                    $"п.6: ассист погасил |v| без линейного ввода " +
                    $"({_p5Speed:F2} при отпускании W → {_ship.LinearVelocity.Length():F2} сейчас)");
                _p6Speed = _ship.LinearVelocity.Length();
                _p6Spin = _ship.AngularVelocity.Length();
                _p6Tick = _ship.Tick;
                _pilotFrames = 0;
                _pilotStep = 4;
                return;

            // Проверка «погасло и не растёт»: через ~30 кадров idle скорость
            // обязана остаться нулевой — залипшая клавиша или двойной шаг модели
            // дали бы прирост.
            case 4:
                if (_pilotFrames < 30) return;

                float speed = _ship.LinearVelocity.Length();
                // Этап 6: «жидкий космос» — после отпуска клавиш пилот-ассист
                // гасит скорость тем же ускорением, что и разгон (т.е. разгон
                // за N кадров гасится за N кадров). За 27 кадров (~0.45 с при
                // 75 м/с²) скорость в несколько м/с обязана уйти в ноль.
                // Линейного демпфирования ТЛА всё равно нет (LinearDamp = 0):
                // гасит ассист, и только он.
                Check(speed < 0.2f,
                    $"п.6: скорость погашена ассистом (|v| = {speed:F2} м/с за 30 кадров)");
                float spin2 = _ship.AngularVelocity.Length();
                Check(spin2 < _p6Spin * 0.9f,
                    $"п.6: вращение гаснет само (|w|: {_p6Spin:F3} → {spin2:F3} рад/с)");
                Check(_ship.Tick > _p6Tick,
                    $"п.6: физический шаг не встал (тик {_p6Tick} → {_ship.Tick})");

                // Мышь (шаг 2.2.2) проверяем на СВЕЖЕМ корабле. Летавший для этого
                // не годится: после Q у него осталась угловая скорость (демпфер
                // гасит её ~полсекунды), и она добавила бы поворот за те же 2 кадра,
                // где мы ждём ровно 0.075 рад от мыши. Плюс пересадка — это ещё и
                // отдельная проверка: корабль, посаженный уже в идущей игре, обязан
                // получить пилоту камеру и сборщик мыши.
                _ship.QueueFree();
                _pilotFrames = 0;
                _pilotStep = 5;
                return;

            // --- шаг 2.2.2: мышь доходит до корабля и поворачивает нос ---
            case 5:
                if (_pilotFrames < 2) return;

                PackedScene shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
                _ship = shipScene.Instantiate<Ship>();
                _ship.Name = "AimShip";
                _root.AddChild(_ship);

                Check(_ship.GetNodeOrNull<Node3D>("MouseCatcher") != null,
                    "п.6b: у посаженного в идущей игре корабля есть узел-сборщик мыши");
                // added 2026-08: дефолтный вид пилота — «из глаз», chase-камера
                // включается по V. Прицел мы меряем именно chase-камерой, поэтому
                // включаем её тем же публичным путём, что и клавиша V.
                Check(_ship.GetNodeOrNull<Camera3D>("ChaseCamera") == null,
                    "п.6b: в дефолтном виде «из глаз» chase-камеры ещё нет");
                _ship.TogglePilotCamera();
                Check(_ship.GetNodeOrNull<Camera3D>("ChaseCamera") != null,
                    "п.6b: после V у корабля есть камера-преследователь (ею и меряем прицел)");
                Check(GetViewport()?.GetCamera3D() == _ship.GetNodeOrNull<Camera3D>("ChaseCamera"),
                    "п.6b: камера корабля стала активной в вьюпорте");

                // Пилотирование 2.0 крутит нос из ПОЗИЦИИ курсора. Чтобы схема
                // вообще могла проверить доворот, окно обязано вмещать слепую
                // зону (100px) внутри полного радиуса (¼ высоты): иначе на любом
                // пикселе доворот нулевой. Проверяем, что resize в _Ready сел.
                Vector2 vs = GetViewport().GetVisibleRect().Size;
                Check(PilotAim.FullSpeedRadiusPx(vs.Y) > PilotAim.DeadzoneRadiusPx,
                    $"п.6b: окно {vs} вмещает схему (полный радиус " +
                    $"{PilotAim.FullSpeedRadiusPx(vs.Y):F0}px > слепая зона " +
                    $"{PilotAim.DeadzoneRadiusPx:F0}px)");

                _mRot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _mYawBefore = _ship.GlobalTransform.Basis.GetEuler().Y;
                // Режим 2.0: прицел — ПОЗИЦИЯ курсора. Ставим курсор ЗА окружностью
                // полной скорости (горизонтально вправо) → команда 100% скорости.
                _mAimCursor = AimCenter() + new Vector2(PilotAim.DeadzoneRadiusPx + 100f, 0f);
                PushLookPos(_mAimCursor);
                _pilotFrames = 0;
                _pilotStep = 6;
                return;

            case 6:
            {
                // РЕЖИМ 2.0: скорость доворота = ПОЗИЦИЯ курсора относительно
                // центра (не дельта). Пока курсор держим за окружностью полной
                // скорости, команда 100% скорости ЖИВЁТ В КАЖДОМ кадре ввода
                // (PilotLookFromCursor пересчитывается каждый физшаг) — это и
                // отличает позиционную модель от дельтовой (где дельта жила лишь
                // в ПЕРВОМ кадре). Команда: полный радиус MaxAngularSpeed*dt.
                float fullPerStep = _ship.MaxAngularSpeed * FlightModel.FixedDt;
                if (_pilotFrames == 1)
                    Check(Mathf.Abs(_ship.LocalInput.LookYaw - fullPerStep) < 1e-4f &&
                          Mathf.Abs(_ship.LocalInput.LookPitch) < 1e-6f,
                          $"п.6b: курсор за окружностью — команда 100% скорости " +
                          $"(ожидание {fullPerStep:F4} рад/шаг, факт {_ship.LocalInput.LookYaw:F4})");
                if (_pilotFrames == 3)
                    Check(Mathf.Abs(_ship.LocalInput.LookYaw - fullPerStep) < 1e-4f,
                        $"п.6b: команда держится в кадре (позиция, не дельта): " +
                        $"факт {_ship.LocalInput.LookYaw:F4}");
                if (_pilotFrames < 2) return;

                float yaw = _ship.GlobalTransform.Basis.GetEuler().Y;
                float dyaw = yaw - _mYawBefore;
                Check(dyaw < -1e-3f,
                    $"п.6b: курсор справа от центра довернул нос (Δyaw={dyaw:F4} рад, " +
                    "нос уходит влево — знак как в дельтовой модели)");
                Check(AngleBetween(_mRot, _ship.GlobalTransform.Basis.GetRotationQuaternion()) > 0.02f,
                    "п.6b: ориентация корпуса реально изменилась от позиции курсора");

                // СЛЕПАЯ ЗОНА: ставим курсор вплотную к центру (внутри 100px) —
                // доворот обязан пропасть, команда ввода = 0. Поворот — ПРЯМАЯ
                // дозапись ориентации (ApplyLookRadians), угловой скорости не
                // копится: стоит обнулить look — yaw сразу замирает. therefore
                // снимаем отсчёт СЕЙЧАС (после доворота case6), и case7 меряет
                // приращение уже от слепой зоны.
                _mYawBefore = yaw;
                _mAimDz = AimCenter() + new Vector2(50f, 0f); // r=50 <= 100
                PushLookPos(_mAimDz);
                _pilotFrames = 0;
                _pilotStep = 7;
                return;
            }

            case 7:
                if (_pilotFrames < 2) return;

                // СЛЕПАЯ ЗОНА: курсор в 50px от центра (< 100px) → команда 0,
                // ориентация замирает (look — прямая дозапись, w не копится).
                float yaw2 = _ship.GlobalTransform.Basis.GetEuler().Y;
                Check(Mathf.Abs(yaw2 - _mYawBefore) < 1e-4f,
                    $"п.6b: курсор в слепой зоне — нос не доворачивает (Δ = {yaw2 - _mYawBefore:E2} рад)");
                // Команда ввода в слепой зоне тоже строго ноль (а не «мало»).
                Check(Mathf.Abs(_ship.LocalInput.LookYaw) < 1e-6f &&
                      Mathf.Abs(_ship.LocalInput.LookPitch) < 1e-6f,
                      "п.6b: в слепой зоне команда ввода = 0");
                // Aim-хелпер (направление выстрела) жив: центр ловится, угол — нет.
                Check(_ship.TestAimOnShip(AimCenter()),
                    "п.6b: центр экрана = направление выстрела активно");
                Check(!_ship.TestAimOnShip(AimCorner()),
                    "п.6b: угол экрана = направление выстрела отключено");

                // --- шаг «режим пилотирования / режим взаимодействия» ---
                // Правая кнопка мыши (toggle_interaction) гасит позиционный доворот:
                // в режиме взаимодействия НИЧТО не крутит нос (ни курсор, ни Q).
                Check(!_ship.IsInteractionMode, "п.6c: по умолчанию режим пилотирования");
                _ship.SetInteractionMode(true);
                Check(_ship.IsInteractionMode, "п.6c: переключение в режим взаимодействия");

                _mRot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _mYawBefore = _ship.GlobalTransform.Basis.GetEuler().Y;
                // Курсор за окружностью (в пилот-режиме дал бы 100%) + удерживаемая
                // Q — в режиме взаимодействия гейт обязан погасить позиционный канал.
                Input.ParseInputEvent(KeyEvt(Key.Q, true));
                PushLookPos(_mAimCursor);
                _pilotFrames = 0;
                _pilotStep = 8;
                return;

            case 8:
                // Механизм фичи виден в кадре ввода: в режиме взаимодействия нет
                // ни битов тангажа/рыскания (клавиши), ни дельты мыши. Проверяем
                // именно кадр, а не физический поворот: после предыдущей фазы у
                // корпуса ещё есть остаточная |w|, и она по инерции доворачивала бы
                // нос независимо от режима (демпфирование 0.5 гаснет не за 3 кадра).
                // Дельту мыши смотрим в ПЕРВОМ кадре — дальше кадр пустой anyway.
                if (_pilotFrames == 1)
                    Check(Mathf.Abs(_ship.LocalInput.LookYaw) < 1e-6f &&
                          Mathf.Abs(_ship.LocalInput.LookPitch) < 1e-6f,
                          "п.6c: в кадре взаимодействия нет дельты мыши");
                if (_pilotFrames < 3) return;

                Check((_ship.LocalInput.Mask & InputMask.AngularBits) == 0u,
                    "п.6c: в кадре взаимодействия нет битов тангажа/рыскания");
                Check((_ship.LocalInput.Mask & InputMask.TorqueRollLeft) == 0u,
                    "п.6c: удерживаемая Q не попала в кадр взаимодействия");

                // Возврат в режим пилотирования: та же удерживаемая Q и мышь
                // снова попадают в кадр.
                _ship.SetInteractionMode(false);
                Check(!_ship.IsInteractionMode, "п.6c: возврат в режим пилотирования");
                // Позиционный канал: ставим курсор за окружностью полной скорости —
                // в пилот-режиме это снова команда 100% (взаимодействие сняло гейт).
                PushLookPos(_mAimCursor);
                _pilotFrames = 0;
                _pilotStep = 9;
                return;

            case 9:
                // Позиционный доворот ЖИВЁТ В КАЖДОМ кадре (не дельта) — курсор
                // стоит за окружностью, команда держится. Проверяем в первом кадре:
                // сразу после снятия гейта взаимодействия нос снова слушает мышь.
                if (_pilotFrames == 1)
                    Check(Mathf.Abs(_ship.LocalInput.LookYaw -
                          _ship.MaxAngularSpeed * FlightModel.FixedDt) < 1e-4f,
                        "п.6c: в режиме пилотирования мышь вернулась в кадр");
                if (_pilotFrames < 3) return;
                Input.ParseInputEvent(KeyEvt(Key.Q, false));

                Check((_ship.LocalInput.Mask & InputMask.TorqueRollLeft) != 0u,
                    "п.6c: в режиме пилотирования Q вернулась в кадр (бит крена)");
                FinishPilotTest();
                return;
        }
    }

    /// <summary>Центр окна: туда смотрит камера-догонялка корабля, корпус в
    /// прицеле. Размер окна берём у вьюпорта, а не хардкодим: ship читает тот
    /// же `GetViewport().GetVisibleRect().Size` в `PilotLookFromCursor`, и у
    /// нас resize в `on-ready` поднимает окно до 1280×720. Если бы центр был
    /// хардкодом (или env-зависимым), курсор-прицел теста и центр, от которого
    /// отсчитывает нос корабль, разошлись бы — доворот считался бы по неверному
    /// направлению, а луч выстрела промахивался бы по корпусу.</summary>
    private Vector2 AimCenter()
    {
        Vector2 vs = GetViewport().GetVisibleRect().Size;
        return new Vector2(vs.X * 0.5f, vs.Y * 0.5f);
    }

    /// <summary>Дальний угол окна: луч камеры проходит мимо корпуса.</summary>
    private static Vector2 AimCorner() => new(1f, 1f);

    /// <summary>Одна мышиная дельта «как из жизни»: событие уходит вьюпорту,
    /// его ловит узел-сборщик под кораблём и передаёт кораблю. Сам `Ship` —
    /// `RigidBody3D` (`CollisionObject3D`), и мышь к нему напрямую не приходит
    /// (проба `tests/probes/ProbeInputHeadless.tscn`), поэтому тест идёт через
    /// вьюпорт, а не дёргает метод корабля.</summary>
    private void PushLook(float dx, float dy, Vector2 screenPos)
    {
        var ev = new InputEventMouseMotion
        {
            Relative = new Vector2(dx, dy),
            GlobalPosition = screenPos,
            Position = screenPos,
        };
        GetViewport().PushInput(ev, true);
    }

    /// <summary>Одна мышиная ПОЗИЦИЯ курсора-прицела (режим пилотирования 2.0):
    /// корабль доворачивает нос к этой экрменной точке со скоростью,
    /// пропорциональной её удалению от центра экрана. `Relative` не задаём —
    /// новый режим читает только позицию.</summary>
    private void PushLookPos(Vector2 screenPos)
    {
        var ev = new InputEventMouseMotion
        {
            GlobalPosition = screenPos,
            Position = screenPos,
        };
        GetViewport().PushInput(ev, true);
    }

    private void FinishPilotTest()
    {
        _pilotStep = -1;
        GD.Print($"[Stage2Test] п.4-6: pos={Fmt(_ship.GlobalPosition)} |v|={_ship.LinearVelocity.Length():F2} " +
                 $"|w|={_ship.AngularVelocity.Length():F3} tick={_ship.Tick}");
        // За пилотированием идут п.9-11: реконсиляция предсказания. Они не требуют
        // физических кадров (шов TestReconcile дёргает ядро ReconcileWith напрямую),
        // поэтому считаются синхронно и только потом — общий итог.
        RunReconcileTests();
        Finish();
    }

    // ==================================================================
    // П.9-п.11: предсказание + reconciliation на пилоте (шаг 2.4, §1.6)
    // ==================================================================
    //
    // Реконсиляция в бою вызывается из `_Process` по новой реплике `NetState`
    // (эталон с `AppliedTick`). Настоящего Synchronizer'а в этом тесте нет,
    // поэтому тест сеет предсказание и кольцо вводов швами (`TestSeedPrediction`,
    // `TestSeedPending`, `TestSetTick`) и дёргает ядро реконсиляции (`TestReconcile`)
    // — проверяется алгоритм (порог 0.05 / жёсткий откат / число переигранных
    // кадров), а не транспорт реплик (его покрывает NetTest). Всё синхронно:
    // между посевом и сверкой не проходит ни одного физкадра, поэтому авто-рекон-
    // силяция из `_Process` не вмешивается.

    private void RunReconcileTests()
    {
        GD.Print("[Stage2Test] п.9-11: реконсиляция предсказания пилота");

        PackedScene shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (shipScene == null)
        {
            Check(false, "п.9: не загрузился scenes/Ship.tscn");
            return;
        }
        Ship s = shipScene.Instantiate<Ship>();
        s.Name = "ReconcileShip";
        _root.AddChild(s);

        // --- п.9: appliedTick на 1 меньше последнего кадра → переигран РОВНО 1 кадр.
        // Кольцо: кадры 10, 11, 12 (тяга вперёд). Локальный тик = 13 (последний
        // отправленный кадр = 12). Эталон применил кадр 11 → переиграть надо только
        // кадр 12. Если код ошибочно переиграл бы все три, счётчик был бы 3.
        {
            Vector3 origin = new(100f, 0f, 0f); // эталон в стороне, чтобы не спутать с нулём
            // Скорость эталона НЕ нулевая: FlightModel.Step — симплектический Эйлер
            // (позиция двигается ТЕКУЩЕЙ скоростью, тяга дописывается в скорость).
            // С нулевой скоростью переигранный кадр сдвинул бы только скорость, но
            // не позицию, и assert «кадр реально сдвинул картинку» был бы ложно
            // красным. Даём эталону скорость вперёд по -Z, тогда картинка уезжает
            // по -Z и dot с -Z положителен.
            FlightState server = new()
            {
                Position = origin,
                Rotation = Quaternion.Identity,
                LinearVelocity = new Vector3(0, 0, -2f),
                AngularVelocity = Vector3.Zero,
            };
            // Предсказание далеко от эталона (> порога) → жёсткий откат + переигровка.
            s.TestSeedPrediction(new FlightState
            {
                Position = origin + new Vector3(0f, 0f, 5f),
                Rotation = Quaternion.Identity,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
            });
            s.TestSeedPending(InputFrame.FromMask(10, InputMask.ThrustF));
            s.TestSeedPending(InputFrame.FromMask(11, InputMask.ThrustF));
            s.TestSeedPending(InputFrame.FromMask(12, InputMask.ThrustF));
            s.TestSetTick(13);

            s.TestReconcile(server, appliedTick: 11);

            Check(s.LastReplayedCount == 1,
                $"п.9: appliedTick=11, тик=13 → переигран ровно 1 кадр (факт {s.LastReplayedCount})");
            // После отката к эталону и одной переигровки кадром 12 (тяга вперёд)
            // позиция сдвинулась ОТ эталона в сторону тяги корпуса (локальная -Z).
            Vector3 moved = s.GlobalPosition - origin;
            Check(moved.Length() > 1e-4f,
                $"п.9: переигранный кадр реально сдвинул картинку с эталона на {moved.Length():E3} м");
            var fwd = new Vector3(0, 0, -1); // -Z = «вперёд» корпуса (Godot forward)
            Check(moved.Dot(fwd) > 0f,
                $"п.9: сдвиг по тяге вперёд корпуса (dot с -Z = {moved.Dot(fwd):E3})");
        }

        // --- п.10: расхождение < порога (0.05) → позицию НЕ телепортируем,
        // только подтягиваем скорости (soft-sync). Иначе микро-дёрганье на шуме.
        {
            Vector3 predPos = new(0f, 0f, 0f);
            Vector3 servPos = new(0.04f, 0f, 0f); // |разница| = 0.04 < 0.05
            s.TestSeedPrediction(new FlightState
            {
                Position = predPos,
                Rotation = Quaternion.Identity,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
            });
            s.TestSetTick(1);
            FlightState server = new()
            {
                Position = servPos,
                Rotation = Quaternion.Identity,
                LinearVelocity = new Vector3(5f, 0f, 0f), // отличная скорость эталона
                AngularVelocity = Vector3.Zero,
            };
            s.TestReconcile(server, appliedTick: 0);

            Check(s.PredictionError < 0.05f,
                $"п.10: |err| = {s.PredictionError:F4} < порога 0.05");
            Check(s.GlobalPosition.DistanceTo(predPos) < 1e-5f,
                $"п.10: позиция НЕ телепортирована (осталась в предсказании, |сдвиг| = " +
                $"{s.GlobalPosition.DistanceTo(predPos):E2} м)");
            Check(s.GlobalPosition.DistanceTo(servPos) > 1e-3f,
                "п.10: картинка не прыгнула к эталону (мягкая синхронизация)");
            Check(s.LastReplayedCount == 0,
                $"п.10: при мягкой сверке кадров не переигрывается (факт {s.LastReplayedCount})");
        }

        // --- п.11: расхождение ~1 м → жёсткий откат к эталону (в пределах порога).
        // appliedTick = тик-1 → переигрывать нечего, картинка встаёт ровно на эталон.
        {
            Vector3 predPos = new(0f, 0f, 0f);
            Vector3 servPos = new(1f, 0f, 0f); // |разница| = 1 м >> порога
            s.TestSeedPrediction(new FlightState
            {
                Position = predPos,
                Rotation = Quaternion.Identity,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
            });
            s.TestSetTick(1); // нет кадров после appliedTick=0 → переигровка пуста
            FlightState server = new()
            {
                Position = servPos,
                Rotation = Quaternion.Identity,
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
            };
            s.TestReconcile(server, appliedTick: 0);

            Check(Mathf.Abs(s.PredictionError - 1f) < 1e-3f,
                $"п.11: |err| зафиксирован как расхождение до отката ({s.PredictionError:F4} ≈ 1 м)");
            Check(s.GlobalPosition.DistanceTo(servPos) < 0.05f,
                $"п.11: жёсткий откат — картинка встала к эталону (|до эталона| = " +
                $"{s.GlobalPosition.DistanceTo(servPos):E2} м < 0.05)");
            Check(s.LastReplayedCount == 0,
                $"п.11: при appliedTick=тик-1 переигрывать нечего (факт {s.LastReplayedCount})");
        }

        s.QueueFree();
    }

    // ==================================================================
    // П.12: потолок угловой скорости корабля (MaxAngularSpeed)
    // ==================================================================
    // Модель «каждому кораблю — своя предельная скорость вращения»: одна и та
    // же ручка управления даёт разный результат у кораблей с разным лимитом.
    private void RunAngularSpeedLimitTests()
    {
        GD.Print("[Stage2Test] п.12: лимит угловой скорости (MaxAngularSpeed)");

        // Одинаковый кадр-«ручка» с зажатой Q/E (крен) на длинном прогоне: без
        // лимита момент раскручивает корпус неограниченно, с лимитом — выходит
        // на плато и дальше не растёт.
        var frame = InputFrame.FromMask(1, InputMask.TorqueRollLeft);

        var pNoLimit = new FlightParams
        {
            Mass = 1200f, LinearThrust = 90000f, AngularThrust = 40000f,
            LinearDamping = 0f, AngularDamping = 0f, BoostMultiplier = 2f,
            MaxAngularSpeed = 0f, // без ограничения — как во всех прежних прогонах
        };
        var pLimit = pNoLimit;
        pLimit.MaxAngularSpeed = 2f; // рад/с — «тяжёлый» freighter

        // Park(), а не default: у структурного нуля кватернион нулевой, и
        // Basis(нулевой кватернион) даёт NaN во всех осях.
        var noLimit = FlightState.Park();
        var limited = FlightState.Park();
        float maxSeenLimited = 0f;
        for (int i = 0; i < 600; i++)
        {
            frame.Tick = (uint)(i + 1);
            noLimit = FlightModel.Step(noLimit, pNoLimit, frame, FlightModel.FixedDt);
            limited = FlightModel.Step(limited, pLimit, frame, FlightModel.FixedDt);
            maxSeenLimited = Mathf.Max(maxSeenLimited, limited.AngularVelocity.Length());
        }

        Check(noLimit.AngularVelocity.Length() > 10f,
            $"п.12: без лимита Q раскручивает корпус неограниченно " +
            $"(|w| = {noLimit.AngularVelocity.Length():F2} рад/с за 10 с)");
        Check(Mathf.Abs(limited.AngularVelocity.Length() - 2f) < 1e-3f,
            $"п.12: с лимитом 2 рад/с |w| встаёт на плато " +
            $"({limited.AngularVelocity.Length():F4} ≈ 2)");
        Check(maxSeenLimited <= 2f + 1e-6f,
            $"п.12: лимит не превышен ни на одном шаге (максимум {maxSeenLimited:F6} ≤ 2)");

        // Направление вращения сохраняется: клиппится модуль, а не оси.
        Vector3 wDir = limited.AngularVelocity.Normalized();
        Check(Mathf.Abs(Mathf.Abs(wDir.Z) - 1f) < 1e-3f &&
              wDir.X * wDir.X + wDir.Y * wDir.Y < 1e-6f,
            "п.12: лимит не меняет ось вращения (крен остался вокруг Z)");

        // Диагональный момент: лимит по модулю, а не «каждая ось по 2».
        var diag = FlightState.Park();
        var pDiag = pNoLimit;
        pDiag.MaxAngularSpeed = 2f;
        var diagFrame = InputFrame.FromMask(1,
            InputMask.TorqueRollLeft | InputMask.TorqueYawLeft | InputMask.TorquePitchUp);
        for (int i = 0; i < 600; i++)
        {
            diagFrame.Tick = (uint)(i + 1);
            diag = FlightModel.Step(diag, pDiag, diagFrame, FlightModel.FixedDt);
        }
        Check(Mathf.Abs(diag.AngularVelocity.Length() - 2f) < 1e-3f,
            $"п.12: при вращении по трём осям сразу ограничен МОДУЛЬ (|w| = " +
            $"{diag.AngularVelocity.Length():F4} ≈ 2), а не каждая ось по отдельности");

        // Старые прогоны (лимит = 0 — структурный ноль в FlightParams, как в
        // Params() тестов) обязаны считаться побитово как раньше — иначе все
        // замеры стадий 2-5 поедут.
        // Повторяет pNoLimit во всех полях, кроме MaxAngularSpeed = 0. Иначе
        // сверялись бы РАЗНЫЕ параметры (без демпфирования и с ним) — тривиальный
        // прогон.
        var structZero = pNoLimit;
        structZero.MaxAngularSpeed = 0f;
        FlightState a = FlightState.Park(), b = FlightState.Park();
        for (int i = 0; i < 100; i++)
        {
            frame.Tick = (uint)(i + 1);
            a = FlightModel.Step(a, pNoLimit, frame, FlightModel.FixedDt);
            b = FlightModel.Step(b, structZero, frame, FlightModel.FixedDt);
        }
        Check(a.AngularVelocity == b.AngularVelocity && a.Rotation == b.Rotation,
            "п.12: MaxAngularSpeed = 0 (структурный ноль) = без ограничения, " +
            "числа совпадают побитово");

        // --- Мышь: потолок применяется и к прямому довороту носа ---
        // 100 пикселей за кадр при чувствительности 0.0025 = 0,25 рад — слишком
        // много для «быстрого» корабля; за один шаг 1/60 с лимитом 2 рад/с
        // допустимо не более 2/60 = 0,0333 рад.
        Vector2 lookBig = new Vector2(30f, 30f) * 0.0025f; // (0.075, 0.075)
        float dt = FlightModel.FixedDt;
        Vector2 clamped = FlightModel.ClampLookRadians(lookBig, 2f, dt);
        Check(Mathf.Abs(clamped.Length() - 2f * dt) < 1e-6f,
            $"п.12: мышиная дельта ограничена MaxAngularSpeed·dt " +
            $"(|look| = {clamped.Length():F6} = {2f * dt:F6})");
        Check(clamped.X == clamped.Y && clamped.X > 0f,
            "п.12: дельта режется пропорционально — направление доворота не меняется");
        Check(FlightModel.ClampLookRadians(lookBig, 0f, dt) == lookBig,
            "п.12: лимит 0 = дельта проходит побитово как есть (старые прогоны)");
        Vector2 small = new Vector2(0.001f, 0.002f);
        Check(FlightModel.ClampLookRadians(small, 2f, dt) == small,
            "п.12: дельта внутри лимита не трогается");

        // Живой Ship: мышь не должна поворачивать нос быстрее лимита корабля.
        if (GD.Load<PackedScene>("res://scenes/Ship.tscn") is PackedScene lookScene)
        {
            // Предел задаётся ДО AddChild: `_Ready` переносит его в Flight сам —
            // так проверка подтверждает, что настройка корабля доходит до модели.
            Ship s = lookScene.Instantiate<Ship>();
            s.MaxAngularSpeed = 1.5f;
            AddChild(s);
            s.TestSetPilotForTest(1);
            s.TestSetVelocity(Vector3.Zero);

            // Рывок мыши: 400 пикселей за один кадр (≈ 1 рад) — заведомо больше,
            // чем корабль проворачивает нос за шаг.
            s.TestAddLookDelta(new Vector2(400f, 0f));
            s.TestStepForTest(dt);
            float turned = s.GlobalTransform.Basis.GetRotationQuaternion().AngleTo(Quaternion.Identity);
            // Допуск 1e-4: AngleTo ≈ acos след/2 около нуля усиливает ошибку
            // float32 до ~ε/sin(θ) ≈ 5e-6 при угле 0,025 рад.
            Check(turned <= s.MaxAngularSpeed * dt + 1e-4f,
                $"п.12: рывок мыши повернул нос не быстрее лимита корпуса " +
                $"({turned:F6} рад ≤ {s.MaxAngularSpeed * dt:F6})");

            // И клавиша Q/E: корпус не раскручивается выше своего предела.
            var keyFrame = InputFrame.FromMask(10_000, InputMask.TorqueRollLeft);
            for (int i = 0; i < 600; i++)
            {
                keyFrame.Tick = (uint)(10_000 + i + 1);
                s.TestSetInputForTest(keyFrame);
                s.TestStepForTest(dt);
            }
            Check(Mathf.Abs(s.AngularVelocity.Length() - s.MaxAngularSpeed) < 1e-3f,
                $"п.12: живым Q/E корпус встал на свой потолок " +
                $"(|w| = {s.AngularVelocity.Length():F4} ≈ {s.MaxAngularSpeed})");

            // Другой корабль той же сцены — с другим пределом: настройка
            // действительно принадлежит кораблю, а не модели полёта вообще.
            Ship s2 = lookScene.Instantiate<Ship>();
            s2.MaxAngularSpeed = 0.5f;
            AddChild(s2);
            s2.TestSetPilotForTest(1);
            var keyFrame2 = InputFrame.FromMask(20_000, InputMask.TorquePitchUp);
            for (int i = 0; i < 600; i++)
            {
                keyFrame2.Tick = (uint)(20_000 + i + 1);
                s2.TestSetInputForTest(keyFrame2);
                s2.TestStepForTest(dt);
            }
            Check(s2.AngularVelocity.Length() < 0.501f &&
                  s2.AngularVelocity.Length() < s.AngularVelocity.Length() - 0.5f,
                $"п.12: у корабля с MaxAngularSpeed = 0.5 потолок свой " +
                $"(|w| = {s2.AngularVelocity.Length():F4} < 0.51, у первого {s.AngularVelocity.Length():F2})");

            s.QueueFree();
            s2.QueueFree();
        }

        // ======================================================================
        GD.Print("[Stage2Test] п.12b: AngularRiseTime — набор вращения стартует с 0 " +
                 "и растёт до максимума, пока зажата клавиша");
        // ======================================================================
        {
            FlightParams rp = Params();
            rp.AngularThrust = 1000f;   // a = 1000/100 = 10 рад/с²
            rp.Mass = 100f;
            rp.MaxAngularSpeed = 1f;    // потолок команды
            rp.AngularRiseTime = 0.9f;  // 95% команды за 0.9 с (τ = 0.3 с)
            rp.AngularDamping = 5f;     // в режиме следования НЕ участвует
            var roll = InputFrame.FromMask(1, InputMask.TorqueRollLeft);
            var empty = new InputFrame { Tick = 2 };

            // Срез времени: сколько секунд ушло на 95% потолка.
            FlightState s = FlightState.Park();
            float t = 0f;
            int step95 = -1;
            for (int i = 0; i < 400; i++)
            {
                var st = FlightModel.Step(s, rp, roll, dt);
                s = st; t += dt;
                if (step95 < 0 && st.AngularVelocity.Length() >= 0.95f) step95 = i + 1;
            }
            float t95 = step95 * dt;
            Check(Math.Abs(t95 - 0.9f) < 0.03f,
                $"п.12b: 95% потолка набирается за заданное время ({t95:F3} с ≈ 0.9 с)");

            // Старт РОВНО с нуля: первый же шаг даёт прирост, но не «пинок».
            FlightState s0 = FlightState.Park();
            var first = FlightModel.Step(s0, rp, roll, dt);
            float w1 = first.AngularVelocity.Length();
            Check(w1 < 1f * (dt / 0.3f) + 1e-6f,
                $"п.12b: первый шаг — малый прирост, не импульс ({w1:F6} рад/с)");

            // Монотонность и отсутствие «затухания после пика» во время удержания.
            float prev = -1f; bool monotonic = true, overshoot = false;
            s0 = FlightState.Park();
            for (int i = 0; i < 600; i++)
            {
                s0 = FlightModel.Step(s0, rp, roll, dt);
                float w = s0.AngularVelocity.Length();
                if (w < prev - 1e-9f) monotonic = false;
                if (w > 1f + 1e-6f) overshoot = true;
                prev = w;
            }
            Check(monotonic, "п.12b: |ω| растёт монотонно всё удержание клавиши");
            Check(!overshoot, $"п.12b: перелёта через потолок нет (итог {prev:F6})");
            Check(Math.Abs(prev - 1f) < 1e-4f,
                  $"п.12b: ω встаёт на потолок и ДЕРЖИТСЯ на нём (10 с удержания: {prev:F6})");

            // Отпустили — команда «ноль», и спад идёт тем же законом и с той же
            // τ, что и набор: симметрия времени раскрутки и остановки. Демпфер
            // AngularDamping в режиме следования не участвует (иначе спад шёл бы
            // быстрее набора). За 3τ = 0.9 с ω падает до 5% потолка.
            s0 = FlightState.Park();
            for (int i = 0; i < 600; i++) s0 = FlightModel.Step(s0, rp, roll, dt);
            float wDecayStart = s0.AngularVelocity.Length();
            int stepDown95 = -1;
            float tDown = 0f;
            for (int i = 0; i < 400; i++)
            {
                s0 = FlightModel.Step(s0, rp, empty, dt);
                tDown += dt;
                if (stepDown95 < 0 && s0.AngularVelocity.Length() <= 0.05f * wDecayStart)
                    stepDown95 = i + 1;
            }
            float tDown95 = stepDown95 * dt;
            Check(Math.Abs(tDown95 - 0.9f) < 0.03f,
                  $"п.12b: 95% спад после отпускания — за то же время, что и набор " +
                  $"({tDown95:F3} с ≈ {t95:F3} с разгона)");
            Check(s0.AngularVelocity.Length() < 1e-4f,
                  $"п.12b: после долгогоidle ω легла в ноль ({s0.AngularVelocity.Length():E3})");

            // Монотонность спада: без «пинка» вниз и без отскока.
            s0 = FlightState.Park();
            for (int i = 0; i < 600; i++) s0 = FlightModel.Step(s0, rp, roll, dt);
            prev = 1e9f; monotonic = true;
            prev = s0.AngularVelocity.Length();
            for (int i = 0; i < 400; i++)
            {
                s0 = FlightModel.Step(s0, rp, empty, dt);
                float w = s0.AngularVelocity.Length();
                if (w > prev + 1e-9f) monotonic = false;
                prev = w;
            }
            Check(monotonic, "п.12b: |ω| убывает монотонно всё время после отпускания");

            // Корабль из каталога (живой курьер): раскрутка от Q/E — не «пинок».
            if (GD.Load<PackedScene>("res://scenes/Ship.tscn") is PackedScene riseScene)
            {
                Ship s3 = riseScene.Instantiate<Ship>();
                AddChild(s3);
                s3.TestSetPilotForTest(1);
                s3.TestSetVelocity(Vector3.Zero);
                var kf = InputFrame.FromMask(30_000, InputMask.TorqueRollLeft);
                float cap = s3.MaxAngularSpeed;
                float wAtRamp = 0f, wEnd = 0f, prevW = -1f;
                bool rises = true;
                int riseFrames95 = 0;
                // Курьер: AngularRiseTime = 0.6 с ⇒ τ = 0.2 с (τ не может быть
                // меньше физ. предела сопел Mk1: ωcap/a = 6/33.3 = 0.18 с).
                for (int i = 0; i < 55; i++) // ~0.92 с удержания Q
                {
                    kf.Tick = (uint)(30_000 + i + 1);
                    s3.TestSetInputForTest(kf);
                    s3.TestStepForTest(dt);
                    float w = s3.AngularVelocity.Length();
                    if (w < prevW - 1e-9f) rises = false;
                    if (riseFrames95 == 0 && w >= 0.95f * cap) riseFrames95 = i + 1;
                    prevW = w;
                    if (i == 11) wAtRamp = w;  // 0.2 с = одна τ
                    wEnd = w;
                }
                // За одну τ апериодическая система набирает 63% команды; старый
                // импульсный режим к этому моменту уже упёрся бы в потолок.
                Check(wAtRamp > 0.3f * cap && wAtRamp < 0.75f * cap,
                    $"п.12b: курьер на середине разгона (0.2 с) идёт в {wAtRamp / cap:P0} " +
                    $"своего потолка, а не уже на пределе");
                Check(rises, "п.12b: живой курьер — |ω| растёт монотонно всё удержание Q");
                Check(Math.Abs(wEnd - cap) < 0.1f * cap,
                    $"п.12b: и держится на потолке после выхода ({wEnd:F3} ≈ {cap})");

                // Симметрия на ЖИВОМ курьере: отпустили Q — спад идёт той же τ,
                // значит на 95% спуска (до 5% потолка) уходит то же время, что и
                // на набор (0.6 с = 36 кадров). Старый вид давал бы здесь ~вдвое
                // меньше/больше: быстрый набор + медленный спад по AngularDamping.
                int fallFrames = 0;
                prevW = wEnd; bool monotonicFall = true;
                for (int i = 0; i < 200; i++)
                {
                    var idle = new InputFrame { Tick = (uint)(30_000 + 55 + i + 1) };
                    s3.TestSetInputForTest(idle);
                    s3.TestStepForTest(dt);
                    float w = s3.AngularVelocity.Length();
                    if (fallFrames == 0 && w <= 0.05f * cap) fallFrames = i + 1;
                    if (w > prevW + 1e-9f) monotonicFall = false;
                    prevW = w;
                }
                Check(fallFrames > 0 && Math.Abs(fallFrames - riseFrames95) <= 6,
                      $"п.12b: живой курёр остановил вращение за {fallFrames} кадров " +
                      $"(разгон до 95% — {riseFrames95} кадров): симметрия разгона и остановки");
                Check(monotonicFall,
                    "п.12b: после отпускания Q живой курьер не отскакивает и не пикует");
                s3.QueueFree();
            }
        }
    }

    // ==================================================================
    // Служебное: assert'ы и финал
    // ==================================================================
    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else { _fail++; _failures.Add(what); }
        _lines.Add((ok ? "OK   " : "FAIL ") + what);
        GD.Print((ok ? "  [OK]   " : "  [FAIL] ") + what);
        if (_label != null) _label.Text = string.Join("\n", _lines);
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        GD.Print($"=== Stage2Test: итог: {_pass} OK, {_fail} FAIL ===");
        foreach (string f in _failures) GD.PrintErr("  FAIL: " + f);
        _lines.Add($"итог: {_pass} OK, {_fail} FAIL");
        if (_label != null) _label.Text = string.Join("\n", _lines);
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }

    private bool _finished = false;
}
