using Godot;

namespace SpaceCorporation.Tests;

// ────────────────────────────────────────────────────────────────────────────
// Блок D (undock-plan, docs/undock-plan.md §4): отстыковка Docked → None.
// U1: штатная отстыковка — фазы в None, замок снят, станция освобождена,
//     реестр пуст, позиция вне collision-цилиндра, скорость тихая;
// U2: прислал не пилот — отказ;
// U3: не Docked — отзывать нечего (промежуточные фазы — путь AbortDock);
// U4: после отстыковки контакт-гейт 5.10 не даёт урона «изнутри» станции;
// U5: повторная отстыковка идемпотентна (нет второго выталкивания);
// U6: «сиротский Docked» (реестра нет, состояние осталось) — отстыкуется.
// Стенд Docked поднят вручную (как п.31), без пружины: проверяется сам
// сброс-контракт, а не доводка (пружина покрыта п.25/п.26).
// ────────────────────────────────────────────────────────────────────────────
public partial class Stage5Test
{
    // Где корабль стоял «на причале» в этом блоке (для замера выталкивания).
    private Vector3 _dockPos;

    /// <summary>Поднять стенд Docked вручную: реестр + обе стороны фазы +
    /// корпус у палубы. Возвращает false, если арены нет.</summary>
    private bool BuildDockedStand()
    {
        var nm = NetworkManager.Instance!;
        if (_shipA == null || _station == null) return false;
        int shipId = _shipA.Net!.ShipNetId, stId = _station.State!.StationNetId;
        nm.AbortDock(shipId, "test-pre-undock"); // чистый старт блока
        _station.SetDockStateServerOnly(DockState.None, 0);

        nm.BeginDock(shipId, 1, stId, out _);
        nm.SetDockPhase(shipId, DockState.Docked);
        _station.SetDockStateServerOnly(DockState.Docked, shipId);
        _station.State!.DockApproved = false;
        _shipA.Net.DockStationNetId = stId;
        _shipA.Freeze = true; // как на авторитете через ApplyAuthority (U-стенд — сервер)
        _dockPos = _station.LandingPad != null
            ? _station.LandingPad.DeckSurfaceGlobal
            : _station.DockPointGlobal;
        // Корпус «на палубе»: центр корпуса на высоте палубы + половина высоты —
        // ровно как Docked его оставила (внутри цилиндра r=60 станции).
        _shipA.GlobalPosition = _dockPos + new Vector3(0, _shipA.HullHeightForTest(), 0);
        _shipA.TestSetVelocity(Vector3.Zero);
        return true;
    }

    // ─────────────────────── U1, U4: штатная отстыковка ───────────────────────

    private void CheckUndockHappyPath()
    {
        var nm = NetworkManager.Instance!;
        if (!BuildDockedStand()) return;
        int shipId = _shipA.Net!.ShipNetId, stId = _station.State!.StationNetId;
        _shipA.TestSetPilotForTest(1);

        int dmgBefore = _shipA.Health!.DamageTakenCount;
        Vector3 before = _shipA.GlobalPosition;

        bool done = _shipA.TestUndockForTest(1); // боевой гейт (sender = пилот=хост)

        Check(done, "U1: TestUndockForTest(пилот) вернул true (фаза снята)");
        Check((DockState)_shipA.Net.DockStateValue == DockState.None &&
              _shipA.Net.DockStationNetId == 0,
              "U1: у корабля DockState -> None, DockStationNetId -> 0");
        Check(!_station.State!.DockApproved &&
              (DockState)_station.State!.DockStateValue == DockState.None &&
              _station.State!.DockShipNetId == 0,
              "U1: станция освобождена: dock None, docker 0, Approved false");
        Check(nm.ActiveDockCount == 0,
              $"U1: реестр сделок пуст после отстыковки (активных {nm.ActiveDockCount})");
        Check(!_shipA.Freeze,
              "U1: ApplyAuthority внутри UndockNow снял dock-замок (Freeze false)");

        // Анти-таран (§2.3): вытолкнут вверх, скорость тихая.
        float lift = _shipA.GlobalPosition.Y - before.Y;
        Check(lift > 0.5f * Docking.UndockPushOffM,
              $"U1: корпус вытолкнут вдоль нормали палубы (подъём {lift:F2} м >= {Docking.UndockPushOffM / 2:F2})");
        float v = _shipA.TestGetVelocity().Length();
        Check(v <= Docking.UndockPushSpeed + 0.01f && v > 0f,
              $"U1: скорость = выталкивание ({v:F2} м/с <= {Docking.UndockPushSpeed})");

        // U4: контакт-гейт больше НЕ прикрывает цель (фаза None) — если бы
        // корпус остался в цилиндре, урон бы потёк. После выталкивания — не течёт.
        for (int i = 0; i < 30; i++)
            _shipA.TestStepForTest(FlightModel.FixedDt);
        Check(_shipA.Health!.DamageTakenCount == dmgBefore,
              "U4: после отстыковки контактов с бывшей станцией нет (урон не капает)");

        _shipA.Freeze = false;
    }

    // ─────────────── U2, U3, U5, U6: гейты и «сиротский Docked» ───────────────

    private void CheckUndockGates()
    {
        var nm = NetworkManager.Instance!;
        if (!BuildDockedStand()) return;
        int shipId = _shipA.Net!.ShipNetId;
        _shipA.TestSetPilotForTest(1);

        // U2: чужой пир (999 ≠ PilotPeerId=1) — отказ, всё на месте.
        bool bad = _shipA.TestUndockForTest(999);
        Check(!bad && (DockState)_shipA.Net.DockStateValue == DockState.Docked,
              "U2: отстыковку прислал не пилот — отказ (В17), фаза цела");

        // U3: не Docked — отзывать нечего. Ставим Capturing: путь отмены
        // промежуточных фаз — AbortDock, Undock-гейт обязан отказать.
        nm.SetDockPhase(shipId, DockState.Capturing);
        bool mid = _shipA.TestUndockForTest(1);
        Check(!mid && (DockState)_shipA.Net.DockStateValue == DockState.Capturing,
              "U3: не-Docked (Capturing) — Undock отказал (отмена = AbortDock, не он)");

        // U6: «сиротский Docked» — запись из реестра уже снята (имитация
        // OnPeerLeft), состояние осталось. Новый пилот (peer 2) проходит гейт.
        nm.AbortDock(shipId, "test-u6-noop"); // реестра уже нет (фаза Capturing снимается)
        _shipA.Net.DockStateValue = (byte)DockState.Docked;
        _shipA.Net.DockStationNetId = _station.State!.StationNetId;
        _shipA.Freeze = true;
        _station.SetDockStateServerOnly(DockState.Docked, shipId);
        Check(!nm.HasDockRequest(shipId), "U6: стенд сироты: записи в реестре нет");
        _shipA.TestSetPilotForTest(2); // новый пилот сел в кресло
        bool orphan = _shipA.TestUndockForTest(2);
        Check(orphan && (DockState)_shipA.Net.DockStateValue == DockState.None
                    && !_shipA.Freeze,
              "U6: «сиротский Docked» отстыкован новым пилотом (фаза None, замок снят)");
        Check((DockState)_station.State!.DockStateValue == DockState.None &&
              _station.State!.DockShipNetId == 0,
              "U6: сирота освободил станцию (dock None, docker 0)");

        // U5: повторная отстыковка — no-op, второго выталкивания нет.
        // Скорость выталкивания от U6 обнуляем: TestStepForTest(0) всё равно
        // делает фиксированный шаг и сдвинул бы тело на v*fixed_dt.
        _shipA.TestSetVelocity(Vector3.Zero);
        _shipA.TestStepForTest(0f);
        Vector3 posAfter = _shipA.GlobalPosition;
        bool again = _shipA.TestUndockForTest(2);
        Check(!again && _shipA.GlobalPosition == posAfter,
              "U5: повторный Undock — no-op (фаза уже None, позиция не тронута)");

        // Возврат сцены в базу.
        _shipA.TestSetPilotForTest(1);
        _shipA.TestSetVelocity(Vector3.Zero);
        _shipA.Freeze = false;
        _station.SetDockStateServerOnly(DockState.None, 0);
    }
}
