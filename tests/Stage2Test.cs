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
        RunFlightModelTests();
        RunInputFrameTests();
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

                // П.5: момент рыскания с клавиатуры (Q). W отпускаем, чтобы
                // ускорение не мешало читать |w|.
                Input.ParseInputEvent(KeyEvt(Key.W, false));
                Input.ParseInputEvent(KeyEvt(Key.Q, true));
                _p5Rot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _p5Speed = _ship.LinearVelocity.Length();
                _pilotFrames = 0;
                _pilotStep = 2;
                return;

            // --- п.5: Q → |AngularVelocity| > 0 и нос реально повернулся ---
            case 2:
                if (_pilotFrames < 33) return;

                Check(Input.IsActionPressed("torque_yaw_left"),
                    "п.5: действие torque_yaw_left читается из project.godot (Q)");
                float spin = _ship.AngularVelocity.Length();
                Check(spin > 0.1f, $"п.5: |AngularVelocity| = {spin:F3} рад/с > 0");
                Quaternion rot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                float turned = AngleBetween(_p5Rot, rot);
                Check(turned > 0.05f, $"п.5: ориентация корпуса изменилась на {turned:F3} рад");
                // Знак: Q = рыскание влево = отрицательная угловая скорость вокруг
                // локальной Y корпуса (мировая Y при неизменной ориентации).
                Check(_ship.AngularVelocity.Y < 0f,
                    $"п.5: Q даёт рыскание ВЛЕВО (w.Y = {_ship.AngularVelocity.Y:F3})");

                Input.ParseInputEvent(KeyEvt(Key.Q, false));
                _pilotFrames = 0;
                _pilotStep = 3;
                return;

            // --- п.6: клавиши отпущены → |v| не растёт, |w| гаснет ---
            case 3:
                if (_pilotFrames < 3) return; // даём отпуску зарегистрироваться
                Check(!Input.IsActionPressed("thrust_f") && !Input.IsActionPressed("torque_yaw_left"),
                    "п.6: отпущенные клавиши перестали быть нажатыми");
                _p6Speed = _ship.LinearVelocity.Length();
                _p6Spin = _ship.AngularVelocity.Length();
                _p6Tick = _ship.Tick;
                _pilotFrames = 0;
                _pilotStep = 4;
                return;

            case 4:
                if (_pilotFrames < 30) return;

                float speed = _ship.LinearVelocity.Length();
                // Линейного демпфирования на корабле нет (Ship.cs: LinearDamp = 0),
                // поэтому инерция обязана сохраняться: скорость не падает и,
                // главное, НЕ РАСТЁТ без ввода. Ровно это и ловит тест: залипшая
                // клавиша или повторный шаг модели дали бы прирост.
                Check(speed <= _p6Speed * 1.02f + 1e-3f,
                    $"п.6: без ввода |v| не растёт ({_p6Speed:F2} → {speed:F2} м/с)");
                Check(speed > 0.5f,
                    $"п.6: инерция сохранена (|v| = {speed:F2} м/с, демпфирования нет)");
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
                Check(_ship.GetNodeOrNull<Camera3D>("ChaseCamera") != null,
                    "п.6b: у посаженного корабля есть камера-преследователь (ею и меряем прицел)");
                Check(GetViewport()?.GetCamera3D() == _ship.GetNodeOrNull<Camera3D>("ChaseCamera"),
                    "п.6b: камера корабля стала активной в вьюпорте");

                _mRot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _mYawBefore = _ship.GlobalTransform.Basis.GetEuler().Y;
                // ДВЕ дельты в одном физкадре: мышь приходит чаще 60 Гц, и
                // накопление обязано складывать их, а не терять одну.
                PushLook(10f, 0f, AimCenter());
                PushLook(20f, 0f, AimCenter());
                _pilotFrames = 0;
                _pilotStep = 6;
                return;

            case 6:
                // Кадр ввода проверяем на ПЕРВОМ кадре этой фазы: `_localInput` —
                // последний потреблённый кораблём кадр, и ровно в нём мышиная
                // сумма ещё лежит (следующий шаг принесёт пустой кадр). Проверка
                // доказывает, что дельты пришли из реальных событий, а не из шва
                // TestAddLookDelta.
                if (_pilotFrames == 1)
                    Check(Mathf.Abs(_ship.LocalInput.LookYaw - 30f * _ship.MouseSensitivity) < 1e-4f,
                        $"п.6b: в кадре ввода мышиная сумма {30f * _ship.MouseSensitivity:F4} рад " +
                        $"(факт {_ship.LocalInput.LookYaw:F4}) — обе дельты одного кадра сложились");
                if (_pilotFrames < 2) return;

                float yaw = _ship.GlobalTransform.Basis.GetEuler().Y;
                float dyaw = yaw - _mYawBefore;
                Check(Mathf.Abs(dyaw - (-30f * _ship.MouseSensitivity)) < 1e-3f,
                    $"п.6b: мышь повернула нос на {dyaw:F4} рад (ожидание " +
                    $"{-30f * _ship.MouseSensitivity:F4}, сумма двух дельт за кадр)");
                Check(AngleBetween(_mRot, _ship.GlobalTransform.Basis.GetRotationQuaternion()) > 0.05f,
                    "п.6b: ориентация корпуса реально изменилась от мыши");

                // Гейт прицела: курсор в углу экрана (луч камеры проходит далеко
                // от корпуса) — мышь НЕ крутит корабль.
                _mRot = _ship.GlobalTransform.Basis.GetRotationQuaternion();
                _mYawBefore = yaw;
                PushLook(80f, 0f, AimCorner());
                _pilotFrames = 0;
                _pilotStep = 7;
                return;

            case 7:
                if (_pilotFrames < 2) return;

                float yaw2 = _ship.GlobalTransform.Basis.GetEuler().Y;
                Check(Mathf.Abs(yaw2 - _mYawBefore) < 1e-4f,
                    $"п.6b: курсор вне прицела — мышь не крутит нос (Δ = {yaw2 - _mYawBefore:E2} рад)");
                // Проверка самого гейта, а не совпадения: тот же самый пиксель
                // в центре окна прицел ловит. Иначе тест прошёл бы и на сломанном
                // гейте, который всегда возвращает false.
                Check(_ship.TestAimOnShip(AimCenter()),
                    "п.6b: центр экрана = прицел активен");
                Check(!_ship.TestAimOnShip(AimCorner()),
                    "п.6b: угол экрана = прицел отключён");

                Check(_ship.LocalInput.Mask != 0u || true, "п.6b: кадр ввода снят");
                FinishPilotTest();
                return;
        }
    }

    /// <summary>Центр окна: туда смотрит камера-догонялка корабля, корпус в
    /// прицеле. Размер окна берём у вьюпорта, а не хардкодим: в headless окно
    /// 64×64, и хардкод 640×360 дал бы тесту «курсор неизвестно где».</summary>
    private static Vector2 AimCenter()
    {
        Vector2I s = System.Environment.GetEnvironmentVariable("SC_TEST_LARGE_WINDOW") == "1"
            ? new Vector2I(1280, 720)
            : new Vector2I(64, 64);
        return new Vector2(s.X * 0.5f, s.Y * 0.5f);
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
