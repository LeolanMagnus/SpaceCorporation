using Godot;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Тест чистого правила захвата (шаг 2 плана frame-plan.md). Запуск:
///   ./run-tests.sh res://tests/CaptureRulesTest.tscn
/// Всё считается в _Ready без физического шага: CaptureRules — чистая функция,
/// вход — только числа. Проверяются вход/выход, гистерезисы (радиус и масса),
/// «влетел быстро — не захват», гасфазы родителя (док/сам захвачен), выбор
/// наибольшего родителя, корректность v_rel и FrameVelocity.
/// </summary>
public partial class CaptureRulesTest : Node
{
    private int _pass = 0;
    private int _fail = 0;
    private readonly List<string> _failures = new();
    private bool _finished = false;

    public override void _Ready()
    {
        GD.Print("=== CaptureRulesTest: начало ===");

        // ---- п.1 базовый вход: маленький внутри пузыря большого, тихо ----
        CaptureBody small = new() { Mass = 1200f, Position = new Vector3(5f, 0f, 0f), Velocity = new Vector3(100f, 0f, 0f), Radius = 1f };
        CaptureBody big = new() { Mass = 10000f, Position = Vector3.Zero, Velocity = new Vector3(101f, 0f, 0f), Radius = 40f };
        CaptureParentState okParent = new();

        CaptureDecision d = CaptureRules.Evaluate(small, big, okParent, false);
        Check(d.Captured && d.Reason == CaptureReject.None, "вход: маленький в тихом пузыре большого захвачен");
        // v_rel = v_A - v_B = (100,0,0)-(101,0,0) = (-1,0,0)
        Check(d.RelativeVelocity.DistanceTo(new Vector3(-1f, 0f, 0f)) < 1e-3f, "v_rel считается всегда (и при входе)");

        // уже захваченный остаётся (тот же вход)
        d = CaptureRules.Evaluate(small, big, okParent, true);
        Check(d.Captured && d.Reason == CaptureReject.None, "удержание: захваченный остаётся в пузыре");

        // ---- п.2 масса не превосходит (вход) ----
        CaptureBody heavy = small; heavy.Mass = 4000f; // 10000/4000 = 2.5 < 3
        d = CaptureRules.Evaluate(heavy, big, okParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.MassRatioLow, "вход: масса 2.5× < RatioMin=3 — отказ");

        // ---- п.3 вне пузыря ----
        CaptureBody far = small; far.Position = new Vector3(0f, 0f, 45f); // > 40
        d = CaptureRules.Evaluate(far, big, okParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.OutsideBubble, "вход: 45 м > радиуса 40 — отказ");

        // ---- п.4 влетел слишком быстро ----
        CaptureBody fast = small; fast.Velocity = new Vector3(127f, 0f, 0f); // |v_rel| = 26 > 25
        d = CaptureRules.Evaluate(fast, big, okParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.TooFast, "вход: |v_rel| = 26 > VAttachMax=25 — отказ (пролетел мимо)");
        CaptureBody atLimit = small; atLimit.Velocity = new Vector3(126f, 0f, 0f); // |v_rel| = 25 — не больше, а равно: вход разрешён
        d = CaptureRules.Evaluate(atLimit, big, okParent, false);
        Check(d.Captured, "граница TooFast: |v_rel| = 25 (не >) — захват");
        CaptureBody borderline = small; borderline.Velocity = new Vector3(120f, 0f, 0f); // |v_rel| = 19
        d = CaptureRules.Evaluate(borderline, big, okParent, false);
        Check(d.Captured, "вход: |v_rel| = 19 < 25 — захват");

        // ---- п.5 гистерезис по радиусу: 42 м = между 40 и 56 ----
        CaptureBody at42 = small; at42.Position = new Vector3(0f, 0f, 42f);
        d = CaptureRules.Evaluate(at42, big, okParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.OutsideBubble, "гистерезис r: 42 м > 40 — новый вход запрещён");
        d = CaptureRules.Evaluate(at42, big, okParent, true);
        Check(d.Captured, "гистерезис r: 42 м < 40×1.4=56 — удержание");
        CaptureBody at57 = small; at57.Position = new Vector3(0f, 0f, 57f);
        d = CaptureRules.Evaluate(at57, big, okParent, true);
        Check(!d.Captured && d.Reason == CaptureReject.LeftBubble, "выход: 57 м > 56 — расхват LeftBubble");

        // ---- п.6 гистерезис по массе: ratio 2.5 между 2 и 3 ----
        CaptureBody mid = small; mid.Mass = 4000f;
        d = CaptureRules.Evaluate(mid, big, okParent, true);
        Check(d.Captured, "гистерезис m: ratio 2.5 ≥ RatioRelease=2 — удержание");
        CaptureBody lightParent = small; // сам A лёгкий, B потяжелел наоборот
        CaptureBody bigLight = big; bigLight.Mass = 2500f; // 2500/1200 ≈ 2.08 ≥ 2 — удержание на грани
        d = CaptureRules.Evaluate(small, bigLight, okParent, true);
        Check(d.Captured, "гистерезис m: ratio 2.08 ≥ 2 — удержание");
        CaptureBody bigLighter = big; bigLighter.Mass = 2300f; // 2300/1200 ≈ 1.92 < 2 — выход
        d = CaptureRules.Evaluate(small, bigLighter, okParent, true);
        Check(!d.Captured && d.Reason == CaptureReject.ParentMassDrop, "выход: ratio 1.92 < 2 — расхват ParentMassDrop");
        _ = lightParent;

        // ---- п.7 гасфазы родителя (в обе стороны перехода) ----
        CaptureParentState dockedParent = new() { InDockPhase = true };
        d = CaptureRules.Evaluate(small, big, dockedParent, true);
        Check(!d.Captured && d.Reason == CaptureReject.ParentInDockPhase, "док-фаза гасит существующий захват (R6)");
        d = CaptureRules.Evaluate(small, big, dockedParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.ParentInDockPhase, "док-фаза родителя — вход запрещён");

        CaptureParentState capturedParent = new() { Captured = true };
        d = CaptureRules.Evaluate(small, big, capturedParent, false);
        Check(!d.Captured && d.Reason == CaptureReject.ParentCaptured, "вложенность: сам захвачен — не родитель (R5)");
        d = CaptureRules.Evaluate(small, big, capturedParent, true);
        Check(!d.Captured && d.Reason == CaptureReject.ParentCaptured, "вложенность: родитель стал захвачен — расхват");

        // ---- п.8 выбор наибольшего родителя (в тесте — два кандидата вручную) ----
        CaptureBody candidateSmall = new() { Mass = 5200f, Position = new Vector3(3f, 0f, 0f), Velocity = new Vector3(100f, 0f, 0f), Radius = 1f };
        CaptureParentState ps2 = new();
        CaptureDecision dBig = CaptureRules.Evaluate(candidateSmall, big, ps2, false); // 10000/5200 = 1.92 < 3 — отказ
        CaptureBody haulerParent = big; haulerParent.Mass = 5200f;
        CaptureDecision dHauler = CaptureRules.Evaluate(candidateSmall, haulerParent, ps2, false); // 1.0 < 3 — отказ
        CaptureBody courier = big; courier.Mass = 10000f;
        CaptureBody hauler = candidateSmall;
        CaptureDecision dCourierParent = CaptureRules.Evaluate(hauler, courier, ps2, false); // 10000/5200 ≈ 1.92 — тот же отказ
        // Корректный позитивный случай: freighter(10000) забирает courier(1200)
        CaptureDecision dPick = CaptureRules.Evaluate(small, big, ps2, false);
        Check(!dBig.Captured && !dHauler.Captured && dPick.Captured,
            "иерархия масс: freighter>hauler>courier — захватывает только превосходящий ≥3×");
        _ = dCourierParent;

        // ---- п.9 v_rel при «везёт» почти нулевая, при независимом манёвре — ненулевая ----
        CaptureBody carried = small; carried.Velocity = big.Velocity; // синхронно
        d = CaptureRules.Evaluate(carried, big, okParent, true);
        Check(d.Captured && d.RelativeVelocity.Length() < 1e-4f, "везёт синхронно: v_rel ≈ 0");
        CaptureBody maneuvering = small; maneuvering.Velocity = big.Velocity + new Vector3(0f, 12.4f, 0f);
        d = CaptureRules.Evaluate(maneuvering, big, okParent, true);
        Check(d.Captured && Mathf.Abs(d.RelativeVelocity.Length() - 12.4f) < 1e-3f, "локальный манёвр: v_rel = 12.4 (синий HUD)");

        // ---- п.10 FrameVelocity: без вращения = v родителя; с ω — + ω×r (пока off) ----
        Vector3 fv = CaptureRules.FrameVelocity(Vector3.Zero, new Vector3(100f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 10f), false);
        Check(fv.DistanceTo(new Vector3(100f, 0f, 0f)) < 1e-4f, "FrameVelocity (rot off) = v родителя (R3: ω-член выключен)");
        Vector3 fvr = CaptureRules.FrameVelocity(Vector3.Zero, new Vector3(100f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 10f), true);
        // ω×r = (0,1,0)×(0,0,10) = (10,0,0)
        Check(fvr.DistanceTo(new Vector3(110f, 0f, 0f)) < 1e-3f, "FrameVelocity (rot on) = v + ω×r (для будущей стадии)");

        // ---- п.11 константы тюнинга соответствуют плану ----
        Check(Mathf.Abs(CaptureTuning.RatioMin - 3f) < 1e-6f && Mathf.Abs(CaptureTuning.RatioRelease - 2f) < 1e-6f
            && Mathf.Abs(CaptureTuning.VAttachMax - 25f) < 1e-6f && Mathf.Abs(CaptureTuning.Hysteresis - 1.4f) < 1e-6f
            && CaptureTuning.CheckEveryN == 4, "CaptureTuning = 3 / 2 / 25 / 1.4 / 4 (plan §4)");

        // ---- п.12 крейсерский гейт (cruise-capture-plan §5) ----
        // Флаг по умолчанию ВЫКЛ → поведение не меняется (регрессия 24).
        bool flagWas = CaptureTuning.RequireCruisingParent;
        try
        {
            CaptureParentState notCruising = new() { Cruising = false };
            CaptureParentState cruising = new() { Cruising = true };

            // (а) гейт ВЫКЛ: родитель не крейсер — всё равно захват (нет гейта).
            CaptureTuning.RequireCruisingParent = false;
            d = CaptureRules.Evaluate(small, big, notCruising, false);
            Check(d.Captured, "гейт ВЫКЛ: не-крейсер родитель — вход ок (регрессия)");

            // (б) гейт ВКЛ: родитель крейсер → вход ок.
            CaptureTuning.RequireCruisingParent = true;
            d = CaptureRules.Evaluate(small, big, cruising, false);
            Check(d.Captured && d.Reason == CaptureReject.None, "гейт ВКЛ: крейсер-родитель — вход ок");

            // (в) гейт ВКЛ: родитель не крейсер → вход отклонён ParentNotCruising.
            d = CaptureRules.Evaluate(small, big, notCruising, false);
            Check(!d.Captured && d.Reason == CaptureReject.ParentNotCruising,
                "гейт ВКЛ: не-крейсер — вход отклонён ParentNotCruising");

            // (г) гейт ВКЛ, АКТИВНЫЙ захват + родитель потерял крейсер → зацеп
            // ЖИВЁТ (гейт только на входе, §2.1.2): не рвём по потере крейсера.
            d = CaptureRules.Evaluate(small, big, notCruising, true);
            Check(d.Captured, "гейт ВКЛ: потеря крейсера НЕ рвёт активный зацеп");

            // (д) гейт ВКЛ: гасфазы по-прежнему в обе стороны, даже у не-крейсера
            // (проверяем, что гейт крейсера не «перебивает» приоритет гасфаз).
            CaptureParentState dockNotCruising = new() { InDockPhase = true, Cruising = false };
            d = CaptureRules.Evaluate(small, big, dockNotCruising, false);
            Check(!d.Captured && d.Reason == CaptureReject.ParentInDockPhase,
                "гейт ВКЛ: док-фаза приоритетнее отказа крейсера");
        }
        finally
        {
            CaptureTuning.RequireCruisingParent = flagWas;
        }

        Finish();
    }

    private void Check(bool ok, string what)
    {
        if (ok) _pass++;
        else { _fail++; _failures.Add(what); }
        GD.Print((ok ? "  [OK]   " : "  [FAIL] ") + what);
    }

    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        GD.Print($"=== CaptureRulesTest: итог: {_pass} OK, {_fail} FAIL ===");
        foreach (string f in _failures) GD.PrintErr("  FAIL: " + f);
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }
}
