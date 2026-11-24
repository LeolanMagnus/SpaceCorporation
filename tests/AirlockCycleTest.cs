using Godot;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Тест чистого правила цикла шлюза (фаза 2, F2.1; habitable-deck-phase2-plan.md).
/// Запуск: ./run-tests.sh res://tests/AirlockCycleTest.tscn
/// AirlockCycle — чистая функция (вход — только були), физического шага нет.
/// Проверяются: три разрешённых перехода, все refus-ветки, инвариант
/// «обе двери открыты недостижим» (перебором всех состояний × всех действий).
/// </summary>
public partial class AirlockCycleTest : Node
{
    private int _pass = 0;
    private int _fail = 0;
    private readonly List<string> _failures = new();

    public override void _Ready()
    {
        GD.Print("=== AirlockCycleTest: начало ===");

        // ---- п.1 три разрешённых перехода --------------------------------
        var d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = false, Inside = false, OnBoots = false },
            HatchAction.OpenExternalFromOutside);
        Check(d.Allowed && d.ExternalOpen && !d.InternalOpen,
            "открыть наружную снаружи из герметичного шлюза");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = true, InternalOpen = false, Inside = true, OnBoots = false },
            HatchAction.CycleIn);
        Check(d.Allowed && !d.ExternalOpen && d.InternalOpen && d.BootsOn,
            "цикл внутрь: наружная закр., внутр. откр., ботинки ВКЛ");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = true, Inside = true, OnBoots = true },
            HatchAction.CycleOut);
        Check(d.Allowed && d.ExternalOpen && !d.InternalOpen && !d.BootsOn,
            "цикл наружу: внутр. закр., наружная откр., ботинки ВЫКЛ");

        // ---- п.2 refus-ветки ---------------------------------------------
        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = true, InternalOpen = false, Inside = false, OnBoots = false },
            HatchAction.OpenExternalFromOutside);
        Check(!d.Allowed && d.Reason == HatchReject.NotSealed,
            "отказ: наружную при уже открытой двери — NotSealed");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = true, InternalOpen = false, Inside = true, OnBoots = true },
            HatchAction.CycleIn);
        Check(!d.Allowed && d.Reason == HatchReject.WrongStateIn,
            "отказ: цикл внутрь в ботинках — WrongStateIn");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = true, InternalOpen = false, Inside = true, OnBoots = false },
            HatchAction.CycleOut);
        Check(!d.Allowed && d.Reason == HatchReject.WrongStateOut,
            "отказ: цикл наружу без ботинок/при открытой наружной — WrongStateOut");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = false, Inside = false, OnBoots = true },
            HatchAction.CycleIn);
        Check(!d.Allowed && d.Reason == HatchReject.WrongStateIn,
            "отказ: цикл внутрь снаружи (у герметичного) — WrongStateIn");

        // Внутри герметичного шлюза снаружи-действие — отказ (не та сторона).
        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = false, Inside = true, OnBoots = false },
            HatchAction.OpenExternalFromOutside);
        Check(!d.Allowed && d.Reason == HatchReject.OutsideNotAtDoor,
            "отказ: «открыть снаружи» изнутри — OutsideNotAtDoor");

        // CancelToSpace — откат CycleIn, только из тупика «внутри, джетпак,
        // наружная закрыта, внутренняя открыта» (после принятого CycleIn,
        // если ботинки не включились).
        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = true, Inside = true, OnBoots = false },
            HatchAction.CancelToSpace);
        Check(d.Allowed && d.ExternalOpen && !d.InternalOpen && !d.BootsOn,
            "CancelToSpace: из тупика — наружная открыта, внутренняя закрыта");

        d = AirlockCycle.Evaluate(
            new HatchState { ExternalOpen = false, InternalOpen = true, Inside = true, OnBoots = true },
            HatchAction.CancelToSpace);
        Check(!d.Allowed,
            "отказ: CancelToSpace в ботинках (это штатный CycleOut, не откат)");

        // Тупик покрыт: у состояния «внутри+джетпак+только внутренняя открыта»
        // есть хотя бы одно разрешённое действие.
        var dead = new HatchState { ExternalOpen = false, InternalOpen = true, Inside = true, OnBoots = false };
        bool any = false;
        foreach (HatchAction a in System.Enum.GetValues<HatchAction>())
            if (AirlockCycle.Evaluate(dead, a).Allowed) any = true;
        Check(any, "тупик CycleIn-без-опоры имеет выход (CancelToSpace)");

        // ---- п.3 инвариант: «обе открыты» недостижим (полный перебор) ----
        // Состояния-тоги (8 шт., включая запрещённое «обе открыты» — из него
        // ни одно действие не должно выдать «обе открыты» снова), 3 действия.
        int illegalOut = 0;
        for (int st = 0; st < 16; st++)
        {
            var s = new HatchState
            {
                ExternalOpen = (st & 1) != 0,
                InternalOpen = (st & 2) != 0,
                Inside = (st & 4) != 0,
                OnBoots = (st & 8) != 0,
            };
            foreach (HatchAction a in System.Enum.GetValues<HatchAction>())
            {
                var dec = AirlockCycle.Evaluate(s, a);
                if (dec.Allowed && dec.ExternalOpen && dec.InternalOpen)
                    illegalOut++;
            }
        }
        Check(illegalOut == 0, $"инвариант: ни один переход не открывает обе двери ({illegalOut} нарушений)");

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
        GD.Print($"=== AirlockCycleTest: итог: {_pass} OK, {_fail} FAIL ===");
        foreach (string f in _failures) GD.PrintErr("  FAIL: " + f);
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }
}
