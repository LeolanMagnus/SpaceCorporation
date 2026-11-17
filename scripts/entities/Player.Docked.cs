using Godot;

namespace SpaceCorporation;

// ─────────────────────────────────────────────────────────────────────────────
// Механика посадки, П9/П10 (docs/landing-mechanic-plan.md): статус
// «пристыкован» — поведение игрока внутри зоны обитаемости площадки, чей
// корабль в фазе DockState.Docked.
//
// Статус = «игрок не в кресле» И «точка игрока внутри зоны обитаемости
// (Station.IsPointInHabitatZone — палуба и зона живут на станции, П8)
// станции с пристыкованным кораблём (площадка-владелец → OwnerStation)».
// Поведение (П9):
//  • внутри зоны игрок ходит как в магнитных ботинках (ботинки включаются
//    автоматически: ActivateBoots + их же анимация доворота BootsTurnTime);
//  • спрыгивание с плоскости корабля и переступание границ плоскостей палубы
//    НЕ переключают в «управление скафандром» (джетпак) — хук в StepBoots;
//  • за край ЗОНЫ ОБИТАЕМОСТИ — статус гаснет, ботинки выключаются, обычный
//    джетпак; обратно в зону — статус и «ботинки» возвращаются;
//  • П10-гейт в HandleEvaKeys: G внутри зоны не выключает ботинки — статуса
//    «магнитные ботинки» в обитаемой зоне нет вообще (правка ТЗ): заход в
//    зону в ЛЮБОМ свободном режиме (джетпак/ботинки/прыжок) даёт ЕДИНЫЙ
//    статус «пристыкован», ботинки внутри него — механизм ходьбы «как в
//    шутере», HUD-индикатор ботинок при статусе скрыт; пилот в кресле
//    статуса не получает.
//
// Позиция игрока в статусе не реплицируется отдельно: поза EVA идёт своим
// каналом (Block D), режим — локальный выбор своего игрока на любой стороне.
// ─────────────────────────────────────────────────────────────────────────────
public partial class Player
{
    private Station? _dockStation;
    private bool _dockStatus;

    /// <summary>Игрок в статусе «пристыкован» (П9/П10).</summary>
    public bool IsDockedStatus => _dockStatus;
    /// <summary>Станция чьей зоны обитаемости статус держит (null = статуса нет).</summary>
    public Station? DockZoneStation => _dockStation;

    /// <summary>Пересчёт статуса каждый физический шаг StepEva (после гейтов
    /// Seat/локальности). Вне кресла и внутри зоны обитаемости станции с
    /// пристыкованным кораблём — статус; если при этом я в джетпаке —
    /// прилипаю ботинками (доворот ногами к палубе идёт их анимацией за
    /// BootsTurnTime, ТЗ П9).</summary>
    private void RefreshDockedStatus()
    {
        Station? st = _mode == EvaMode.Seat ? null : FindHabitatStation();
        if (st == null)
        {
            if (_dockStatus)
                ExitDockedStatus();
            return;
        }
        if (!_dockStatus)
        {
            // Заход в обитаемую зону в ЛЮБОМ свободном режиме (джетпак,
            // ботинки, прыжок) — статус «пристыкован» (правка ТЗ: отдельный
            // статус «магнитные ботинки» в обитаемой зоне НЕ заводится:
            // пристыкованный игрок получает ОДИН статус, а ходьба в ботинках
            // — внутренний механизм шутерного управления, HUD-индикатор
            // ботинок при статусе скрыт).
            EnterDockedStatus(st);
        }

        // «Как в шутере» внутри зоны: джетпак в обитаемой зоне недопустим —
        // пробуем прилипнуть к любой поверхности зоны (палуба/корабль).
        // Нет поверхности в радиусе (прыжок над пустотой) — остаюсь в
        // джетпаке до следующей попытки (или до приземления хуком прыжка).
        if (_mode == EvaMode.Jetpack && ActivateBoots())
            _mode = EvaMode.Boots;
    }

    /// <summary>Станция с пристыкованным кораблём, в чьей зоне обитаемости я
    /// нахожусь (null = ни в какой). Площадка — точка входа: у неё ищем
    /// станцию-владельца (П8: палуба и зона живут на станции).</summary>
    private Station? FindHabitatStation()
    {
        if (!IsInsideTree())
            return null;
        foreach (Node n in GetTree().GetNodesInGroup(LandingPad.GroupName))
        {
            if (n is LandingPad pad && pad.HasDockedShip()
                && pad.OwnerStation is { } st
                && st.IsPointInHabitatZone(GlobalPosition))
                return st;
        }
        return null;
    }

    /// <summary>Всё ещё внутри зоны обитаемости станции статуса? (Хук выхода
    /// за край плоскости в прыжке: пока да — джетпак не включаем.)</summary>
    private bool InsideDockedHabitatZone()
        => _dockStatus && _dockStation != null && GodotObject.IsInstanceValid(_dockStation)
           && _dockStation.IsPointInHabitatZone(GlobalPosition);

    private void EnterDockedStatus(Station station)
    {
        _dockStatus = true;
        _dockStation = station;
        DebugInfoBus.Report("Посадка: обитаемая зона — статус «пристыкован»");
    }

    /// <summary>Потеря статуса (за краем зоны / корабль отстыковали / сел в
    /// кресло): ботинки гаснут, дальше обычный джетпак (П9 п.2).</summary>
    private void ExitDockedStatus()
    {
        _dockStatus = false;
        _dockStation = null;
        if (_mode == EvaMode.Boots)
        {
            DeactivateBoots(keepPosition: true);
            _mode = EvaMode.Jetpack;
            SyncFlightFromBody();
            DebugInfoBus.Report("Посадка: за краем обитаемой зоны — джетпак");
        }
    }
}
