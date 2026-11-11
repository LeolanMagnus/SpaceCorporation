using Godot;

namespace SpaceCorporation;

// partial Player: вырезано из Player.cs при partial-декомпозиции.
public partial class Player : CharacterBody3D
{
    /// <summary>Пристегнуться к точке с [Export] IsDefaultForPlayer = true.
    /// При нескольких кораблях таких точек несколько (Seat каждого корабля),
    /// поэтому выбирается ближайшая к игроку, а не первая из группы (§4 В4).</summary>
    public bool AttachToDefaultPoint()
    {
        AttachPoint? best = null;
        float bestDist = float.MaxValue;
        foreach (var node in GetTree().GetNodesInGroup(AttachPoint.GroupName))
        {
            if (node is AttachPoint point && point.IsDefaultForPlayer &&
                GodotObject.IsInstanceValid(point))
            {
                float d = GlobalPosition.DistanceSquaredTo(point.GlobalPosition);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = point;
                }
            }
        }
        if (best == null)
            return false;

        // BUG-008 (рецидив, часть 2): у точки с сетевым адресом (реальный корабль
        // из WorldSpawner) идём ПОЛНЫМ серверным путём AttachServerSide, а не чистым
        // AttachTo. Причина: вход в кабину = назначение пилота (§3.5 п.8, В14=B), и
        // только AttachServerSide зовёт nm.SetPilot. Чистый AttachTo меняет дерево,
        // но PilotPeerId не трогает, поэтому после спавна (где корабли уже с
        // пилотом 0) пристёгнутый игрок остался бы без управления, а чужие
        // корабли — с прежним пилотом.
        //
        // Точки без сетевого адреса (OwnerNetId <= 0: TestStand автотестов и проб,
        // где стенд не спавнится через WorldSpawner) остаются на старом пути:
        // AttachServerSide по реестру их не найдёт и откажет в креплении.
        if (best.OwnerNetId > 0)
        {
            AttachServerSide(best.OwnerNetId, best.NetId);
            return IsAttached;
        }

        AttachTo(best);
        return true;
    }

    /// <summary>
    /// Пристегнуться к точке. Игрок переносится в иерархию точки (Reparent), поэтому
    /// все её перемещения/повороты (корабль, станция) передаются игроку и его камере.
    /// snapToAttachPoint = true — встать в позу, заданную точкой (PlayerOffset),
    /// лицом вдоль её -Z; false — сохранить мировое положение.
    /// </summary>
    public void AttachTo(AttachPoint point, bool snapToAttachPoint = true)
    {
        if (point == null || !GodotObject.IsInstanceValid(point))
        {
            GD.PrintErr("Player.AttachTo: точка крепления недействительна");
            return;
        }
        if (AttachedTo == point)
            return;
        if (IsAttached)
            Detach();

        AttachedTo = point;
        Reparent(point, keepGlobalTransform: !snapToAttachPoint);
        if (snapToAttachPoint)
            Transform = new Transform3D(Basis.Identity, point.PlayerOffset);

        Velocity = Vector3.Zero;
        ResetPhysicsInterpolation();

        // EVA (С3): любое крепление = режим кресла — и дебаг-кнопка, и F, и
        // автостыковка на спавне, и ApplyAttach-реплика. Вызов ПОСЛЕ Reparent и
        // присваивания `AttachedTo`: хук читает владельца точки (корабль/станция,
        // Б2 заправка) и мировую позу. Для чужого пира хук почти ничего не делает
        // (все ветки guarded `if (!_isLocal) return`).
        OnAttachedEvaMode();
    }

    /// <summary>
    /// Открепиться: игрок возвращается в мировой корень сцены, мировая позиция
    /// сохраняется. Дальше (этап 6) его можно пристегнуть к другой сцене.
    /// </summary>
    public void Detach()
    {
        if (!IsAttached)
            return;

        AttachedTo = null;
        Reparent(_worldRoot, keepGlobalTransform: true);
        Velocity = Vector3.Zero;
        ResetPhysicsInterpolation();

        // EVA: выход из крепления = свободный режим (джетпак), сборщик мыши
        // обратно, позиция тела в модель инерции. Порядок важен: AttachTo зовёт
        // Detach() перед новым креплением, и завершающий OnAttachedEvaMode()
        // перекроет режим — залипнуть в джетпаке, сидя в кресле, нельзя.
        OnDetachedEvaMode();
    }

    /// <summary>
    /// Перемещение в горизонтальной (для точки крепления) плоскости — задел на этап 6:
    /// WASD-ходьба по палубе корабля или станции. Ось X = право точки, Z = её назад,
    /// «вверх» точки — её локальный +Y, поэтому игрок не «всплывает» относительно палубы.
    /// Ввод в этой версии ещё не подключается (клавиши игрока — этап 2/6).
    /// </summary>
    public void MoveInAttachedPlane(Vector2 input, float speed = 3f)
    {
        if (!IsAttached || AttachedTo == null)
            return;

        var basis = AttachedTo.GlobalTransform.Basis;
        // Вертикаль относительно палубы (магнитные ботинки) добавится на этапе 6.
        var wish = basis.X * input.X + basis.Z * input.Y;
        Velocity = wish * speed;
        MoveAndSlide();
    }

    // --------------------------------------------------------- сеть (§3.4)
    //
    // Адрес точки крепления — пара целых (ownerNetId, pointNetId), а не NodePath:
    // пути расходятся между процессами в тот момент, когда кто-то сделал Reparent.
    // Reparent выполняется сервером (он валидирует запрос) и повторяется каждым
    // пиром по RPC ApplyAttach — только поэтому дерево не разъезжается (§3.5 п.6).
    // Сигнатуры AttachTo/Detach при этом НЕ меняются: это единственное место,
    // где происходит Reparent.

    /// <summary>
    /// Клиентский запрос пристегнуться к точке (ownerNetId, pointNetId).
    /// CallLocal = false: на отправителе не выполняется — работает только
    /// серверный экземпляр того же узла (пути спавненных узлов совпадают, §3.6).
    /// </summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RequestAttach(int ownerNetId, int pointNetId)
    {
        if (NetworkManager.Instance is not { IsServer: true })
        {
            GD.PrintErr("Player.RequestAttach: запрос обработал не сервер — " +
                        "валидировать отправителя некому");
            return;
        }
        if (!IsRequestFromOwner())
            return;
        AttachServerSide(ownerNetId, pointNetId);
    }

    /// <summary>Клиентский запрос открепиться. Тот же порядок, что у RequestAttach.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RequestDetach()
    {
        if (NetworkManager.Instance is not { IsServer: true })
        {
            GD.PrintErr("Player.RequestDetach: запрос обработал не сервер");
            return;
        }
        if (!IsRequestFromOwner())
            return;
        DetachServerSide();
    }

    /// <summary>Сервер подтвердил крепление — каждый пир повторяет Reparent локально.
    /// RpcMode в Godot 4 не имеет значения «только сервер» (есть Authority/AnyPeer/
    /// Disabled), поэтому режим AnyPeer + проверка отправителя == 1 внутри: подделать
    /// рассылку может только пир с id 1 (§2.5 п.2).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void ApplyAttach(int ownerNetId, int pointNetId)
    {
        if (!IsFromServer())
            return;
        var point = NetworkManager.Instance?.FindAttachPoint(ownerNetId, pointNetId);
        if (point == null)
        {
            // Молча проглотить нельзя: дерево разъедется, и дальше игрок «в кабине»
            // у одного пира и свободен у другого.
            GD.PrintErr($"Player.ApplyAttach: точка ({ownerNetId}, {pointNetId}) " +
                        "не найдена в реестре — дерево разойдётся");
            return;
        }
        AttachTo(point);
    }

    /// <summary>Сервер подтвердил открепление — каждый пир делает Detach локально.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void ApplyDetach()
    {
        if (!IsFromServer())
            return;
        Detach();
    }

    /// <summary>Серверная (и offline) сердцевина пристёжки: валидация точки по
    /// реестру, локальный Reparent, рассылка ApplyAttach.
    /// Переход «из корабля в корабль» — ОДИН вызов: AttachTo сам делает Detach,
    /// между сообщениями игрок не висит непристёгнутым (§3.4 п.3).
    /// Публичен только для offline и автотестов (§3.9 п.15): в сети вызывается
    /// исключительно из RequestAttach после проверки отправителя.</summary>
    public void AttachServerSide(int ownerNetId, int pointNetId)
    {
        var point = NetworkManager.Instance?.FindAttachPoint(ownerNetId, pointNetId);
        if (point == null)
        {
            GD.PrintErr($"Player: точка крепления ({ownerNetId}, {pointNetId}) " +
                        "не найдена в реестре — отказ в запросе");
            return;
        }
        // Блок D (Д3, BUG-009): кресло занято — нельзя «увести» пилота у чужого
        // корабля, запрыгнув в его кабину из EVA. Без гейта вход в кабину =
        // nm.SetPilot(pilotPeer) перезаписывал бы PilotPeerId чужого корабля, и
        // два игрока гоняли бы один корпус (счётчик «PilotPeerId == pilot» ломается,
        // ввод пилота-владельца теряется). Гейт стоит ДО AttachTo: отказ не должен
        // менять ни дерево, ни назначение пилота.
        //   • Точка не кресло (пол/шлюз станции) — свободна, пропускаем.
        //   • Пилот == FreeShipPilotId (сеть: автопилот 1; offline: «никто» 0) —
        //     кабина не занята реальным игроком, вход легален (спавн-кресло,
        //     AutoAttachToFreeSeat, пересадка с другого корабля).
        //   • Пилот == этот игрок — легальная пересадка в «своё» кресло / возврат
        //     после реконнекта (PilotPeerId ещё держит его peerId).
        // Отказ = PrintErr attach-denied и ранний return без AttachTo/SetPilot/Rpc.
        if (point.Kind == AttachPoint.KindOf.Seat &&
            NetworkManager.Instance is { IsAuthoritySide: true } nmGate &&
            nmGate.FindShip(ownerNetId) is { } targetShip &&
            targetShip.PilotPeerId != nmGate.FreeShipPilotId &&
            targetShip.PilotPeerId != GetMultiplayerAuthority())
        {
            GD.PrintErr($"Player: attach-denied — кабина корабля {ownerNetId} " +
                        $"занята пилотом {targetShip.PilotPeerId}, игрок " +
                        $"{GetMultiplayerAuthority()} не может занять чужое кресло");
            return;
        }
        // §3.9 п.17: переход «корабль A → корабль B» — один вызов, но авторитет
        // A надо вернуть серверу, иначе пилот висит на двух кораблях сразу
        // (счётчик «PilotPeerId == pilot» > 1 = два пилотируемых корабля).
        // Запоминаем прежний корабль ДО AttachTo: после Reparent AttachedTo
        // указывает на новую точку, и спросить «откуда ушли» будет негде.
        var prevShip = AttachedTo != null
            ? NetworkManager.Instance?.FindShip(AttachedTo.OwnerNetId)
            : null;
        int pilotPeer = GetMultiplayerAuthority();

        AttachTo(point);
        // §3.5 п.8 + В14=B: вход в кабину = НАЗНАЧЕНИЕ ПИЛОТА, а не смена
        // авторитета. Авторитет корабля навсегда остаётся у сервера (id = 1):
        // только так сервер интегрирует физику сам, а не принимает чужую позицию
        // как факт. Владелец игрока (GetMultiplayerAuthority) становится пилотом —
        // он шлёт серверу кадры ввода. Если владелец точки — не корабль (станция
        // на этапе 6), пилота не назначаем: у станции его назначает сервер навсегда (§3.4 п.2).
        // BUG-008/009: гейт расширен с IsServer на IsAuthoritySide — offline тоже
        // назначает пилота по факту пристёжки (в offline SelfId = 1, и без смены
        // пилота все корабли оставались «локальными»).
        if (NetworkManager.Instance is { IsAuthoritySide: true } nm)
        {
            if (nm.FindShip(ownerNetId) is { } ship)
                nm.SetPilot(shipNetId: ownerNetId, peerId: pilotPeer);
            // Прежний корабль освобождаем от пилота только если этот игрок был
            // его пилотом (иначе это наблюдатель, и пилотом A он не был).
            // В сети свободный корабль уходит автопилоту (1), в offline — «никому» (0).
            if (prevShip != null && prevShip.Net?.ShipNetId != ownerNetId &&
                prevShip.PilotPeerId == pilotPeer)
                nm.SetPilot(shipNetId: prevShip.Net!.ShipNetId, peerId: nm.FreeShipPilotId);
        }
        if (NetworkManager.Instance is { HasRealPeer: true })
            Rpc(nameof(ApplyAttach), ownerNetId, pointNetId);
    }

    /// <summary>Серверная (и offline) сердцевина отстёжки.</summary>
    private void DetachServerSide()
    {
        // §3.5 п.8: если этот игрок был пилотом корабля, из которого он отстёгивается,
        // пилот возвращается серверу (автопилот, peerId = 1). Пилот определяется
        // по PilotPeerId корабля-владельца точки, а не по тому, кто где сидел:
        // PilotPeerId обновляется на сервере при каждой смене (§4 В3 п.3, В14=B).
        var ship = AttachedTo != null
            ? NetworkManager.Instance?.FindShip(AttachedTo.OwnerNetId)
            : null;
        int playerPeer = GetMultiplayerAuthority();
        Detach();
        if (ship != null && ship.PilotPeerId == playerPeer &&
            NetworkManager.Instance is { IsAuthoritySide: true } nm)
            // BUG-009: в offline освободившийся корабль уходит «никому» (0), а не
            // автопилоту 1: SelfId = 1 сделал бы его снова «локальным пилотом»
            // игрока, и отстёжка не снимала бы управление. По инерции тело летит
            // само (Freeze = false у авторитета, ввод не читается).
            nm.SetPilot(shipNetId: ship.Net!.ShipNetId, peerId: nm.FreeShipPilotId);
        if (NetworkManager.Instance is { HasRealPeer: true })
            Rpc(nameof(ApplyDetach));
    }

    /// <summary>Запрос RPC должен прийти от пира, которому принадлежит этот игрок:
    /// иначе любой клиент гонял бы чужих игроков по сцене (§2.5 п.2).</summary>
    private bool IsRequestFromOwner()
    {
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender == GetMultiplayerAuthority())
            return true;
        GD.PrintErr($"Player: пир {sender} просит переместить чужого игрока " +
                    $"(авторитет узла = {GetMultiplayerAuthority()}) — отказ");
        return false;
    }

    /// <summary>Ближайшая к игроку точка из реестра NetworkManager (§3.5 п.3).
    /// В offline реестр тоже работает: адрес выдаёт тот же счётчик (§1.8).</summary>
    private (int Owner, int Point) NearestAttachPoint()
    {
        var nm = NetworkManager.Instance;
        if (nm == null)
            return (0, 0);
        int bestOwner = 0, bestPoint = 0;
        float bestDist = float.MaxValue;
        foreach (var (key, p) in nm.AttachPoints)
        {
            if (!GodotObject.IsInstanceValid(p))
                continue;
            float d = GlobalPosition.DistanceSquaredTo(p.GlobalPosition);
            if (d < bestDist)
            {
                bestDist = d;
                bestOwner = key.Owner;
                bestPoint = key.Point;
            }
        }
        return (bestOwner, bestPoint);
    }

}
