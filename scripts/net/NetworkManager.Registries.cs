using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial NetworkManager: вырезано из NetworkManager.cs при partial-декомпозиции.
public partial class NetworkManager : Node
{
    /// <summary>Монотонный счётчик сетевых адресов спавнящихся объектов мира
    /// (ShipNetId / StationNetId — одна нумерация на все объекты, §3.4 п.2).</summary>
    private int _netIdCounter;
    public int NextNetId() => ++_netIdCounter;

    /// <summary>Offline: выдать `NetState` адрес, если он ещё не назначен. В сети
    /// адрес назначает сервер до `AddChild`, и этот метод для сетевого узла ничего
    /// не делает (роль уже не Offline). Повторный вход узла в дерево адрес не
    /// меняет — адрес привязан к объекту, а не к дереву (§4 В4 п.3).</summary>
    public void AssignNetIdIfUnset(NetState state)
    {
        // Адрес выдаёт авторитет: сервер (Dedicated/Host) или offline. Клиент
        // спавнит корабль через MultiplayerSpawner и адрес получает с репликой,
        // поэтому на клиенте метод не вызывается вовсе (спавнер не работает).
        // §4 В4 п.3: сервер обязан выдать адрес ДО AddChild — иначе первый снимок
        // уйдёт с ShipNetId = 0 и RPC корабля потеряются.
        if (Role == NetRole.Client || state.ShipNetId != 0)
            return;
        state.ShipNetId = NextNetId();
    }

    /// <summary>Реестр точек крепления: (ownerNetId, pointNetId) → AttachPoint (§3.4).</summary>
    private readonly Dictionary<(int Owner, int Point), AttachPoint> _attachPoints = new();

    public void RegisterAttachPoint(AttachPoint point)
    {
        // Точка без сетевого владельца (TestStand стадии 1) в реестре не участвует:
        // у неё нет адреса, по которому её можно запросить из сети (§3.4 п.2).
        if (point.OwnerNetId < 0)
            return;

        var key = (point.OwnerNetId, point.NetId);
        if (_attachPoints.TryGetValue(key, out var existing) && existing != point)
        {
            // Коллизия ключа — громкая ошибка, а не перезапись: перезапись означает,
            // что игрок пристегнётся к чужому кораблю (§3.4 п.2).
            GD.PushError($"NetworkManager: коллизия ключа точки крепления {key} " +
                         $"({existing.GetPath()} уже занимает её)");
            return;
        }
        _attachPoints[key] = point;

        // §2.4 п.4: игрок, вернувшийся к ещё не заспавненному кораблю, был отложен
        // в _pendingAttach. Как только точка появилась в реестре — крепим его.
        FlushPendingAttach(point.OwnerNetId, point.NetId);
    }

    public void UnregisterAttachPoint(AttachPoint point)
    {
        var key = (point.OwnerNetId, point.NetId);
        if (_attachPoints.TryGetValue(key, out var cur) && cur == point)
            _attachPoints.Remove(key);
    }

    public AttachPoint? FindAttachPoint(int ownerNetId, int pointNetId) =>
        _attachPoints.TryGetValue((ownerNetId, pointNetId), out var p) &&
        GodotObject.IsInstanceValid(p) ? p : null;

    public IReadOnlyDictionary<(int Owner, int Point), AttachPoint> AttachPoints => _attachPoints;

    // ------------------------------------------------- реестр кораблей (§4 В4 п.3)

    /// <summary>Реестр кораблей: ShipNetId → узел. Все RPC про корабль
    /// (назначение пилота) адресуются по ShipNetId, а не по NodePath: пути
    /// спавненых узлов расходятся между пирами (§4 В4 п.3).</summary>
    private readonly Dictionary<int, Ship> _ships = new();

    public void RegisterShip(Ship ship)
    {
        int id = ship.Net?.ShipNetId ?? 0;
        if (id == 0)
            return; // без адреса в реестре участвовать нечем (ошибка уже напечатана в Ship)
        if (_ships.TryGetValue(id, out var existing) && existing != ship)
        {
            GD.PushError($"NetworkManager: коллизия ShipNetId {id} " +
                         $"({existing.GetPath()} уже занимает его)");
            return;
        }
        _ships[id] = ship;
    }

    public void UnregisterShip(Ship ship)
    {
        int id = ship.Net?.ShipNetId ?? 0;
        if (_ships.TryGetValue(id, out var cur) && cur == ship)
            _ships.Remove(id);
    }

    public Ship? FindShip(int shipNetId) =>
        _ships.TryGetValue(shipNetId, out var s) && GodotObject.IsInstanceValid(s) ? s : null;

    /// <summary>Сколько кораблей считают пилотом этого пира (по PilotPeerId).
    /// Инвариант §3.9 п.17: пир не может пилотировать два корабля сразу —
    /// инвариант проверяется и как рантайм-гейт в SetPilot, и assert'ом
    /// автотеста.</summary>
    public int CountShipsPilotedBy(int peerId)
    {
        int n = 0;
        foreach (var s in _ships.Values)
            if (GodotObject.IsInstanceValid(s) && s.PilotPeerId == peerId)
                n++;
        return n;
    }

    public IReadOnlyDictionary<int, Ship> Ships => _ships;

    // --------------------------------------------- реестр мишеней (этап 3, §3.4)
    //
    // Реестр по АДРЕСУ (TargetNetId), а не по пути узла: у спавн-сцены путь на
    // разных пирах разный (MultiplayerSpawner генерирует имена), а адрес один —
    // поэтому RPC урона (шаг 3.6) ищут цель здесь, а не через GetNode(path).
    // Адрес мишени берётся из ТОГО ЖЕ монотонного счётчика, что ShipNetId
    // (§3.4 п.2: одна нумерация на все спавнящиеся объекты, id не сталкиваются).

    private readonly Dictionary<int, TargetState> _targets = new();

    /// <summary>Выдать мишени адрес из общего счётчика, если ещё не назначен.
    /// По образцу <see cref="AssignNetIdIfUnset"/>: на клиенте (Role==Client) не
    /// делаем — адрес придёт с репликой; повторный вход адрес не меняет.</summary>
    public void AssignTargetNetIdIfUnset(TargetState state)
    {
        if (Role == NetRole.Client || state.TargetNetId != 0)
            return;
        state.TargetNetId = NextNetId();
    }

    public void RegisterTarget(TargetState target)
    {
        int id = target.TargetNetId;
        if (id == 0)
            return; // без адреса в реестре участвовать нечем (ошибка на печати адреса)
        if (_targets.TryGetValue(id, out var existing) && existing != target)
        {
            GD.PushError($"NetworkManager: коллизия TargetNetId {id} " +
                         $"({existing.GetPath()} уже занимает его)");
            return;
        }
        _targets[id] = target;
    }

    public void UnregisterTarget(TargetState target)
    {
        int id = target.TargetNetId;
        if (_targets.TryGetValue(id, out var cur) && cur == target)
            _targets.Remove(id);
    }

    public TargetState? FindTarget(int targetNetId) =>
        _targets.TryGetValue(targetNetId, out var t) && GodotObject.IsInstanceValid(t) ? t : null;

    public IReadOnlyDictionary<int, TargetState> Targets => _targets;

    // ------------------------------------------- реестр станций (этап 4, шаг 4.2)
    //
    // Ровно по образцу реестра мишеней выше: ключ — адрес из общего счётчика
    // (StationNetId), а не путь узла. Станция спавнится сервером при старте мира
    // (шаг 4.3) и приходит пирам спавн-снимком; в этой стадии реестр нужен для
    // OwnerNetId точки DockingPoint и assert'ов Stage4Test, в Этапе 5 по нему
    // пойдут RPC-действия со станцией.

    private readonly Dictionary<int, StationState> _stations = new();

    /// <summary>Выдать станции адрес из общего счётчика, если ещё не назначен.
    /// Гейт `Role == Client` — как у <see cref="AssignTargetNetIdIfUnset"/>:
    /// клиенту адрес придёт с репликой спавна.</summary>
    public void AssignStationNetIdIfUnset(StationState state)
    {
        if (Role == NetRole.Client || state.StationNetId != 0)
            return;
        state.StationNetId = NextNetId();
    }

    public void RegisterStation(StationState station)
    {
        int id = station.StationNetId;
        if (id == 0)
            return; // без адреса в реестре участвовать нечем
        if (_stations.TryGetValue(id, out var existing) && existing != station)
        {
            GD.PushError($"NetworkManager: коллизия StationNetId {id} " +
                         $"({existing.GetPath()} уже занимает его)");
            return;
        }
        _stations[id] = station;
    }

    public void UnregisterStation(StationState station)
    {
        int id = station.StationNetId;
        if (_stations.TryGetValue(id, out var cur) && cur == station)
            _stations.Remove(id);
    }

    public StationState? FindStation(int stationNetId) =>
        _stations.TryGetValue(stationNetId, out var s) && GodotObject.IsInstanceValid(s) ? s : null;

    /// <summary>Узел станции (Station) по адресу — для геометрии точки стыковки
    /// (шаг 5.6). Это родитель StationState в сцене; отдельного реестра узлов нет.</summary>
    public Station? FindStationNode(int stationNetId) =>
        FindStation(stationNetId)?.GetParent() as Station;

    public IReadOnlyDictionary<int, StationState> Stations => _stations;

    // ─────────────────────── стыковка (Этап 5, шаг 5.4) ───────────────────────
    // Единственная карта «активные стыковки» живёт на autoload — переживает
    // смену сцены, как реестры attach-точек (§3.4 п.1). Писать в неё и в
    // StationState/NetState обязан только сервер (он же авторитет кораблей,
    // В14=B). Инвариант двух сторон:
    //   NetState.DockStationNetId == stationNetId  ↔  StationState.DockShipNetId == shipNetId
    // оба обнуляются в AbortDock.

}
