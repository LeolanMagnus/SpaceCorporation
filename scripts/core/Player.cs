using Godot;

namespace SpaceCorporation;

/// <summary>
/// Игрок-персонаж: капсула + привязанная к ней камера (scenes/Player.tscn).
///
/// Ключевая механика задела на этап 6 — крепление к точкам (AttachPoint):
///   AttachTo(point) — игрок становится узлом точки крепления (кресло кабины,
///                     позже — пол станции/палубы) и летает/ходит вместе с ней;
///   Detach()        — возвращается в мировой корень сцены, сохраняя позицию.
///
/// Поэтому в Main.tscn игрок просто стоит рядом с кораблём, а сам факт «игрок
/// в кабине» задаётся кодом (точка Seat с IsDefaultForPlayer = true), а не
/// вложенностью узлов в сцене: иерархию можно менять в рантайме.
///
/// Горизонтальное перемещение по палубе (этап 6) — MoveInAttachedPlane: движение
/// в локальной плоскости XZ точки крепления, то есть «вперёд» для игрока =
/// «вперёд» корабля или станции, к которой он пристёгнут.
/// </summary>
public partial class Player : CharacterBody3D
{
    /// <summary>Высота капсулы (совпадает с CapsuleShape3D в scenes/Player.tscn).</summary>
    public const float CapsuleHeight = 1.8f;

    [Export] public NodePath CameraRigPath { get; set; } = "CameraRig";

    /// <summary>Точка крепления, к которой пристёгнут игрок (null — свободен).</summary>
    public AttachPoint? AttachedTo { get; private set; }

    public bool IsAttached => AttachedTo != null;

    /// <summary>Куда игрок возвращается при Detach (мировой корень сцены).</summary>
    private Node3D _worldRoot = null!;

    private Node3D _cameraRig = null!;
    private bool _awaitingDefaultPoint = true;
    private int _defaultPointAttempts;

    /// <summary>Этот узел — локальный игрок (§3.5 п.2): ввод, камера и кнопки только
    /// для него. В offline авторитет = 1 = SelfId, поэтому true, и поведение стадии 1
    /// не меняется. На клиенте чужие игроки имеют авторитет другого пира — у них
    /// false; в dedicated-процессе узла Player нет вовсе (§2.4).</summary>
    private bool _isLocal;

    /// <summary>Свои кнопки дебаг-меню (только для _isLocal). Храним ручки,
    /// чтобы снять именно их при _ExitTree, не задев кнопки нового игрока с теми же
    /// метками при смене роли (§3.11 п.9). Ручка работает и если кнопка была
    /// зарегистрирована до готовности слоя (тогда снимается отложенная заявка).</summary>
    private DebugButtonHandle? _debugDetachButton;
    private DebugButtonHandle? _debugAttachButton;

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.Character;
        // Пока игрок сидит в корабле, он не должен сталкиваться с его корпусом:
        // маска намеренно без слоя Ship. Столкновения со станцией и мишенями — есть.
        CollisionMask = CollisionLayers.Station | CollisionLayers.Target;
        MotionMode = MotionModeEnum.Grounded;
        UpDirection = Vector3.Up;
        FloorMaxAngle = Mathf.DegToRad(50f);

        _cameraRig = GetNode<Node3D>(CameraRigPath);
        // §3.5 п.1: при спавне через MultiplayerSpawner родителем станет GameRoot,
        // а при креплении игрок вообще висит под ShipHull — поэтому мировой корень
        // берётся явно, а не из GetParent(): идём вверх по цепочке Node3D-предков до
        // верхнего 3D-узла сцены (в игре это Main; Stage1Test-обёртка — Node, цепочка
        // обрывается на Main). GetTree().CurrentScene не годится: в тестах текущей
        // сценой является обёртка Stage1Test, а не Main.
        _worldRoot = FindWorldRoot();
        if (_worldRoot == null)
            throw new System.InvalidOperationException(
                "Player должен лежать в сцене внутри 3D-узла (Main), чтобы было куда открепляться");

        // §3.5 п.2: ввод, камера и кнопки — только локально-авторитетному игроку.
        // В offline авторитет = 1 = SelfId, поэтому local == true и поведение
        // стадии 1 не меняется. У чужих игроков камеру глушим принудительно:
        // в Player.tscn стоит current = true, и вторая камера перехватит viewport.
        //
        // Встроенный IsMultiplayerAuthority() при снятом peer (после Disconnect
        // MultiplayerPeer = null) зовёт GetUniqueId() и печатает ERROR. Сравниваем
        // авторитет узла с локальным id напрямую; при снятом peer локальный id = 1
        // (offline-авторитет), поэтому в offline это 1 == 1.
        int localId = Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;
        _isLocal = GetMultiplayerAuthority() == localId;
        if (!_isLocal)
        {
            var cam = GetActiveCamera();
            if (cam != null && cam.IsCurrent())
                cam.Current = false;
        }

        // Автопристёгивание откладывается на первый физический шаг: Reparent во время
        // _Ready() запрещён (дерево сцены ещё достраивается), а на первом шаге все
        // точки уже в группе attach_point — порядок узлов в Main.tscn не важен.
        _awaitingDefaultPoint = true;

        // Дебаг-кнопки: цикл «открепиться → свободно → пристегнуться обратно» — ровно
        // то, что понадобится на этапе 6 для перехода корабль ↔ станция.
        // §3.5 п.3: только локальному игроку, и вместо прямых AttachTo/Detach —
        // запрос через сеть (Request*), чтобы Reparent был на сервере и повторялся
        // каждым пиром. В offline запрос сворачивается в локальный вызов.
        if (_isLocal)
        {
            // Держим СВОИ кнопки по экземплярам: при смене роли (Offline→Host) старый
            // игрок удаляется QueueFree, а новый регистрирует кнопки с теми же метками
            // в том же кадре. Снятие по экземпляру гарантирует, что _ExitTree старого
            // игрока уберёт именно его кнопки, а не кнопки нового (§3.11 п.9).
            _debugDetachButton = DebugInfoBus.RegisterDebugButton("Detach Player", OnButtonDetach);
            _debugAttachButton = DebugInfoBus.RegisterDebugButton("Attach Player to Seat", OnButtonAttach);
        }


        DebugInfoBus.RegisterInfoSource("Player", () =>
        {
            // Защита от гонки удаления: если этот узел уже освобождён, а источник
            // ещё висит в шине (QueueFree откладывает _ExitTree до конца кадра),
            // GetParent() бросил бы ObjectDisposedException (§3.11 п.9).
            if (!GodotObject.IsInstanceValid(this))
                return string.Empty;
            return
                $"player: parent = {GetParent()?.GetName()}, " +
                $"attached = {AttachedTo?.Name ?? "-"}, " +
                $"pos = {Fmt(GlobalPosition)}";
        });
    }

    public override void _ExitTree()
    {
        // Снимаем регистрации в шине дебага ТОЛЬКО при реальном удалении узла
        // (QueueFree при смене роли Offline→Dedicated, §3.6): лямбда-источник
        // «Player» и кнопки захватывают этот узел, и если он освобождён, а источник
        // остался в шине, DebugInfoBus.BuildInfoText каждый кадр зовёт мёртвую лямбду
        // → GetParent() на disposed-объекте → ObjectDisposedException (§3.11 п.9).
        //
        // Важно: _ExitTree срабатывает и при Reparent (пристёжка/отстёжка игрока
        // переносит узел между точкой и мировым корнем), а _Ready после Reparent
        // НЕ вызывается повторно. Поэтому без проверки на удаление кнопки и источник
        // терялись бы после первой же пристёжки (§3.5). IsQueuedForDeletion() == true
        // ровно когда узел помечен QueueFree — это и есть путь удаления.
        if (!IsQueuedForDeletion())
            return;

        DebugInfoBus.RemoveInfoSource("Player");
        if (_isLocal)
        {
            // Снимаем СВОИ кнопки по ручкам: при Offline→Host старый игрок удаляется
            // QueueFree (этот _ExitTree отработает в конце кадра), а новый игрок уже
            // успел зарегистрировать кнопки с теми же метками. Снятие по ручке убирает
            // ровно нашу кнопку (или её отложенную заявку), не задев кнопки нового
            // игрока (§3.11 п.9).
            DebugInfoBus.RemoveDebugButton(_debugDetachButton);
            DebugInfoBus.RemoveDebugButton(_debugAttachButton);
            _debugDetachButton = null;
            _debugAttachButton = null;
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

    /// <summary>Верхний Node3D-предок: мировой корень сцены (§3.5 п.1). Обёртка
    /// теста — обычный Node, поэтому цепочка обрывается на Main.</summary>
    private Node3D? FindWorldRoot()
    {
        Node3D? top = null;
        for (var n = GetParent(); n != null; n = n.GetParent())
            if (n is Node3D n3)
                top = n3;
        return top;
    }

    public override void _PhysicsProcess(double delta)
    {
        // §3.5 п.4: автопристёгивание — только серверу и offline. На клиенте
        // крепление приходит репликой ApplyAttach, и _awaitingDefaultPoint не должен
        // заставлять клиента крепиться самому (иначе Reparent разойдётся с сервером).
        var nm = NetworkManager.Instance;
        bool authoritySide = nm == null || !nm.HasRealPeer || nm.IsServer;
        if (!_awaitingDefaultPoint || !authoritySide)
            return;

        if (AttachToDefaultPoint())
        {
            _awaitingDefaultPoint = false;
            return;
        }

        // Точки ещё нет в группе (её сцена достраивается) — пробуем в следующем шаге,
        // но бесконечно молчать не будем: через секунду предупреждаем один раз.
        if (++_defaultPointAttempts == 60)
        {
            GD.PushWarning("Player: точка крепления по умолчанию " +
                           "(AttachPoint.IsDefaultForPlayer) так и не найдена");
        }
    }

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
        if (NetworkManager.Instance is { IsServer: true } nm)
        {
            if (nm.FindShip(ownerNetId) is { } ship)
                nm.SetPilot(shipNetId: ownerNetId, peerId: pilotPeer);
            // Прежний корабль освобождаем от пилота только если этот игрок был
            // его пилотом (иначе это наблюдатель, и пилотом A он не был).
            if (prevShip != null && prevShip.Net?.ShipNetId != ownerNetId &&
                prevShip.PilotPeerId == pilotPeer)
                nm.SetPilot(shipNetId: prevShip.Net!.ShipNetId, peerId: 1);
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
            NetworkManager.Instance is { IsServer: true } nm)
            nm.SetPilot(shipNetId: ship.Net!.ShipNetId, peerId: 1);
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

    /// <summary>RPC-рассылка состояния принимается только от сервера (id = 1):
    /// в авторитетной сервером модели именно он рассылает Apply* всем (§2.5).</summary>
    private bool IsFromServer() => Multiplayer.GetRemoteSenderId() == 1;

    // ------------------------------------------------------ дебаг-кнопки

    private void OnButtonAttach()
    {
        var (owner, point) = NearestAttachPoint();
        if (owner <= 0)
        {
            GD.PrintErr("Player: в реестре нет ни одной точки крепления");
            return;
        }
        // §3.5 п.3: при нескольких кораблях кнопка крепит к ближайшей точке
        // реестра, а не к Seat первого.
        GD.Print($"attach → ship#{owner}/point#{point}");
        // RPC → сервер нужен ТОЛЬКО настоящему клиенту. Хост — это и есть сервер
        // (авторитетная сторона): у его собственного игрока нет клиент→серверного
        // перехода, и RequestAttach с CallLocal=false отработал бы НЕ из сети
        // (GetRemoteSenderId() == 0), а IsRequestFromOwner сравнил бы 0 с авторитетом
        // 1 и отклонил запрос (§2.5 п.2). Хост и offline выполняют серверную логику
        // напрямую — тот же признак authoritySide, что в _PhysicsProcess.
        if (NetworkManager.Instance is { IsClient: true })
            RequestAttach(owner, point);      // клиент: RPC → сервер
        else
            AttachServerSide(owner, point);   // хост/offline: тот же код локально (§1.8)
    }

    private void OnButtonDetach()
    {
        // См. OnButtonAttach: RPC → сервер только настоящему клиенту. Хост (он же
        // сервер) и offline выполняют DetachServerSide напрямую — иначе RPC с
        // CallLocal=false отработает не из сети, GetRemoteSenderId() == 0, и
        // IsRequestFromOwner отклонит запрос («пир 0 просит … авторитет = 1»).
        if (NetworkManager.Instance is { IsClient: true })
            RequestDetach();                  // клиент: RPC → сервер
        else
            DetachServerSide();               // хост/offline: локально
        GD.Print($"player detached, global = {Fmt(GlobalPosition)}");
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

    /// <summary>Камера игрока (для дебаг-инфо и будущего прицела).</summary>
    public Camera3D? GetActiveCamera() =>
        _cameraRig?.GetNodeOrNull<Camera3D>("Camera3D");
}
