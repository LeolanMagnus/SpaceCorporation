using Godot;
using System;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест Этапа 6 («жидкий космос»: пилот-ассист, потолок стрейфа, модули).
/// Запуск:
///   ./run-tests.sh res://tests/LiquidSpaceTest.tscn
/// Проверяет разделы Р1–Р3 плана liquid-space-plan.md на уровне ЧИСТОЙ модели
/// (без сети и узлов, кроме п.11+ — они появляются вместе с ShipLoadout):
///
///   п.1  ассист ВКЛ: отпустил тягу — скорость монолитно падает на a·dt за шаг
///   п.2  плато: при лимите скорость выходит ровно на потолок
///   п.3  потолок стрейфа: ни один шаг не выше лимита, оси не «схлопнуты»
///   п.4  диагональ W+D: обе оси ≤ лимита, вектор не схлопнут в сферу
///   п.5  ассист ВЫКЛ: инерция побитово вечная (регрессия прежнего поведения)
///   п.6  время торможения == время набора (тот же модуль ускорения)
///   п.7  проскок через ноль невозможен: при |v| ≤ a·dt отдаём ровно 0
///   п.8  детерминизм: два прогона с одним вводом дают побитово одно и то же
///   п.9  переигровка Replay с новым флагом воспроизводит «живой» прогон
///   п.10 смена флага в середине прогона реально меняет историю состояний
///
/// Стиль assert'ов — как в Stage2Test (`Check(ok, what)`, финал `Finish()`).
/// </summary>
public partial class LiquidSpaceTest : Node3D
{
    private int _pass = 0;
    private int _fail = 0;
    private readonly List<string> _failures = new();

    private Label _label;
    private readonly List<string> _lines = new();

    private const float Dt = FlightModel.FixedDt;

    /// <summary>Базовые параметры «как на Ship»: a = 90000/1200 = 75 м/с²,
    /// демпфирования движком-моделью нет (LinearDamp = 0 на Ship).</summary>
    private static FlightParams Base() => new()
    {
        Mass = 1200f,
        LinearThrust = 90000f,
        AngularThrust = 40000f,
        LinearDamping = 0f,
        AngularDamping = 1.5f,
        BoostMultiplier = 2f,
    };

    /// <summary>Параметры с включённым ассистом: торможение = ускорению набора.</summary>
    private static FlightParams Assisted(float maxLinear = 0f)
    {
        FlightParams p = Base();
        p.PilotAssist = true;
        p.AssistDragAccel = p.Mass > 0f ? p.LinearThrust / p.Mass : 0f; // 75 м/с²
        p.MaxLinearSpeed = maxLinear;
        return p;
    }

    public override void _Ready()
    {
        GD.Print("=== LiquidSpaceTest: начало ===");

        _label = new Label();
        _label.Position = new Vector2I(16, 16);
        AddChild(_label);

        RunAssistBrakingTests();
        RunSpeedCapTests();
        RunCoastRegressionTests();
        RunDeterminismTests();
        RunLoadoutTests(); // Этап 6, п.11–п.15: каталог модулей и ShipLoadout.Build

        Finish();
    }

    // ==================================================================
    // П.1/п.6/п.7 — торможение ассиста: линейное, = ускорению набора, без проскока
    // ==================================================================
    private void RunAssistBrakingTests()
    {
        GD.Print("[LiquidSpaceTest] п.1/6/7: торможение пилот-ассиста");
        FlightParams p = Assisted();
        float accel = p.LinearThrust / p.Mass; // 75 м/с²

        // Разгон 1 с (зажатый W), потом coast с включённым ассистом.
        InputFrame fwd = InputFrame.FromMask(1, InputMask.ThrustF);
        var s = new FlightState { Rotation = Quaternion.Identity };
        for (int i = 0; i < 60; i++)
        {
            fwd.Tick = (uint)(i + 1);
            s = FlightModel.Step(s, p, fwd, Dt);
        }
        float vAfterThrust = s.LinearVelocity.Length();
        Check(Mathf.Abs(vAfterThrust - accel * 1f) < 1e-3f,
            $"п.1: разгон 1 с дал |v| = {vAfterThrust:F3} ≈ a·t = {accel * 1f:F3} (ассист не мешает тяге)");

        // Дальше — пустые кадры: скорость должна падать ровно на a·dt за шаг.
        InputFrame idle = new() { Tick = 100 };
        bool mono = true;
        float prev = s.LinearVelocity.Length();
        int coastSteps = 0;
        while (s.LinearVelocity.Length() > 0f && coastSteps < 200)
        {
            idle.Tick = (uint)(100 + coastSteps + 1);
            s = FlightModel.Step(s, p, idle, Dt);
            float now = s.LinearVelocity.Length();
            // Шаг либо падает ровно на a·dt, либо обнуляется (последний шаг).
            bool ok = now == 0f ? prev <= accel * Dt + 1e-5f
                                : Mathf.Abs((prev - now) - accel * Dt) < 1e-3f;
            if (!ok) { mono = false; break; }
            prev = now;
            coastSteps++;
        }
        Check(mono && s.LinearVelocity == Vector3.Zero,
            $"п.1: coast гасится постоянным ускорением до нуля ({coastSteps} шагов, v={s.LinearVelocity})");

        // п.6: время торможения == время набора (тот же модуль ускорения).
        Check(Mathf.Abs(coastSteps - 60) <= 1,
            $"п.6: шагов торможения {coastSteps} == шагов набора 60 (±1) → a_drag == a_thrust");

        // п.7: проскок через ноль невозможен — хелпер на малой скорости даёт ровно 0.
        Vector3 small = new(1f, 0f, 0f);
        Vector3 b = FlightModel.BrakeToZero(small, accel, Dt);
        Check(b == Vector3.Zero, $"п.7: |v| < a·dt ⇒ ровно Vector3.Zero (получено {b})");
        Vector3 big = new(1000f, 0f, 0f);
        Vector3 c = FlightModel.BrakeToZero(big, accel, Dt);
        Check(c.X > 0f && Mathf.Abs(big.X - c.X - accel * Dt) < 1e-4f,
            $"п.7: |v| > a·dt ⇒ Δv = a·dt строго по направлению (c.X = {c.X:F4})");
        // Торможение не разворачивает вектор (знак сохраняется до обнуления).
        Check(Mathf.Sign(c.X) == Mathf.Sign(big.X), "п.7: направление не меняется");
    }

    // ==================================================================
    // П.2/п.3/п.4 — потолок линейной скорости
    // ==================================================================
    private void RunSpeedCapTests()
    {
        GD.Print("[LiquidSpaceTest] п.2/3/4: потолок стрейфа");
        const float cap = 50f;
        FlightParams p = Base();
        p.PilotAssist = true;
        p.AssistDragAccel = p.LinearThrust / p.Mass;
        p.MaxLinearSpeed = cap;

        // п.3: 600 шагов W — ни один шаг не выше лимита, оси не трогает лишнего.
        InputFrame fwd = InputFrame.FromMask(1, InputMask.ThrustF);
        var s = new FlightState { Rotation = Quaternion.Identity };
        bool capped = true;
        for (int i = 0; i < 600; i++)
        {
            fwd.Tick = (uint)(i + 1);
            s = FlightModel.Step(s, p, fwd, Dt);
            if (s.LinearVelocity.Length() > cap + 1e-4f) { capped = false; break; }
            if (Mathf.Abs(s.LinearVelocity.X) > 1e-6f || Mathf.Abs(s.LinearVelocity.Y) > 1e-6f)
            { capped = false; break; } // тяга вперёд = мировая -Z при нулевой ориентации
        }
        Check(capped && Mathf.Abs(s.LinearVelocity.Length() - cap) < 0.6f * Dt * 10f,
            $"п.3: 600 шагов W ⇒ плато |v| = {s.LinearVelocity.Length():F4} ≈ {cap} (лимит держится)");

        // п.2: время выхода на плато = cap / a (±1 шаг).
        InputFrame idle = new();
        var s2 = new FlightState { Rotation = Quaternion.Identity };
        int toCap = 0;
        while (s2.LinearVelocity.Length() < cap && toCap < 1000)
        {
            fwd.Tick = (uint)(++toCap);
            s2 = FlightModel.Step(s2, p, fwd, Dt);
        }
        float expect = cap / (p.LinearThrust / p.Mass) / Dt; // шагов до потолка
        Check(Mathf.Abs(toCap - expect) <= 1.01f,
            $"п.2: плато за {toCap} шагов ≈ cap/a = {expect:F1} шагов");

        // п.4: диагональ W+D — обе «рабочие» оси ≤ лимита, вектор не схлопнут.
        InputFrame diag = InputFrame.FromMask(1, InputMask.ThrustF | InputMask.ThrustR);
        var s3 = new FlightState { Rotation = Quaternion.Identity };
        for (int i = 0; i < 600; i++)
        {
            diag.Tick = (uint)(i + 1);
            s3 = FlightModel.Step(s3, p, diag, Dt);
        }
        Check(Mathf.Abs(s3.LinearVelocity.X) <= cap + 1e-4f &&
              Mathf.Abs(s3.LinearVelocity.Z) <= cap + 1e-4f &&
              Mathf.Abs(s3.LinearVelocity.X) > cap * 0.9f &&
              Mathf.Abs(s3.LinearVelocity.Z) > cap * 0.9f,
            $"п.4: диагональ W+D ⇒ обе оси у потолка, не схлопнуты: {s3.LinearVelocity}");
        Check(Mathf.Abs(s3.LinearVelocity.Y) < 1e-6f,
            "п.4: по Y (вверх/вниз) скорости нет — оси независимы");
    }

    // ==================================================================
    // П.5 — регрессия: ассист ВЫКЛ = прежняя вечная инерция
    // ==================================================================
    private void RunCoastRegressionTests()
    {
        GD.Print("[LiquidSpaceTest] п.5: ассист выключен — инерция вечна");
        FlightParams p = Base(); // PilotAssist = false (структурный ноль)
        InputFrame fwd = InputFrame.FromMask(1, InputMask.ThrustF);
        var s = new FlightState { Rotation = Quaternion.Identity };
        for (int i = 0; i < 60; i++)
        {
            fwd.Tick = (uint)(i + 1);
            s = FlightModel.Step(s, p, fwd, Dt);
        }
        Vector3 vAfterThrust = s.LinearVelocity;
        InputFrame idle = new() { Tick = 100 };
        for (int i = 0; i < 300; i++) // 5 секунд coast
        {
            idle.Tick = (uint)(100 + i + 1);
            s = FlightModel.Step(s, p, idle, Dt);
        }
        Check(s.LinearVelocity == vAfterThrust,
            $"п.5: ассист ВЫКЛ — |v| побитово та же через 5 с ({s.LinearVelocity.Length():F6})");
    }

    // ==================================================================
    // П.8/п.9/п.10 — детерминизм, переигровка и смена флага
    // ==================================================================
    private void RunDeterminismTests()
    {
        GD.Print("[LiquidSpaceTest] п.8/9/10: детерминизм и смена флага");
        FlightParams p = Assisted(120f);

        // Сценарий: 60 шагов W, 60 шагов idle, 60 шагов S+boost, лимит 120.
        List<InputFrame> frames = Scripted(180);

        // п.8: прогон «вживая» шаг за шагом vs переигровка Replay — побитово одно.
        FlightState live = new() { Rotation = Quaternion.Identity };
        for (int i = 0; i < frames.Count; i++) live = FlightModel.Step(live, p, frames[i], Dt);
        FlightState replayed = FlightModel.Replay(new FlightState { Rotation = Quaternion.Identity }, p, frames, 0, Dt);
        Check(live.Position == replayed.Position &&
              live.Rotation == replayed.Rotation &&
              live.LinearVelocity == replayed.LinearVelocity &&
              live.AngularVelocity == replayed.AngularVelocity,
            $"п.8: step-by-step и Replay — побитово одинаково (pos={live.Position}, |v|={live.LinearVelocity.Length():F4})");

        // п.9: переигровка с ДРУГИМ флагом ассиста (хард-роллбек с новым PilotAssist)
        // воспроизводит «живой» прогон, где флаг менялся на том же кадре.
        const uint switchTick = 60; // после 60-го кадра ассист выключается
        FlightParams pAfter = p; pAfter.PilotAssist = false; pAfter.AssistDragAccel = 0f;

        FlightState liveSwitch = new() { Rotation = Quaternion.Identity };
        for (int i = 0; i < frames.Count; i++)
        {
            FlightParams pp = (i + 1 <= switchTick) ? p : pAfter;
            liveSwitch = FlightModel.Step(liveSwitch, pp, frames[i], Dt);
        }
        // Состояние на момент переключения (после 60-го кадра) — «серверная истина»,
        // с неё начинается hard rollback; далее кадры 61..180 переигрываются с pAfter.
        FlightState atSwitch = new() { Rotation = Quaternion.Identity };
        for (int i = 0; i < (int)switchTick; i++) atSwitch = FlightModel.Step(atSwitch, p, frames[i], Dt);
        FlightState rolled = FlightModel.Replay(atSwitch, pAfter, frames, switchTick, Dt);
        Check(rolled.Position == liveSwitch.Position &&
              rolled.LinearVelocity == liveSwitch.LinearVelocity,
            $"п.9: hard rollback с новым флагом = живой прогон с той же сменой (|v|={rolled.LinearVelocity.Length():F4})");

        // п.10: тот же ввод целиком с ассистом ВЫКЛ — история обязана отличаться
        // (флаг участвует в шаге, а не только «в UI»).
        FlightState alwaysOff = FlightModel.Replay(new FlightState { Rotation = Quaternion.Identity }, pAfter, frames, 0, Dt);
        Check(alwaysOff.Position != live.Position,
            $"п.10: ассист выключен на всём прогоне ⇒ финал другой: {alwaysOff.Position} vs {live.Position}");
    }


    /// <summary>Скрипт ввода для п.8–п.10: 60×W, 60×idle, 60×S+boost.</summary>
    private static List<InputFrame> Scripted(int count)
    {
        var list = new List<InputFrame>(count);
        for (int i = 0; i < count; i++)
        {
            uint tick = (uint)(i + 1);
            uint mask = i < 60 ? InputMask.ThrustF
                      : i < 120 ? 0u
                      : (InputMask.ThrustB | InputMask.Boost);
            list.Add(InputFrame.FromMask(tick, mask));
        }
        return list;
    }

    // ==================================================================
    // П.11–п.15: каталог модулей, ShipLoadout.Build, сети-шов флага (Этап 6)
    // ==================================================================
    private void RunLoadoutTests()
    {
        GD.Print("[LiquidSpaceTest] п.11: пустой loadout = побитовый base");

        // Пустые слоты + ассист выключен → Build обязан вернуть base «как есть»:
        // все новые поля структурного нуля (0) означают «нет мнения», Build их
        // не трогает (кроме PilotAssist/AssistDragAccel = выкл → 0).
        FlightParams b = Base();
        var empty = new ShipLoadout();
        foreach (ModuleKind k in System.Enum.GetValues<ModuleKind>())
            empty.EquipKind(k, null);
        empty.PilotAssist = false;
        FlightParams got = empty.Build(b);
        bool same = got.Mass == b.Mass && got.LinearThrust == b.LinearThrust &&
                    got.AngularThrust == b.AngularThrust &&
                    got.LinearDamping == b.LinearDamping &&
                    got.AngularDamping == b.AngularDamping &&
                    got.BoostMultiplier == b.BoostMultiplier &&
                    got.MaxAngularSpeed == b.MaxAngularSpeed &&
                    got.ThrustAccel == b.ThrustAccel &&
                    got.AngularAccel == b.AngularAccel &&
                    got.MaxLinearSpeed == b.MaxLinearSpeed &&
                    got.PilotAssist == b.PilotAssist &&
                    got.AssistDragAccel == b.AssistDragAccel;
        Check(same, "п.11: пустые слоты + assist OFF → FlightParams побитово = base");

        // Комплектация по умолчанию (Mk1) — базовые ускорения корпуса:
        // тяга 75 м/с² (90 кН/1200 кг), потолок 260 м/с, форсаж 1+0.5+0.5=2.
        var def = new ShipLoadout();
        Check(Mathf.Abs(def.RawThrustAccel() - 75f) < 1e-4f &&
              Mathf.Abs(def.RawMaxLinearSpeed() - 260f) < 1e-4f &&
              Mathf.Abs(def.BoostMultiplier() - 2f) < 1e-4f &&
              def.Powered() == 1f,
            $"п.11b: Mk1-комплектация: a = {def.RawThrustAccel():F2}, cap = " +
            $"{def.RawMaxLinearSpeed():F0}, boost = {def.BoostMultiplier():F2}, " +
            $"powered = {def.Powered():F2}");

        // ── п.12: потолок стрейфа не мешает режиму стыковки ─────────────────
        GD.Print("[LiquidSpaceTest] п.12: лимит скорости vs стыковка (≤ 8 м/с)");
        {
            // Cruise до потолка 50, затем торможение обратной тягой до порога
            // Docking.DockSpeedLimit: клиппинг «после импульса» не должен мешать
            // медленным режимам (пружина захвата живёт на скоростях ≤ 8 м/с).
            FlightParams p = Assisted(50f);
            var s = new FlightState { Rotation = Quaternion.Identity };
            uint tick = 0;
            for (int i = 0; i < 600; i++)
                s = FlightModel.Step(s, p, InputFrame.FromMask(++tick, InputMask.ThrustF), Dt);
            Check(s.LinearVelocity.Length() <= 50f + 1e-3f &&
                  Mathf.Abs(s.LinearVelocity.Length() - 50f) < 1f,
                $"п.12: крейс при лимите 50 встал на плато (|v| = {s.LinearVelocity.Length():F3})");
            // Торможение — самим ассистом на idle (BrakeToZero): 50 м/с при
            // тормозе 75 м/с² гаснут за ≈ 0.67 с; 1 с idle с запасом.
            for (int i = 0; i < 60; i++)
                s = FlightModel.Step(s, p, InputFrame.FromMask(++tick, 0u), Dt);
            float vDock = s.LinearVelocity.Length();
            Check(vDock <= Docking.DockSpeedLimit,
                $"п.12: после торможения |v| = {vDock:F3} ≤ порога захвата " +
                $"{Docking.DockSpeedLimit} — лимит не мешает стыковке");
        }

        // ── п.14: замена модуля меняет характеристики предсказуемо ──────────
        GD.Print("[LiquidSpaceTest] п.14: Mk I → Mk II (замена двигателя/РСУ)");
        {
            var lo = new ShipLoadout();
            lo.PilotAssist = false;
            // Реактор RT-2 сразу: Mk2-сборка потребляет 6.5 усл. ед., RT-1 (6)
            // устроил бы деградацию 6/6.5 — а здесь нужны ЧИСТЫЕ значения каталога.
            lo.Equip("reactor_rt2_mk2");
            FlightParams p1 = lo.Build(Base());
            Check(lo.Equip("engine_ics_mk2") && lo.Equip("rcs_ics_mk2"),
                "п.14: Mk II встали в свои слоты ( Equip проверяет вид )");
            FlightParams p2 = lo.Build(Base());
            Check(lo.Powered() == 1f &&
                  Mathf.Abs(p2.ThrustAccel - 110f) < 1e-4f &&
                  Mathf.Abs(p2.MaxLinearSpeed - 340f) < 1e-4f &&
                  Mathf.Abs(p2.AngularAccel - 60f) < 1e-4f,
                $"п.14: Mk II: a = {p2.ThrustAccel:F1}, cap = {p2.MaxLinearSpeed:F0}, " +
                $"α = {p2.AngularAccel:F1} — ровно значения каталога");
            Check(p2.MaxLinearSpeed > p1.MaxLinearSpeed && p2.ThrustAccel > p1.ThrustAccel,
                "п.14: Mk II быстрее Mk I по обоим характеристикам");
            FlightParams p2b = lo.Build(Base());
            Check(p2b.ThrustAccel == p2.ThrustAccel && p2b.MaxLinearSpeed == p2.MaxLinearSpeed,
                "п.14: Build идемпотентен (повторный вызов не «накапливает» бонус)");
            Check(!lo.Equip("radar_rn1_mk1") || lo.Get(ModuleKind.Radar) == "radar_rn1_mk1",
                "п.14: модуль уходит в слот СВОЕГО вида (чужой слот не перезаписывается)");
            // Отказ: неизвестный id не меняет слот (не сбрасывает Mk II).
            Check(!lo.Equip("engine_nonexistent") &&
                  lo.Get(ModuleKind.Engine) == "engine_ics_mk2",
                "п.14: неизвестный id — отказ, слот сохранён");
        }

        // ── п.15: деградация по питанию ─────────────────────────────────────
        GD.Print("[LiquidSpaceTest] п.15: реактор слабее — ускорения и потолок деградируют");
        {
            var lo = new ShipLoadout();
            lo.Equip("engine_ics_mk3"); // draw 4
            lo.Equip("rcs_ics_mk3");    // draw 2 (+ radar 1 + cap 1 = 8 при output 6)
            FlightParams p = lo.Build(Base());
            float powered = lo.Powered();
            Check(powered > 0f && powered < 1f &&
                  Mathf.Abs(powered - 6f / 8f) < 1e-4f,
                $"п.15: Powered = Output/ΣDraw = {powered:F3} (6/8)");
            Check(Mathf.Abs(p.ThrustAccel - 150f * powered) < 1e-3f &&
                  Mathf.Abs(p.MaxLinearSpeed - 420f * powered) < 1e-3f,
                $"п.15: ThrustAccel = {p.ThrustAccel:F1}, cap = {p.MaxLinearSpeed:F1} — " +
                "умножены на Powered, конечны и неотрицательны");
            Check(p.ThrustAccel >= 0f && p.MaxLinearSpeed >= 0f &&
                  !float.IsInfinity(p.ThrustAccel) && !float.IsNaN(p.MaxLinearSpeed),
                "п.15: деградация не даёт отрицательных/бесконечных значений");
            // Тормоз ассиста деградирует СРАЗУ с тягой (иначе «ползал бы с перелётом»).
            lo.PilotAssist = true;
            FlightParams pa = lo.Build(Base());
            Check(pa.AssistDragAccel == pa.ThrustAccel,
                "п.15: AssistDragAccel = деградированной тяге (тормоз = разгон и в деградации)");
            // Нулевой реактор: Powered = 0 → ускорения 0, без деления на ноль.
            lo.EquipKind(ModuleKind.Reactor, null);
            FlightParams pz = lo.Build(Base());
            Check(pz.ThrustAccel == 0f && pz.MaxLinearSpeed == 0f && !float.IsNaN(pz.ThrustAccel),
                "п.15: реактор снят → Powered = 0: ускорения 0 без NaN/∞");
        }

        // ── п.13: флаг ассиста в сети (реальный Ship + NetState) ────────────
        GD.Print("[LiquidSpaceTest] п.13: реплика флага и гейт RPC на реальном Ship");
        {
            PackedScene shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
            if (shipScene == null)
            {
                Check(false, "п.13: не загрузился scenes/Ship.tscn");
                return;
            }
            Ship s = shipScene.Instantiate<Ship>();
            s.Name = "AssistNetShip";
            AddChild(s);

            Check(s.Net != null, "п.13: у корабля есть NetState (носитель реплики флага)");
            Check(s.Loadout.PilotAssist && s.Net!.PilotAssist == 1,
                "п.13: по умолчанию флаг включён и в loadout, и в реплике");

            // Гейт: переключение принимает только пилот (offline пилот = 1).
            Check(!s.TestRpcToggleAssistForTest(2, false),
                "п.13: RPC от НЕ-пилота отклонён (чужой piron не меняет физику чужого)");
            Check(s.Loadout.PilotAssist && s.Net!.PilotAssist == 1,
                "п.13: после отказа реплика и loadout не изменились");

            // Пилот: offline-путь (сервер = пилот) — флаг + реплика меняются сразу.
            bool now = s.TestTogglePilotAssistForTest();
            Check(!now && !s.Loadout.PilotAssist && s.Net!.PilotAssist == 0,
                "п.13: T от пилота: loadout и реплика синхронно погасли");
            Check(s.Flight.AssistDragAccel == 0f && !s.Flight.PilotAssist,
                "п.13: Flight пересобран: торможения ассиста больше нет");

            // Возврат через RPC-шов от пилота (путь внешнего пилота без UDP).
            Check(s.TestRpcToggleAssistForTest(1, true),
                "п.13: RPC от пилота принят (гейт по PilotPeerId)");
            Check(s.Net!.PilotAssist == 1 && s.PilotAssistLocal,
                "п.13: принятый флаг опубликован в реплике и в локальном loadout");

            // Откатов переключение не добавило (п.13: ложных реконсиляций нет).
            Check(s.AssistRollbacks == 0,
                $"п.13: переключения не добавили жёстких откатов (счётчик {s.AssistRollbacks})");

            // Замена модуля на живом корабле меняет Flight (п.14 на узле).
            float capBefore = s.Flight.MaxLinearSpeed;
            Check(s.EquipModule("engine_ics_mk2") && s.Flight.MaxLinearSpeed > capBefore,
                $"п.14/узел: EquipModule Mk II на корабле: потолок {capBefore:F0} → " +
                $"{s.Flight.MaxLinearSpeed:F0} м/с");

            s.QueueFree();
        }
    }

    // ==================================================================
    // Служебное: assert'ы и финал (стиль Stage2Test)
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
        GD.Print($"=== LiquidSpaceTest: итог: {_pass} OK, {_fail} FAIL ===");
        foreach (string f in _failures) GD.PrintErr("  FAIL: " + f);
        _lines.Add($"итог: {_pass} OK, {_fail} FAIL");
        if (_label != null) _label.Text = string.Join("\n", _lines);
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }
}
