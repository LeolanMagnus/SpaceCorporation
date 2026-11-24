using Godot;

namespace SpaceCorporation;

/// <summary>
/// Типовой шлюз: герметичная коробка 1×1 м, высота 2 м, двери на противоположных
/// стенах. Наружная дверь (External) смотрит в космос (локальный +Z), внутренняя
/// (Internal) — в обитаемый объём (локальный −Z). Все стены и двери — на слое
/// `Deck` (256): в них упирается капсула игрока в ботинках, поэтому «выйти из
/// кабины можно только через шлюз» работает как физический гейт, а не как
/// скриптовый запрет.
///
/// Двери — `StaticBody3D` с формой-заглушкой проёма: закрыта ⇒ коллизия
/// включена, открыта ⇒ коллизия выключена и полотно сдвигается в сторону
/// (только визуал; в живом коде — анимация). Состояние дверей — единственная
/// «логика» шлюза на этом шаге; циклом шлюзования (внешняя закрыта → разворот
/// ногами к палубе → внутренняя открыта) управляет игрок (Player.HatchCycle).
/// </summary>
public partial class Airlock : Node3D
{
    /// <summary>Группа шлюзов сцены (их ищет F-подсказка/ХатчTryInteract).</summary>
    public const string GroupName = "airlock";

    /// <summary>Полотно наружной двери (в космос, +Z).</summary>
    [Export] public NodePath ExternalDoorPath { get; set; } = "DoorExternal";
    /// <summary>Полотно внутренней двери (в кабину, −Z).</summary>
    [Export] public NodePath InternalDoorPath { get; set; } = "DoorInternal";

    public StaticBody3D ExternalDoor { get; private set; }
    public StaticBody3D InternalDoor { get; private set; }

    public bool ExternalOpen { get; private set; }
    public bool InternalOpen { get; private set; }

    /// <summary>Направление сдвига полотна при открытии (в локальных осях двери,
    /// нормировано). Только визуал: дыра в стене становится видна.</summary>
    public override void _Ready()
    {
        AddToGroup(GroupName);
        ExternalDoor = GetNodeOrNull<StaticBody3D>(ExternalDoorPath);
        InternalDoor = GetNodeOrNull<StaticBody3D>(InternalDoorPath);
        // Стартовое состояние: обе двери закрыты — шлюз герметичен.
        ExternalOpen = false;
        InternalOpen = false;
        SetDoor(ExternalDoor, false);
        SetDoor(InternalDoor, false);
    }

    public void SetExternalOpen(bool open)
    {
        ExternalOpen = open;
        SetDoor(ExternalDoor, open);
    }

    public void SetInternalOpen(bool open)
    {
        InternalOpen = open;
        SetDoor(InternalDoor, open);
    }

    /// <summary>Полностью герметичен ли шлюз (обе двери закрыты) — условие, при
    /// котором разрешён цикл шлюзования.</summary>
    public bool IsSealed => !ExternalOpen && !InternalOpen;

    /// <summary>Внутри ли точка мировой позиции коробки шлюза (габарит
    /// Airlock.tscn: x/z ±0.6 по локальным осям, y 0..2.2). Общая для
    /// локального игрока и серверной валидации (F2.1).</summary>
    public bool IsInsideWorld(Vector3 worldPos)
    {
        Vector3 local = ToLocal(worldPos);
        return Mathf.Abs(local.X) <= 0.6f && Mathf.Abs(local.Z) <= 0.6f
               && local.Y >= -0.2f && local.Y <= 2.2f;
    }

    private static void SetDoor(StaticBody3D door, bool open)
    {
        if (door == null) return;
        var shape = door.GetNodeOrNull<CollisionShape3D>("Shape");
        if (shape != null) shape.Disabled = open;
        if (door is Door d) d.SetSlideOpen(open);
    }

    // ==================================================================
    // Сеть (docs/habitable-deck-phase2-plan.md, F2.1)
    // ==================================================================
    // Источник истины о дверях — сервер (как пилот/док-фазы): клиент шлёт
    // НАМЕРЕНИЕ (HatchAction), сервер валидирует его честной функцией
    // `AirlockCycle` на СВОЁМ состоянии + близостью игрока по серверной позе,
    // и атомарно рассылает итоговую пару дверей `ApplyDoorState` всем пирам.
    // RPC идут по пути узла (тот же трюк, что docking-фазы: копий Airlock в
    // одинаковом пути у всех пиров, т.к. имя корабля — из спавн-снимка).
    // Оффлайн/сервер: прямой вызов тех же методов, без RPC (стенды не меняются).

    /// <summary>Клиент: отправить серверу намерение у шлюза. Офлайн/сервер —
    /// исполнить то же правило локально (гейт по роли, как SubmitDockToServer:
    /// в offline IsServer == false, поэтому проверка именно `IsClient`).</summary>
    public void RequestHatch(HatchAction action)
    {
        var nm = NetworkManager.Instance;
        if (nm is not { IsClient: true })
        {
            AcceptHatch(nm?.SelfId ?? 1, action);
            return;
        }
        RpcId(1, nameof(RpcHatchRequest), (int)action);
    }

    /// <summary>Серверный приём намерения (гейт F2.1: только авторитетная
    /// сторона; валидация правила и близости — в AcceptHatch).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcHatchRequest(int action)
        => AcceptHatch(Multiplayer.GetRemoteSenderId(), (HatchAction)action);

    /// <summary>Серверная приёмка: правило `AirlockCycle` на серверном
    /// состоянии + близость игрока-отправителя по серверной копии его позы.
    /// Отказ — тихо (у клиента двери предсказываются только broadcast'ом,
    /// рассинхрона нет); режим игрока при CycleIn/Out на сервере может быть
    /// просроченным (EvaState канал 2) — просрочка это ровно ПРЕДЫДУЩИЙ режим,
    /// который переход и требует, так что гейт от этого только честнее.</summary>
    private void AcceptHatch(int senderPeer, HatchAction action)
    {
        var nm = NetworkManager.Instance;
        if (nm is not { IsAuthoritySide: true })
            return; // не авторитет — состояние дверей здесь не рождается

        // Близость: игрок отправителя (по его копии на сервере) рядом со шлюзом.
        // Игрок-отправитель — тот, чей multiplayer-authority равен peer'у
        // отправителя (WorldSpawner.Spawn: SetMultiplayerAuthority(peerId)).
        Player pl = null;
        foreach (Node n in GetTree().GetNodesInGroup("player"))
        {
            if (n is Player p && GodotObject.IsInstanceValid(p)
                && p.GetMultiplayerAuthority() == senderPeer)
            { pl = p; break; }
        }
        if (pl == null
            || (!IsInsideWorld(pl.GlobalPosition)
                && (pl.GlobalPosition - (GlobalPosition + Vector3.Up)).Length()
                    > Player.HatchRangeM * 1.5f))
        {
            GD.PrintErr($"airlock@{GetPath()}: намерение {action} от peer={senderPeer} " +
                        "без близости — отказ (F2.1)");
            return;
        }

        // Режим отправителя: у копии его игрока `_mode` жив только у владельца
        // копии (Player.Eva.Net: позу и режим шлёт только `_isLocal`-владелец),
        // чужие копии кормятся репликой `_remoteEvaMode`.
        bool senderBoots = pl.GetMultiplayerAuthority() == nm.SelfId
            ? pl.Mode == EvaMode.Boots // своя копия на сервере/в офлайне: живой режим
            : pl.EvaRemoteMode() == EvaMode.Boots; // чужой — последняя реплика режима
        var s = new HatchState
        {
            ExternalOpen = ExternalOpen, InternalOpen = InternalOpen,
            Inside = IsInsideWorld(pl.GlobalPosition), OnBoots = senderBoots,
        };
        var d = AirlockCycle.Evaluate(s, action);
        if (!d.Allowed)
        {
            GD.Print($"airlock@{GetPath()}: намерение {action} отклонено ({d.Reason})");
            return;
        }

        ApplyDoors(d.ExternalOpen, d.InternalOpen);
        if (nm.HasRealPeer)
            Rpc(nameof(RpcApplyDoorState), d.ExternalOpen, d.InternalOpen);
    }

    /// <summary>Рассылка сервером итоговой пары дверей всем пирам (и себе:
    /// CallLocal). Событийная — трафик на покое не растёт (надёжный канал 1).</summary>
    [Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    private void RpcApplyDoorState(bool externalOpen, bool internalOpen)
    {
        // На пирах принимает только не-авторитетная сторона; гейт отправителя:
        // состояние дверей имеет право объявлять только сервер (peer 1).
        if (NetworkManager.Instance is { IsAuthoritySide: true })
            return;
        if (Multiplayer.GetRemoteSenderId() != 1)
            return;
        ApplyDoors(externalOpen, internalOpen);
    }

    /// <summary>Атомарное применение пары дверей (минует поштучные сеттеры —
    /// переход приходит целиком из правила, инвариант «обе открыты недостижимы»
    /// не нарушается и на пирах).</summary>
    private void ApplyDoors(bool externalOpen, bool internalOpen)
    {
        ExternalOpen = externalOpen;
        InternalOpen = internalOpen;
        SetDoor(ExternalDoor, externalOpen);
        SetDoor(InternalDoor, internalOpen);
    }
}
