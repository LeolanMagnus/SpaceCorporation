using Godot;

namespace SpaceCorporation;

/// <summary>
/// Точка, к которой может быть пристёгнут игрок (Player.AttachTo).
///
/// Закладка под этап 6: игрок открепляется от корабля и прикрепляется к станции.
/// Для этого у корабля есть точка кабины (Seat), у станции позже появятся точки
/// пола (Floor) и шлюза (Hatch). Все они состоят в группе AttachPoint.GroupName,
/// поэтому код игрока ищет их по группе, а не по жёстким путям в сцене.
///
/// Сеть (§3.4): через RPC узел передать нельзя — путь `Ship/AttachPoints/Seat`
/// расходится между процессами в тот момент, когда кто-то сделал `Reparent`.
/// Поэтому адрес точки — пара целых `(OwnerNetId, NetId)`, а не `NodePath`:
///   - `NetId` — стабильный номер точки внутри владельца (в сцене, `[Export]`);
///   - `OwnerNetId` — сетевой адрес владельца (`ShipNetId` / `StationNetId`),
///     он же ключ реестра; берётся из `NetState` ближайшего предка-владельца.
/// Реестр живёт на autoload `NetworkManager`: он переживает смену сцены, а
/// `AttachPoint` — нет.
/// </summary>
public partial class AttachPoint : Node3D
{
    public enum KindOf
    {
        /// <summary>Кабина корабля / кресло.</summary>
        Seat,
        /// <summary>Пол станции или палубы (магнитные ботинки, этап 6).</summary>
        Floor,
        /// <summary>Шлюз: переход между кораблём и станцией.</summary>
        Hatch,
    }

    /// <summary>Группа всех точек крепления в сцене.</summary>
    public const string GroupName = "attach_point";

    [Export] public KindOf Kind { get; set; } = KindOf.Seat;

    /// <summary>К этой точке игрок пристёгнут при старте сцены.</summary>
    [Export] public bool IsDefaultForPlayer { get; set; }

    /// <summary>
    /// Смещение капсулы игрока относительно этой точки (в локальных осях точки).
    /// Для Kind.Seat — посадить капсулу в кресло; для Kind.Floor — поднять на рост
    /// над палубой, чтобы капсула не проваливалась в пол.
    /// </summary>
    [Export] public Vector3 PlayerOffset { get; set; } = Vector3.Zero;

    /// <summary>Уникальный в пределах владельца номер точки (§3.4 п.1). У кабины
    /// корабля в Ship.tscn — 0. Проставляется в сцене, по сети не передаётся:
    /// сцена одинакова у всех процессов.</summary>
    [Export] public int NetId { get; set; }

    /// <summary>Сетевой адрес владельца (ShipNetId/StationNetId). 0 = владелец ещё
    /// не получил адрес (сервер обязан выдать его до AddChild, §4 В4 п.3).</summary>
    public int OwnerNetId { get; private set; }

    public override void _Ready()
    {
        AddToGroup(GroupName);
        OwnerNetId = ResolveOwnerNetId();
        NetworkManager.Instance?.RegisterAttachPoint(this);
    }

    public override void _ExitTree()
    {
        if (NetworkManager.Instance != null)
            NetworkManager.Instance.UnregisterAttachPoint(this);
    }

    /// <summary>Владелец — ближайший предок с `NetState` (Ship, позже Station).
    /// Точки крепления висят под `ShipHull` (§3.3), поэтому искать надо вверх по
    /// дереву, а не читать `GetParent()`.
    /// Возвращает: >0 — адрес владельца; 0 — владелец есть, но адрес ещё не
    /// выдан (в сети сервер обязан сделать это до `AddChild`, §4 В4 п.3);
    /// -1 — сетевого владельца нет вовсе (TestStand стадии 1: стенд не спавнится
    /// и не реплицируется, в реестре точка не участвует).</summary>
    private int ResolveOwnerNetId()
    {
        for (Node n = GetParent(); n != null; n = n.GetParent())
        {
            var net = n.GetNodeOrNull<NetState>("NetState");
            if (net != null)
                return net.ShipNetId;
        }
        return -1;
    }
}
