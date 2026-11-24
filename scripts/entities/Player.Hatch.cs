using Godot;

namespace SpaceCorporation;

/// <summary>
/// Шлюзование (docs/habitable-deck-plan.md, шаг «шлюз обратно»): переход
/// «снаружи → внутрь» через типовой шлюз (Airlock).
///
/// Клавиша F (в свободном EVA-режиме, если под курсором нет панели) —
/// одно действие на все состояния шлюза, машина состояний проста:
///   1. Игрок снаружи у наружной двери, шлюз герметичен ⇒ ОТКРЫТЬ наружную
///      дверь (дальше игрок влетает внутрь на джетпаке).
///   2. Игрок внутри шлюза на джетпаке, наружная дверь открыта ⇒ ЦИКЛ
///      ШЛЮЗОВАНИЯ: закрыть наружную дверь → включить магнитные ботинки
///      (разворот «ногами к палубе», как при G на палубе) → открыть
///      внутреннюю дверь. Игрок выходит на палубу.
///   3. Игрок в ботинках внутри шлюза, внутренняя дверь открыта ⇒ ОБРАТНЫЙ
///      ЦИКЛ: закрыть внутреннюю → снять ботинки (свободный полёт) →
///      открыть наружную. Игрок выходит в космос.
///
/// Пока однопользовательски/оффлайново: сети (attach-реплика по Kind) —
/// следующий шаг плана; здесь только локальная логика + стендовые швы.
/// </summary>
public partial class Player : CharacterBody3D
{
    /// <summary>Радиус действия F у шлюза (от центра коробки шлюза).</summary>
    public const float HatchRangeM = 2.2f;

    /// <summary>F у шлюза: вернуть true, если действие выполнено (клавиша
    /// «съедена» шлюзом) и false, если ближайшего шлюза нет.</summary>
    public bool HatchTryInteract()
    {
        if (_mode == EvaMode.Seat)
            return false;

        Airlock? hatch = NearestAirlock();
        if (hatch == null)
            return false;

        bool inside = IsInsideAirlock(hatch);
        GD.Print($"[HATCH] try inside={inside} ext={hatch.ExternalOpen} int={hatch.InternalOpen} mode={_mode}");

        // Сеть (F2.1): двери — источник истины на сервере, локально шлём
        // НАМЕРЕНИЕ + исполняем свою часть (ботинки). Двери поменяются
        // broadcast'ом ApplyDoorState у всех пиров. Оффлайн (стенды, одиночная
        // игра) — прежний прямой путь ниже, без изменений.
        if (NetworkManager.Instance is { HasRealPeer: true })
            return HatchTryInteractNet(hatch, inside);

        // Снаружи у закрытого шлюза — открыть наружную дверь для входа.
        if (!inside && hatch.ExternalOpen == false && hatch.IsSealed)
        {
            hatch.SetExternalOpen(true);
            MissHint("Шлюзование: наружная дверь открыта — влетай внутрь");
            return true;
        }

        // Внутри на джетпаке при открытой наружной — замкнуть цикл наружу,
        // развернуться ботинками к палубе и открыть внутреннюю.
        if (inside && _mode == EvaMode.Jetpack && hatch.ExternalOpen && !hatch.InternalOpen)
        {
            hatch.SetExternalOpen(false);
            if (!TryActivateBoots())
            {
                // Нет опоры (пол шлюза?) — откатываемся, чтобы не запереться.
                hatch.SetExternalOpen(true);
                MissHint("Шлюзование: нет опоры для ботинок — наружная дверь снова открыта");
                return true;
            }
            hatch.SetInternalOpen(true);
            MissHint("Шлюзование завершено: давление выровнено, внутренняя дверь открыта");
            return true;
        }

        // Внутри в ботинках при открытой внутренней — обратный выход в космос.
        if (inside && _mode == EvaMode.Boots && hatch.InternalOpen && !hatch.ExternalOpen)
        {
            hatch.SetInternalOpen(false);
            _mode = EvaMode.Jetpack; // как в ToggleBoots: снял ботинки → джетпак
            DeactivateBoots(keepPosition: true);
            hatch.SetExternalOpen(true);
            MissHint("Шлюзование (наружу): наружная дверь открыта");
            return true;
        }

        // Нечего переключать (например, внутри кабины у закрытой внутренней
        // при закрытой наружной): F НЕ съедаем — пусть работает обычная
        // подсказка панели (контракт Stage7 C4).
        return false;
    }

    /// <summary>Сетевая F-ветка (F2.1): намерение — серверу (он валидирует
    /// правилом `AirlockCycle` + близостью и рассылает двери всем), своя часть
    /// (ботинки) — сразу локально. Двери сами не двигаются — только
    /// broadcast'ом. Откат CycleIn (нет опоры) — намерение CancelToSpace.</summary>
    private bool HatchTryInteractNet(Airlock hatch, bool inside)
    {
        // Снаружи у закрытого шлюза — попросить открыть наружную.
        if (!inside && hatch.ExternalOpen == false && hatch.IsSealed)
        {
            hatch.RequestHatch(HatchAction.OpenExternalFromOutside);
            return true;
        }

        // Внутри на джетпаке при открытой наружной — цикл внутрь. Сервер
        // закрывает наружную и открывает внутреннюю; разворот ботинками —
        // локальный носитель. Не развернулись — просим откат наружу,
        // иначе заперлись бы внутри с выключенным внутренним циклом.
        if (inside && _mode == EvaMode.Jetpack && hatch.ExternalOpen && !hatch.InternalOpen)
        {
            hatch.RequestHatch(HatchAction.CycleIn);
            if (!TryActivateBoots())
            {
                hatch.RequestHatch(HatchAction.CancelToSpace);
                MissHint("Шлюзование: нет опоры для ботинок — наружная дверь снова открыта");
                return true;
            }
            MissHint("Шлюзование завершено: давление выровнено, внутренняя дверь открыта");
            return true;
        }

        // Внутри в ботинках при открытой внутренней — обратный выход в космос.
        if (inside && _mode == EvaMode.Boots && hatch.InternalOpen && !hatch.ExternalOpen)
        {
            hatch.RequestHatch(HatchAction.CycleOut);
            _mode = EvaMode.Jetpack; // как в ToggleBoots: снял ботинки → джетпак
            DeactivateBoots(keepPosition: true);
            MissHint("Шлюзование (наружу): наружная дверь открыта");
            return true;
        }

        return false;
    }

    /// <summary>Ближайший шлюз в радиусе действия (центр коробки ~ y+1 от пола).</summary>
    private Airlock? NearestAirlock()
    {
        Airlock? best = null;
        float bestD = HatchRangeM;
        foreach (Node n in GetTree().GetNodesInGroup(Airlock.GroupName))
        {
            if (n is not Airlock a || !GodotObject.IsInstanceValid(a))
                continue;
            Vector3 center = a.GlobalPosition + Vector3.Up * 1f;
            float d = GlobalPosition.DistanceTo(center);
            if (d < bestD)
            {
                bestD = d;
                best = a;
            }
        }
        return best;
    }

    /// <summary>Тестовый шов (стенд Clip6): внутри ли игрок коробки шлюза.</summary>
    public bool HatchTestInside(Airlock a) => IsInsideAirlock(a);

    /// <summary>Hover-подсказка шлюза для HUD ("" — шлюза рядом нет или для
    /// его двери нечего делать). Та же машина состояний, что у
    /// `HatchTryInteract`: «что подсказано — то и действует».</summary>
    public string HatchHoverHint()
    {
        if (_mode == EvaMode.Seat)
            return "";
        Airlock? hatch = NearestAirlock();
        if (hatch == null)
            return "";
        bool inside = IsInsideAirlock(hatch);
        string key = PanelKeyText();
        if (!inside && !hatch.ExternalOpen && hatch.IsSealed)
            return $"{key} — открыть наружную дверь шлюза ({key})";
        if (inside && _mode == EvaMode.Jetpack && hatch.ExternalOpen && !hatch.InternalOpen)
            return $"{key} — шлюзование: встать на палубу ({key})";
        if (inside && _mode == EvaMode.Boots && hatch.InternalOpen && !hatch.ExternalOpen)
            return $"{key} — шлюзование: выход в космос ({key})";
        return "";
    }

    /// <summary>Внутри ли игрок коробки шлюза (габариты Airlock.tscn:
    /// x/z ±0.6 по локальным осям шлюза, y 0..2.1). Локальные оси — потому
    /// что шлюз повёрнут вместе с кораблём.</summary>
    private bool IsInsideAirlock(Airlock a)
    {
        Vector3 local = a.ToLocal(GlobalPosition);
        return Mathf.Abs(local.X) <= 0.6f && Mathf.Abs(local.Z) <= 0.6f
               && local.Y >= -0.2f && local.Y <= 2.2f;
    }
}
