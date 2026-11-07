using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial NetworkManager: вырезано из NetworkManager.cs при partial-декомпозиции.
public partial class NetworkManager : Node
{
    /// <summary>Отложенные назначения пилота: shipNetId → peerId. RPC SetPilot
    /// может прийти раньше, чем спавнер создаст у пира узел корабля; пир кладёт
    /// пару сюда и применяет в Ship._Ready (§4 В4 п.2).</summary>
    private readonly Dictionary<int, int> _pilots = new();

    /// <summary>Шов для автотестов (§3.9 п.15): дополнительные «существующие» пиры,
    /// которых нет в реальном соединении. Живой сети в headless-тесте нет
    /// (ENet-пиры в одном процессе к себе не подключаются, §3.10 шаг 6), а assert
    /// про смену пилота на id=2 требует, чтобы валидация пира его пропускала.
    /// Пустое множество = боевое поведение: только реальные GetPeers().</summary>
    public static readonly System.Collections.Generic.HashSet<int> TestExtraPeers = new();

    public bool PeerExists(int peerId) =>
        System.Array.IndexOf(Multiplayer.GetPeers(), peerId) >= 0 ||
        TestExtraPeers.Contains(peerId);

    /// <summary>Шов теста (Этап 5, п.4 Stage5Test): изобразить роль клиента,
    /// чтобы гейт <c>IsAuthoritySide</c> у серверных методов (SetDockStateServerOnly)
    /// был проверен детерминированно в headless. Возвращает прежнюю роль — тест
    /// обязан восстановить её этим значением. Реальной сети шов не создаёт.</summary>
    public NetRole TestSetRoleForTest(NetRole next)
    {
        var prev = Role;
        if (prev != next)
        {
            Role = next;
            DebugInfoBus.Report($"role(test-seam): {prev.ToString().ToLower()} -> {next.ToString().ToLower()}");
        }
        return prev;
    }

    /// <summary>Назначить пилота корабля (только сервер). peerId = 1 — сервер:
    /// корабль летает по входным кадрам серверного пилота (offline-режим) либо
    /// стоит замороженным, если кадров ни от кого нет (правило Freeze, §3.5 п.8).</summary>
    public void SetPilot(int shipNetId, int peerId)
    {
        // BUG-008/009: назначать пилота вправе авторитетная сторона — сервер
        // (Dedicated/Host) И offline-процесс (он сам себе сервер, LocalAuthorityId
        // = 1). Раньше гейт был `IsServer`, и в offline пристёжка/отстёжка вообще
        // не меняли PilotPeerId: все корабли оставались с PilotPeerId = 1, а
        // SelfId = 1 делал их «локальными пилотами» одновременно (BUG-008).
        if (!IsAuthoritySide)
        {
            GD.PrintErr($"NetworkManager.SetPilot({shipNetId}, {peerId}): " +
                        "пилота назначает только сервер");
            return;
        }
        // peerId = 0 («никто не пилотирует») допустим только в offline: в сети
        // свободный корабль уходит автопилоту (peerId = 1), иначе клиент мог бы
        // оставить корабль без серверного интегратора ввода.
        if (peerId != 0 && peerId != 1 && !PeerExists(peerId))
        {
            GD.PrintErr($"NetworkManager.SetPilot: пир {peerId} не существует — отказ");
            return;
        }
        if (peerId == 0 && HasRealPeer)
        {
            GD.PrintErr($"NetworkManager.SetPilot({shipNetId}, 0): в сети свободный " +
                        "корабль уходит автопилоту (1), а не «никому» — отказ");
            return;
        }

        // §3.9 п.17 (рантайм-гейт): пир не может пилотировать два корабля сразу —
        // иначе между двумя назначениями висит состояние «один пир — два входа»,
        // и он шлёт кадры за оба. Освобождаем прежний ДО назначения нового.
        // peerId = 0 — «никто», а не пир: правило одного корабля на пирра к нему
        // не применяется (несколько свободных кораблей с пилотом 0 — норма).
        if (peerId != 1 && peerId != 0)
            foreach (var other in _ships.Values)
                if (GodotObject.IsInstanceValid(other) && other.Net != null &&
                    other.Net.ShipNetId != shipNetId && other.PilotPeerId == peerId)
                {
                    GD.Print($"NetworkManager: пир {peerId} уже пилотирует " +
                             $"ship#{other.Net.ShipNetId} — освобождаю прежний (§3.9 п.17)");
                    ReleasePilot(other.Net.ShipNetId);
                }

        _pilots[shipNetId] = peerId; // единый источник правды, в т.ч. для отложенных


        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyPilot(peerId);
        else
            GD.PrintErr($"NetworkManager.SetPilot({shipNetId}, {peerId}): " +
                        "корабля с таким адресом на сервере нет — назначение отложено");

        // §2.4 (R4): якорь интереса и адрес пилотской записи этого клиента.
        if (peerId >= 2)
            _broadcaster?.SetOwnShip(peerId, shipNetId);

        if (HasRealPeer)
            Rpc(nameof(RpcSetPilot), shipNetId, peerId);
    }

    /// <summary>Снять пилота (корабль уходит серверу, peerId = 1) без рассылки:
    /// рассылка — внутри SetPilot. Отдельный метод нужен, чтобы освобождение
    /// прежнего корабля не запускало рекурсивный обход.</summary>
    private void ReleasePilot(int shipNetId)    {
        int free = FreeShipPilotId; // сеть: автопилот (1); offline: «никто» (0) — BUG-008/009
        _pilots[shipNetId] = free;
        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyPilot(free);
        if (HasRealPeer)
            Rpc(nameof(RpcSetPilot), shipNetId, free);
    }

    /// <summary>Серверная рассылка нового пилота. Едет через autoload, а не через
    /// узел Ship: у пира узла ещё может не быть (спавнер асинхронный), и RPC по пути
    /// к несуществующему узлу теряется без следа (§4 В4 п.2).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcSetPilot(int shipNetId, int peerId)
    {
        if (Multiplayer.GetRemoteSenderId() != 1)
            return; // рассылку пилота делает только сервер (id = 1)

        _pilots[shipNetId] = peerId;
        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyPilot(peerId);
        // иначе — применится в Ship._Ready через ConsumePendingPilot
    }

    /// <summary>Последний известный пилот корабля по адресу (0 = не назначался).
    /// Нужен, чтобы пир, создавший узел позже RPC, не остался без пилота.</summary>
    public bool TryGetPilot(int shipNetId, out int peerId) =>
        _pilots.TryGetValue(shipNetId, out peerId);

    /// <summary>BUG-008/009: первоначальный пилот спавнящегося корабля — только
    /// запись в реестр, БЕЗ ApplyPilot/RPC. Узел ещё не в дереве и не
    /// зарегистрирован; корабль подхватит значение в _Ready через TryGetPilot.
    /// Лишний ApplyPilot здесь снял бы дебаг-кнопку чужого корабля (метки кнопок
    /// общие для всех кораблей) и-resetнул бы очередь ввода у уже летящего тела.</summary>
    public void SetInitialPilot(int shipNetId, int peerId)
    {
        if (!IsAuthoritySide)
            return;
        _pilots[shipNetId] = peerId;
    }

    // -------------------------------------------------- идентичность (§4 В2)
    //
    // playerKey — GUID, созданный процессом один раз и лежащий в user://identity.cfg.
    // peerId (unique_id ENet) выдаётся заново при каждом переподключении, поэтому
    // ключ «где этот игрок был» — строковый playerKey, а не peerId. Папка user://
    // привязана к имени проекта: редактор и CLI `godot --path .` дают одну и ту же
    // папку (проверено probe_paths.gd), поэтому один ключ работает и там, и там.

    private string? _playerKey;
    public string PlayerKey => _playerKey ??= LoadOrCreatePlayerKey();

    private const string IdentityPath = "user://identity.cfg";

    private static string LoadOrCreatePlayerKey()
    {
        if (FileAccess.FileExists(IdentityPath))
        {
            using var f = FileAccess.Open(IdentityPath, FileAccess.ModeFlags.Read);
            var line = f?.GetLine()?.Trim();
            if (!string.IsNullOrEmpty(line))
                return line!;
        }
        var key = Guid.NewGuid().ToString("N");
        using var w = FileAccess.Open(IdentityPath, FileAccess.ModeFlags.Write);
        w?.StoreLine(key);
        GD.Print($"NetworkManager: новый playerKey = {key[..8]}… (записан в {IdentityPath})");
        return key;
    }

    /// <summary>Серверное знание «где игрок был». Basis, а не Quaternion: в C#-обёртке
    /// Transform3D.Basis имеет тип Godot.Basis, Quaternion появляется только через
    /// Basis.GetQuaternion() — при сериализации в RPC-словарь это разные типы (§2.4).</summary>
    public struct LastSeenState
    {
        public Vector3 Position;
        public Basis Rotation;
        public int OwnerNetId;   // владелец точки крепления (ShipNetId/StationNetId)
        public int PointNetId;   // NetId точки на этом владельце
        public bool WasAttached; // true — вышел пристёгнутым, возврат в точку крепления
    }

    // -------------------------------------------------- таблицы входа (§2.4)
    //
    //   peerId    → Player        // живой слот, живёт до PeerDisconnected
    //   playerKey → LastSeenState // позиция/крепление, живёт до рестарта процесса
    //   peerId    → playerKey     // обратная связь: по слоту найти ключ для записи
    //   peerId    → (owner,point) // отложенное крепление: точка ещё не заспавнена

    private readonly Dictionary<int, Player> _peerPlayers = new();
    private readonly Dictionary<string, LastSeenState> _lastSeen = new();
    private readonly Dictionary<int, string> _peerKeys = new();
    private readonly Dictionary<int, (int Owner, int Point)> _pendingAttach = new();

    public int LivePlayerCount => _peerPlayers.Count;
    public bool TryGetPeerPlayer(int peerId, out Player? player) =>
        _peerPlayers.TryGetValue(peerId, out player);

    /// <summary>Снимок текущего состояния игрока в LastSeenState по его ключу.
    /// Вызывается ПЕРЕД удалением узла (после QueueFree читать позицию не с чего, §2.4).</summary>
    private void CaptureLastSeen(Player player, string key)
    {
        if (player == null || !GodotObject.IsInstanceValid(player))
            return;
        var st = new LastSeenState
        {
            Position = player.GlobalPosition,
            Rotation = player.GlobalTransform.Basis,
            WasAttached = player.IsAttached,
            OwnerNetId = player.IsAttached ? player.AttachedTo!.OwnerNetId : 0,
            PointNetId = player.IsAttached ? player.AttachedTo!.NetId : 0,
        };
        _lastSeen[key] = st;
        DebugInfoBus.Report($"save LastSeen[{key[..System.Math.Min(8, key.Length)]}] " +
                            $"@ ({st.Position.X:F2},{st.Position.Y:F2},{st.Position.Z:F2}) " +
                            $"attached = {st.WasAttached}");
    }

    /// <summary>
    /// Серверная сердцевина входа в игру (шаг 6, §2.4). Идемпотентна: повторный
    /// запрос от того же пира не создаёт второго игрока. Публична как шов для
    /// автотестов (§3.9 п.15): в сети вызывается только из RpcRequestSpawn после
    /// проверки отправителя.
    /// </summary>
    public void HandleRequestSpawn(int peerId, string playerKey)
    {
        if (!IsServer)
        {
            GD.PrintErr($"NetworkManager.HandleRequestSpawn({peerId}, …): спавнит только сервер");
            return;
        }
        if (peerId < 2)
        {
            // Авторитет игрока никогда не равен 1 (§2.4): серверный процесс живёт без Player.
            GD.PrintErr($"NetworkManager: запрос спавна от id = {peerId} — отказ (авторитет ≥ 2)");
            return;
        }

        // Идемпотентность: у пира уже есть игрок — второго не спавним.
        if (_peerPlayers.TryGetValue(peerId, out var existing) &&
            GodotObject.IsInstanceValid(existing))
        {
            GD.Print($"NetworkManager: пир {peerId} уже в игре — повторный RequestSpawn проигнорирован");
            return;
        }

        int holder = FindPeerHoldingKey(playerKey);

        // Коллизия ключа с игроком ХОСТА (holder == 1): хоста не выгоняем — он и
        // есть сервер. Клиент с тем же ключом (dev-прогон «два окна» одного
        // проекта: user://identity.cfg общий) получает гостевой ключ key#<peerId>.
        // Проверка идёт ДО ветки вытеснения: иначе обычный код вытеснения принял бы
        // слот 1 за «старый слот другого пира» и выгнал бы игрока хоста.
        if (holder == 1)
        {
            string guestKey = $"{playerKey}#{peerId}";
            GD.PrintErr($"NetworkManager: ключ {playerKey} совпал с ключом хоста " +
                        $"(общий user://identity.cfg при двух окнах) — клиенту " +
                        $"выдан гостевой ключ {guestKey}");
            playerKey = guestKey;
            holder = 0;
        }

        // Ключ уже занят другим ЖИВЫМ пиром (оборванное соединение ещё не отвалилось
        // по таймауту). Выбираем явно: выгнать старый слот, записав LastSeenState ДО
        // DisconnectPeer(force:true) — при force:true сигнал PeerDisconnected не
        // приходит, и позиция иначе потерялась бы (§2.4).
        if (holder != 0 && holder != peerId &&
            _peerPlayers.TryGetValue(holder, out var oldPlayer) &&
            GodotObject.IsInstanceValid(oldPlayer))
        {
            GD.Print($"NetworkManager: ключ {playerKey[..System.Math.Min(8, playerKey.Length)]} " +
                     $"занят пиром {holder} — выгоняю старый слот (force) и спавню заново");
            CaptureLastSeen(oldPlayer, playerKey);
            _peerPlayers.Remove(holder);
            _peerKeys.Remove(holder);
            _pendingAttach.Remove(holder);
            if (HasRealPeer && System.Array.IndexOf(Multiplayer.GetPeers(), holder) >= 0)
                Multiplayer.MultiplayerPeer?.DisconnectPeer(holder, force: true);
            else
                oldPlayer.QueueFree();
        }

        var player = SpawnPlayerForPeer(peerId, playerKey);
        if (player == null)
            RpcSpawnDeniedTo(peerId, "spawn-failed");
    }

    /// <summary>Сердцевина спавна игрока пира по правилу §2.4 «Где спавнится
    /// игрок»: LastSeenState (позиция/крепление) → SpawnPlayerAt → регистрация в
    /// таблицах. Используется и для клиентов (HandleRequestSpawn), и для игрока
    /// хоста с авторитетом 1 (SpawnHostPlayer, стадия 0-rework). Возвращает узел
    /// или null (нет WorldSpawner / спавн не удался).</summary>
    public Player? SpawnPlayerForPeer(int peerId, string playerKey)
    {
        var spawner = WorldSpawner.Current;
        if (spawner == null)
        {
            GD.PrintErr("NetworkManager: WorldSpawner.Current нет — " +
                        "спавн невозможен (нет GameRoot)");
            return null;
        }

        Player? player;
        (int Owner, int Point)? deferred = null;
        if (_lastSeen.TryGetValue(playerKey, out var seen))
        {
            if (seen.WasAttached)
            {
                var point = FindAttachPoint(seen.OwnerNetId, seen.PointNetId);
                if (point != null)
                {
                    // Точка жива: спавним на свободной стартовой, затем крепим в точку
                    // (AttachTo сам переносит в иерархию точки, мировая позиция — из неё).
                    player = spawner.SpawnPlayerAt(playerKey, peerId);
                    player?.AttachServerSide(seen.OwnerNetId, seen.PointNetId);
                }
                else
                {
                    // Точка крепления ещё не заспавнена (порядок Ship/Player) — спавним
                    // на стартовой и откладываем крепление до регистрации точки (§2.4 п.4).
                    player = spawner.SpawnPlayerAt(playerKey, peerId);
                    deferred = (seen.OwnerNetId, seen.PointNetId);
                }
            }
            else
            {
                // Игрок был свободен: возврат в сохранённую позицию/поворот, скорость ноль.
                player = spawner.SpawnPlayerAt(playerKey, peerId,
                    position: seen.Position, hasRotation: true, rotation: seen.Rotation);
                if (player != null)
                    player.Velocity = Vector3.Zero;
            }
        }
        else
        {
            // Первый заход: первая свободная стартовая точка (SpawnPlayerAt выбирает сам).
            player = spawner.SpawnPlayerAt(playerKey, peerId);
        }

        if (player == null)
            return null;

        _peerPlayers[peerId] = player;
        _peerKeys[peerId] = playerKey;
        if (deferred is { } d)
        {
            _pendingAttach[peerId] = d;
            DebugInfoBus.Report($"attach[{playerKey[..System.Math.Min(8, playerKey.Length)]}] " +
                                $"отложено до точки ({d.Owner},{d.Point}) (§2.4 п.4)");
        }
        else if (Autopilot && !player.IsAttached)
        {
            // Headless-стенд §2.6: автопилот-демо без UI, игрок сам не пристёгивается.
            // Сажаем его в свободную кабину (корабль без реального пилота), чтобы
            // сервер назначил ему пилота и метрики предсказания/откатов стало чем мерить.
            AutoAttachToFreeSeat(player);
        }
        return player;
    }

    /// <summary>Только для headless-стенда (§2.6): посадить игрока в первую кабину
    /// (Kind.Seat) корабля, у которого нет реального пилота (PilotPeerId == 1 =
    /// сервер/автопилот). AttachServerSide сам назначит пилота (§3.5 п.8, В14=B).</summary>
    private void AutoAttachToFreeSeat(Player player)
    {
        foreach (var (key, point) in _attachPoints)
        {
            if (point.Kind != AttachPoint.KindOf.Seat)
                continue;
            var ship = FindShip(key.Owner);
            if (ship == null || ship.PilotPeerId != 1)
                continue; // кабина уже занята реальным пилотом
            player.AttachServerSide(key.Owner, key.Point);
            return;
        }
    }

    /// <summary>Спавн игрока ХОСТА (авторитет 1 = id сервера, стадия 0-rework):
    /// тот же путь, что у клиентов — через SpawnPlayerForPeer и MultiplayerSpawner,
    /// чтобы реплика доехала до подключённых клиентов. Публично как шов автотеста.</summary>
    public Player? SpawnHostPlayer()
    {
        if (!IsHost)
        {
            GD.PrintErr("NetworkManager.SpawnHostPlayer: роль не Host — спавн игрока " +
                        "в серверном процессе без локального игрока запрещён (§2.4)");
            return null;
        }
        if (_peerPlayers.TryGetValue(1, out var cur) && GodotObject.IsInstanceValid(cur) &&
            !cur.IsQueuedForDeletion())
            return cur; // идемпотентность: игрок хоста уже есть
        return SpawnPlayerForPeer(1, PlayerKey);
    }

    /// <summary>Перед уходом offline-игрока (авторитет 1) в Host — записать его
    /// LastSeenState по PlayerKey, чтобы SpawnHostPlayer вернул его на то же место
    /// и в то же крепление (стадия 0-rework).</summary>
    public void CaptureHostCandidateLastSeen()
    {
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
            {
                CaptureLastSeen(p, PlayerKey);
                return;
            }
    }

    private int FindPeerHoldingKey(string playerKey)
    {
        foreach (var (peer, key) in _peerKeys)
            if (key == playerKey)
                return peer;
        return 0;
    }

    /// <summary>Серверный путь обработки отключения пира: сначала записать
    /// LastSeenState по ключу слота, ПОТОМ убирать игрока из таблиц (§2.4). Публично
    /// как шов автотеста; реальный сигнал PeerDisconnected зовёт этот же метод.</summary>
    public void OnPeerLeft(int peerId)
    {
        // §2.4 (R4): снять адресата из вещания ДО удаления слота (в т.ч. если
        // пир ушёл ещё до спавна — early-return ниже иначе пропустил бы чистку).
        _broadcaster?.RemoveClient(peerId);

        // Шаг 5.11 (п.30–п.32): обрыв пира посреди стыковки. Правило: сделку
        // ДО Docked отменяем полностью (AbortDock: DockApproved → false,
        // DockState → None, dock-заморозка снята — корабль снова летит по
        // инерции, как после Detach в BUG-009); Docked НЕ отменяем — запись из
        // реестра убираем, но состояние остаётся: посаженный без пилота корабль
        // стоит на Freeze до прилёта следующего (п.31). Идёт ДО early-return:
        // пир мог уйти и без слота игрока.
        if (_dockRequests.Count > 0)
        {
            var stale = new System.Collections.Generic.List<(int ShipNetId, DockState State)>();
            foreach (var (shipNetId, e) in _dockRequests)
                if (e.PeerId == peerId)
                    stale.Add((shipNetId, e.State));
            foreach (var (shipNetId, state) in stale)
            {
                if (state < DockState.Docked)
                {
                    AbortDock(shipNetId, $"пилот {peerId} отключился");
                }
                else
                {
                    _dockRequests.Remove(shipNetId);
                    DebugInfoBus.Report($"dock: пир {peerId} ушёл, ship#{shipNetId} " +
                                        "остаётся Docked (реестр очищен, состояние сохранено)");
                }
            }
        }

        if (!_peerPlayers.TryGetValue(peerId, out var player))
            return; // слота нет (например, пир отвалился до RequestSpawn)
        if (_peerKeys.TryGetValue(peerId, out var key))
            CaptureLastSeen(player, key);
        _peerPlayers.Remove(peerId);
        _peerKeys.Remove(peerId);
        _pendingAttach.Remove(peerId);
        if (GodotObject.IsInstanceValid(player) && !player.IsQueuedForDeletion())
            player.QueueFree();
    }

    /// <summary>Клиентский запрос входа в игру (после ConnectedToServer, §2.4).</summary>
    private void RequestSpawn()
    {
        if (!IsClient)
            return;
        RpcId(1L, nameof(RpcRequestSpawn), PlayerKey);
    }

    /// <summary>Сервер принимает запрос входа. Отправитель — реальный пир (id ≥ 2);
    /// сервер не спавнит «для себя» (§4 В1).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcRequestSpawn(string playerKey)
    {
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender < 2)
        {
            GD.PrintErr($"NetworkManager.RpcRequestSpawn: запрос от id = {sender} — отказ");
            return;
        }
        HandleRequestSpawn(sender, playerKey);
    }

    /// <summary>Отказ во входе — тому же пиру, а не молчание, иначе клиент зависнет
    /// в «подключён, но без игрока» (§2.4).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcSpawnDenied(string reason)
    {
        if (Multiplayer.GetRemoteSenderId() != 1)
            return; // отказ присылает только сервер
        GD.PrintErr($"NetworkManager: SpawnDenied reason = {reason} — остаюсь без игрока");
    }

    private void RpcSpawnDeniedTo(int peerId, string reason)
    {
        if (HasRealPeer)
            RpcId(peerId, nameof(RpcSpawnDenied), reason);
        else
            GD.PrintErr($"NetworkManager: SpawnDenied({reason}) пиру {peerId} (без peer — только лог)");
    }

    /// <summary>Слить отложенные крепления: как только точка (owner,point) появилась
    /// в реестре, пристегнуть ожидающего её игрока (§2.4 п.4).</summary>
    private void FlushPendingAttach(int ownerNetId, int pointNetId)
    {
        if (_pendingAttach.Count == 0)
            return;
        var ready = new List<int>();
        foreach (var (peerId, d) in _pendingAttach)
            if (d.Owner == ownerNetId && d.Point == pointNetId &&
                _peerPlayers.TryGetValue(peerId, out var p) &&
                GodotObject.IsInstanceValid(p))
            {
                p.AttachServerSide(ownerNetId, pointNetId);
                ready.Add(peerId);
            }
        foreach (var peerId in ready)
            _pendingAttach.Remove(peerId);
    }

}
