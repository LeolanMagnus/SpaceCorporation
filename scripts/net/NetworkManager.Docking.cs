using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial NetworkManager: вырезано из NetworkManager.cs при partial-декомпозиции.
public partial class NetworkManager : Node
{
    private readonly Dictionary<int, (int PeerId, int StationNetId, DockState State)> _dockRequests = new();

    /// <summary>Счётчик отказов стыковки (для assert'ов теста и debug-панели).</summary>
    public int RejectedDockCount { get; private set; }

    /// <summary>Счётчик ведёт сервер (Ship.AcceptDockFrom зовёт это): инкремент
    /// извне не нарушает авторитет — отказ фиксируется на авторитетной стороне.</summary>
    public void NoteDockRejection() => RejectedDockCount++;

    public int ActiveDockCount => _dockRequests.Count;

    public bool HasDockRequest(int shipNetId) => _dockRequests.ContainsKey(shipNetId);

    public (int PeerId, int StationNetId, DockState State)? FindDockRequest(int shipNetId) =>
        _dockRequests.TryGetValue(shipNetId, out var e) ? e : null;

    /// <summary>Начать сделку (сервер). Возвращает false с причиной — если у
    /// корабля уже есть сделка или станция занята ДРУГИМ кораблём
    /// («одна станция — один захватчик», п.9).</summary>
    public bool BeginDock(int shipNetId, int peerId, int stationNetId, out string reason)
    {
        reason = "";
        if (_dockRequests.TryGetValue(shipNetId, out var cur))
        {
            if (cur.StationNetId == stationNetId)
            {
                reason = "duplicate"; // повторный запрос на ту же станцию — не ошибка повторного входа
                return false;
            }
            reason = "ship-busy";
            RejectedDockCount++;
            GD.Print($"Dock: ship#{shipNetId} уже стыкуется к ст#{cur.StationNetId} — " +
                     $"повторная сделка отклонена ({reason})");
            return false;
        }
        var st = FindStation(stationNetId);
        if (st == null)
        {
            reason = "no-station";
            RejectedDockCount++;
            return false;
        }
        if (st.DockShipNetId != 0 && st.DockShipNetId != shipNetId)
        {
            reason = "station-busy";
            RejectedDockCount++;
            GD.Print($"Dock: ст#{stationNetId} уже занята ship#{st.DockShipNetId} — " +
                     $"ship#{shipNetId} отказ ({reason})");
            return false;
        }
        _dockRequests[shipNetId] = (peerId, stationNetId, DockState.Requested);
        DebugInfoBus.Report($"dock: begin ship#{shipNetId} peer {peerId} -> st#{stationNetId}");
        return true;
    }

    /// <summary>Сменить фазу сделки (единственный писатель обеих сторон — В20 §3.4):
    /// пишет и в NetState корабля, и в StationState станции. Возвращает false,
    /// если сделки/узла нет — молча плодить рассогласование нельзя.</summary>
    public bool SetDockPhase(int shipNetId, DockState phase)
    {
        if (!_dockRequests.TryGetValue(shipNetId, out var e))
        {
            GD.PushError($"Dock: SetDockPhase({phase}) для ship#{shipNetId} — сделки нет");
            return false;
        }
        var ship = FindShip(shipNetId);
        var st = FindStation(e.StationNetId);
        if (ship?.Net == null || st == null)
        {
            GD.PushError($"Dock: SetDockPhase({phase}) — нет узла корабля или станции " +
                         $"(ship#{shipNetId}, st#{e.StationNetId})");
            return false;
        }
        _dockRequests[shipNetId] = (e.PeerId, e.StationNetId, phase);
        ship.Net.DockStateValue = (byte)phase;
        ship.Net.DockStationNetId = phase == DockState.None ? 0 : e.StationNetId;
        if (st.GetParent() is Station stationNode)
            stationNode.SetDockStateServerOnly(phase, shipNetId);
        else
        {
            // Станция без обёртки Station (теоретически) — пишем поля напрямую.
            st.DockStateValue = (byte)phase;
            st.DockShipNetId = phase == DockState.None ? 0 : shipNetId;
            st.DockApproved = phase >= DockState.Approved && phase < DockState.Docked;
        }
        DebugInfoBus.Report($"dock: ship#{shipNetId} фаза -> {phase.ToString().ToLower()}");
        DebugInfoBus.Report($"dock: ship#{shipNetId} фаза -> {phase.ToString().ToLower()}");
        // H6 (hud-plan): событие «пристыкован» — в комм-лог HUD (один вызов на
        // событие). Пока событие локальное (хост/сервер); у чистого клиента
        // комм-лог об этом не узнает — RPC-рассылка событий будет позже.
        if (phase == DockState.Docked)
            HudEventBus.Report($"Docked at Station #{e.StationNetId} — 0 m");
        return true;
    }

    /// <summary>Отменить сделку (п.8; вызывается и при OnPeerLeft — шаг 5.11):
    /// обнуляет ОБЕ стороны и снимает реестр. Повторный AbortDock безопасен (no-op).</summary>
    public void AbortDock(int shipNetId, string why)
    {
        if (!_dockRequests.TryGetValue(shipNetId, out var e))
            return; // уже снято (AbortDock идемпотентен — повтор после OnPeerLeft)
        _dockRequests.Remove(shipNetId);
        var ship = FindShip(shipNetId);
        if (ship?.Net != null)
        {
            ship.Net.DockStateValue = (byte)DockState.None;
            ship.Net.DockStationNetId = 0;
        }
        var st = FindStation(e.StationNetId);
        if (st?.GetParent() is Station stationNode)
            stationNode.SetDockStateServerOnly(DockState.None, 0);
        else if (st != null)
        {
            st.DockStateValue = (byte)DockState.None;
            st.DockShipNetId = 0;
            st.DockApproved = false;
        }
        DebugInfoBus.Report($"dock: abort ship#{shipNetId} ({why})");
    }

    // ------------------------------------------- пилот кораблей (§4 В3 п.3, В14=B)
    //
    // С шага 2.3 сервер назначает только ПИЛОТА — того, кто шлёт входные кадры
    // этого корабля. Физический авторитет корабля навсегда остаётся за сервером
    // (id = 1): если отдать его клиенту, клиент интегрирует физику у себя, а
    // сервер примет его позицию как факт (§3.5 п.8, В14=B).

}
