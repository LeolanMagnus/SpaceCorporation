using Godot;

namespace SpaceCorporation;

/// <summary>
/// Узел-хранилище сетевого состояния станции (Этап 4, шаг 4.2). По образцу
/// <see cref="TargetState"/>: висит дочерним узлом у корня станции
/// (`Station.tscn`), настраивается `MultiplayerSynchronizer` с `RootPath = ".."`,
/// а `StationReplication.tres` адресует свойства от RootPath (`StationState:NetPosition`).
///
/// Станция — СТАТИКА: у неё нет симулируемого состояния, поэтому в конфиге у всех
/// свойств replication_mode = 0 (Never) и spawn = true: пир получает позицию,
/// поворот и адрес ОДИН РАЗ вместе с узлом (спавн-снимок, шаг 4.3). Дельт нет —
/// станция никуда не двигается в этой стадии (Этап 5 добавит HP/таран).
///
/// Адрес — ОБЩИЙ счётчик NetworkManager.NextNetId() (§3.4 п.2: одна нумерация на
/// все спавнящиеся объекты, id Ship/Target/Station не сталкиваются).
///
/// Свойства ОБЯЗАНЫ быть `[Export] public ... { get; set; }` — иначе реплика и
/// спавн-снимок молча не приходят (§3.11 п.2).
/// </summary>
public partial class StationState : Node
{
    /// <summary>Адрес станции из общего счётчика. Реплицируется спавн-снимком:
    /// без него клиент не сопоставит узел с реестром станций (реестр нужен
    /// Этапу 5 для RPC-действий со станцией и проверки OwnerNetId у DockingPoint).</summary>
    [Export] public int StationNetId { get; set; }

    /// <summary>Максимум здоровья. Станцию можно таранить только в Этапе 5, но
    /// поле закладывается сейчас (спавн-снимок, Never), чтобы конфиг станции не
    /// менялся потом.</summary>
    [Export] public int MaxHp { get; set; } = 5000;

    /// <summary>Поза станции в мире. Спавн-снимок (spawn=true, Never): станция
    /// статична, дельты не нужны.</summary>
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public Quaternion NetRotation { get; set; } = Quaternion.Identity;

    /// <summary>Адрес выдаёт сервер ДО AddChild (по образцу
    /// <see cref="TargetState._EnterTree"/>); на клиенте узел создаёт спавнер, и
    /// StationNetId придёт с репликой. Клиенту адрес не выдаём (Role==Client).</summary>
    public override void _EnterTree()
    {
        NetworkManager.Instance?.AssignStationNetIdIfUnset(this);
    }

    public override void _Ready()
    {
        NetworkManager.Instance?.RegisterStation(this);
    }

    public override void _ExitTree()
    {
        NetworkManager.Instance?.UnregisterStation(this);
    }
}
