using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Autoload-сингл (res://scripts/core/NetworkManager.cs), объявлен в project.godot
/// ВЫШЕ DebugInfoBus — иначе DebugLayer при старте читает ещё не готовый синглтон.
///
/// Единственный источник правды о режиме работы процесса (решение В1,
/// пересмотрено на стадии 0-rework: сервер поднимается вместе с игроком-хостом):
///
///   Offline    — нет peer'а; всё как на стадии 1, стадия 1 работает без изменений.
///   Dedicated  — CLI `-- --server` (headless-стенды/CI). Процесс НЕ является
///                игроком: Player не создаётся никогда, ни сейчас, ни позже.
///   Host       — кнопка `Start Server`: сервер в этом процессе + локальный игрок
///                с авторитетом 1 (= id сервера). Хост и есть сервер.
///   Client     — CLI `-- --join <ip>` или кнопка `Join`. Свой игрок приходит
///                репликой спавнера.
///
/// Правила, из которых код не имеет права выпадать:
///   - GetUniqueId() == 1 бывает только у серверного процесса; авторитет Player
///     равен 1 только у игрока хоста; чужие игроки — всегда id >= 2 (§2.4);
///   - «мой игрок» — player.GetMultiplayerAuthority() == LocalAuthorityId
///     (для хоста 1 == 1, для клиента — свой id);
///   - режим определяется Role, а не наличием UI: серверная ветка не имеет права
///     знать, запущена она из CLI или кнопкой (§2.6).
/// </summary>
public partial class NetworkManager : Node
{
    public enum NetRole { Offline, Dedicated, Host, Client }

    // ------------------------------------------------------------- константы
    public const int Port = 24567;
    public const int MaxPlayers = 8;
    public const int ReplicationHz = 30;

    /// <summary>Три канала ENet: 0 = снимки (unreliable), 1 = ввод/события (reliable),
    /// 2 = редкие крупные (спавн-данные). См. §2.1.</summary>
    public const int ChannelCount = 3;

    /// <summary>Радиус свободной сферы при выборе стартовой точки (§3.6).</summary>
    public const float SpawnClearance = 2.5f;

    /// <summary>Частота валидации/репорта NetState (не картинки), §2.9.</summary>
    public const int ReportHz = 15;

    // -------------------------------------------------------------- синглтон
    public static NetworkManager? Instance { get; private set; }

    // ------------------------------------------------------------- состояние
    public NetRole Role { get; private set; } = NetRole.Offline;

    /// <summary>Ловушка 4.x, о которой нельзя забывать: у `SceneTree` по умолчанию
    /// уже стоит `OfflineMultiplayerPeer`, поэтому `Multiplayer.HasMultiplayerPeer()`
    /// и `Multiplayer.IsServer()` возвращают `true` даже в offline. Отличить
    /// настоящее соединение можно только по типу peer'а.</summary>
    public bool HasRealPeer =>
        Multiplayer.MultiplayerPeer != null && Multiplayer.MultiplayerPeer is not OfflineMultiplayerPeer;

    /// <summary>Источник истины о роли — сам `Role`, а не `Multiplayer.IsServer()`:
    /// offline-peer тоже «сервер» (id = 1), и на этом различии строить нельзя (§2.1).</summary>
    public bool IsServer => Role == NetRole.Dedicated || Role == NetRole.Host;
    public bool IsClient => Role == NetRole.Client;
    /// <summary>Сервер с локальным игроком (кнопка `Start Server`, стадия 0-rework):
    /// серверная ветка + свой Player с авторитетом 1.</summary>
    public bool IsHost => Role == NetRole.Host;
    public bool IsActive => HasRealPeer;

    /// <summary>Локальный id авторитета: в Offline = 1 (то же значение, что у
    /// сервера, поэтому режимы различают по `Role`, а не по номеру, §2.5 п.5).
    /// GetUniqueId() при снятом peer (после Disconnect MultiplayerPeer = null)
    /// печатает ERROR «No multiplayer peer» — берём 1 напрямую. Тот же id нужен
    /// и Player/Ship для проверки авторитета вместо встроенного
    /// IsMultiplayerAuthority(), который зовёт GetUniqueId() внутри.</summary>
    public int LocalAuthorityId =>
        Multiplayer.HasMultiplayerPeer() ? Multiplayer.GetUniqueId() : 1;

    public int SelfId => LocalAuthorityId;

    /// <summary>Последняя ошибка запуска соединения (для панели: «port busy?»).</summary>
    public Error LastError { get; private set; } = Error.Ok;

    /// <summary>Смена роли (Offline ↔ Dedicated ↔ Client). Подписчики перестраивают
    /// своё содержимое: DebugLayer — кнопки, мир — одиночного Player.</summary>
    public event Action<NetRole, NetRole>? RoleChanged;

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
        if (Role != NetRole.Offline || state.ShipNetId != 0)
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
    /// (AssignShipAuthority, репорт NetState) адресуются по ShipNetId, а не по
    /// NodePath: пути спавненых узлов расходятся между пирами.</summary>
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
    /// инвариант проверяется и как рантайм-гейт в AssignShipAuthority, и assert'ом
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

    // ------------------------------------------- авторитет кораблей (§4 В3 п.3)

    /// <summary>Отложенные назначения авторитета: shipNetId → peerId. RPC
    /// AssignShipAuthority может прийти раньше, чем спавнер создаст у пира узел
    /// корабля; пир кладёт пару сюда и применяет в Ship._Ready (§4 В4 п.2).</summary>
    private readonly Dictionary<int, int> _pendingAuthority = new();

    /// <summary>Шов для автотестов (§3.9 п.15): дополнительные «существующие» пиры,
    /// которых нет в реальном соединении. Живой сети в headless-тесте нет
    /// (ENet-пиры в одном процессе к себе не подключаются, §3.10 шаг 6), а assert
    /// про смену авторитета на id=2 требует, чтобы валидация пира его пропускала.
    /// Пустое множество = боевое поведение: только реальные GetPeers().</summary>
    public static readonly System.Collections.Generic.HashSet<int> TestExtraPeers = new();

    public void AssignShipAuthority(int shipNetId, int peerId)
    {
        if (!IsServer)
        {
            GD.PrintErr($"NetworkManager.AssignShipAuthority({shipNetId}, {peerId}): " +
                        "авторитет назначает только сервер");
            return;
        }
        if (peerId == 0 || (peerId != 1 &&
                            System.Array.IndexOf(Multiplayer.GetPeers(), peerId) < 0 &&
                            !TestExtraPeers.Contains(peerId)))
        {
            GD.PrintErr($"NetworkManager.AssignShipAuthority: пир {peerId} не существует — отказ");
            return;
        }

        // §3.9 п.17 (рантайм-гейт): пилот уже пилотирует другой корабль — сначала
        // освобождаем прежний, иначе между двумя назначениями висит состояние
        // «один пир — два авторитета», и он шлёт импульсы за оба корабля.
        if (peerId != 1)
            foreach (var other in _ships.Values)
                if (GodotObject.IsInstanceValid(other) && other.Net != null &&
                    other.Net.ShipNetId != shipNetId && other.PilotPeerId == peerId)
                {
                    GD.Print($"NetworkManager: пир {peerId} уже пилотирует " +
                             $"ship#{other.Net.ShipNetId} — освобождаю прежний (§3.9 п.17)");
                    ReleaseShipAuthority(other.Net.ShipNetId);
                }

        _pendingAuthority[shipNetId] = peerId; // и для себя: запись единого источника правды

        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyRemoteAuthority(peerId);
        else
            GD.PrintErr($"NetworkManager.AssignShipAuthority({shipNetId}, {peerId}): " +
                        "корабля с таким адресом на сервере нет — назначение отложено");

        if (HasRealPeer)
            Rpc(nameof(RpcApplyShipAuthority), shipNetId, peerId);
    }

    /// <summary>Освободить корабль от пилота (автопилот, peerId = 1) без рассылки
    /// (рассылка — внутри AssignShipAuthority). Отдельный метод нужен, чтобы
    /// освобождение прежнего корабля не запускало рекурсивный обход.</summary>
    private void ReleaseShipAuthority(int shipNetId)
    {
        _pendingAuthority[shipNetId] = 1;
        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyRemoteAuthority(1);
        if (HasRealPeer)
            Rpc(nameof(RpcApplyShipAuthority), shipNetId, 1);
    }

    /// <summary>Серверная рассылка нового авторитета. Едет через autoload, а не через
    /// узел Ship: у пира узла ещё может не быть (спавнер асинхронный), и RPC по пути
    /// к несуществующему узлу теряется без следа (§4 В4 п.2).</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcApplyShipAuthority(int shipNetId, int peerId)
    {
        if (Multiplayer.GetRemoteSenderId() != 1)
            return; // рассылку авторитета делает только сервер (id = 1)

        _pendingAuthority[shipNetId] = peerId;
        var ship = FindShip(shipNetId);
        if (ship != null)
            ship.ApplyRemoteAuthority(peerId);
        // иначе — применится в Ship._Ready через ConsumePendingAuthority
    }

    /// <summary>Последний известный авторитет корабля по адресу (0 = не назначался).
    /// Нужен, чтобы пир, создавший узел позже RPC, не остался с авторитетом 1.</summary>
    public bool TryGetPendingAuthority(int shipNetId, out int peerId) =>
        _pendingAuthority.TryGetValue(shipNetId, out peerId);

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
        return player;
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

    // ------------------------------------------------------------- запуск
    public override void _EnterTree()
    {
        Instance = this;
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    public override void _Ready()
    {
        SubscribeSignals();
        RegisterDebugEntries();
        ApplyCmdlineArgs();
    }

    /// <summary>
    /// Кнопки сети и строка `Net` в дебаг-панели (§3.7). Регистрирует их сам
    /// NetworkManager через DebugInfoBus: DebugLayer про сети ничего не знает,
    /// а в headless-сервере панели нет вовсе — источник и кнопки просто копятся
    /// в очереди шины и никогда не появятся.
    ///
    /// Кнопки ровно три, `Host` нет: listen-server в проекте отсутствует (§4 В1),
    /// и слово «host» означает именно его. `Start Server` поднимает dedicated в
    /// ЭТОМ процессе (В13=A), `Join` — второй процесс к тому же порту, в одном
    /// окне сыграть с собой нельзя: peer один (§4 В1 «цена»).
    /// </summary>
    private void RegisterDebugEntries()
    {
        DebugInfoBus.RegisterDebugButton("Start Server", () => StartServer(withLocalPlayer: true));
        DebugInfoBus.RegisterDebugButton("Join", () => Join("127.0.0.1"));
        DebugInfoBus.RegisterDebugButton("Disconnect", () => Disconnect());
        DebugInfoBus.RegisterInfoSource("Net", GetNetInfoLine);
    }

    /// <summary>Подписка на события соединения. В 4.7 они висят на MultiplayerApi,
    /// у MultiplayerPeer свои дублировать не нужно (§2.2).</summary>
    private void SubscribeSignals()
    {
        Multiplayer.ConnectedToServer += OnConnectedToServer;
        Multiplayer.ConnectionFailed += OnConnectionFailed;
        Multiplayer.ServerDisconnected += OnServerDisconnected;
        Multiplayer.PeerConnected += OnPeerConnected;
        Multiplayer.PeerDisconnected += OnPeerDisconnected;
    }

    /// <summary>
    /// Разбор пользовательских аргументов: строго после разделителя `--`
    /// (флаги до `--` движок съедает сам, и процесс «молча не сервер» — риск §3.11 п.7).
    /// Поддерживаются: --server, --join &lt;ip&gt;, --port &lt;n&gt;.
    /// </summary>
    private void ApplyCmdlineArgs()
    {
        var args = OS.GetCmdlineUserArgs();
        string? joinIp = null;
        int port = Port;
        bool server = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--server":
                    server = true;
                    break;
                case "--join":
                    if (i + 1 < args.Length)
                        joinIp = args[++i];
                    else
                        GD.PrintErr("NetworkManager: после --join ожидается адрес");
                    break;
                case "--port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int p))
                        port = p;
                    break;
            }
        }

        if (server)
        {
            var err = StartServer(port);
            // В headless-стенде неудача StartServer обязана завершать процесс:
            // иначе в скрипте висит процесс без сервера (§2.1).
            if (err != Error.Ok)
                AbortIfHeadlessCli();
        }
        else if (joinIp != null)
        {
            Join(joinIp, port);
        }
    }

    /// <summary>Неудача запуска сервера в headless-CLI (стенд/CI) фатальна: процесс
    /// завершается с кодом 1, иначе в скрипте висит процесс без сервера (§2.1).
    /// В оконном режиме (кнопка Start Server) процесс живёт дальше — об этом скажет
    /// LastError в панели.</summary>
    private void AbortIfHeadlessCli()
    {
        if (!DisplayServer.GetName().ToLowerInvariant().Contains("headless"))
            return;
        GD.PrintErr($"NetworkManager: StartServer не удалось ({LastError}) — порт {Port} занят? " +
                    "Завершаю процесс с кодом 1.");
        GetTree().Quit(1);
    }

    /// <summary>
    /// Поднять сервер в ЭТОМ процессе. `withLocalPlayer` решает роль:
    ///   false → Dedicated (CLI `--server`, headless/CI): игрока в процессе нет;
    ///   true  → Host (кнопка `Start Server`, стадия 0-rework): сервер + локальный
    ///           игрок с авторитетом 1. Игрок спавнится через RoleChanged в
    ///           WorldSpawner (capture LastSeen → remove offline → SpawnHostPlayer).
    /// Имя метода — StartServer, а не Host: «host» в проекте означает listen-server.
    /// </summary>
    public Error StartServer(int port = Port, int maxPlayers = MaxPlayers,
                             bool withLocalPlayer = false)
    {
        if (HasRealPeer)
        {
            LastError = Error.AlreadyInUse;
            GD.PrintErr("NetworkManager.StartServer: peer уже активен — нужен Disconnect() сначала");
            return Error.AlreadyInUse;
        }

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateServer(port, maxPlayers, ChannelCount, 0, 0);
        if (err != Error.Ok)
        {
            LastError = err;
            GD.PrintErr($"NetworkManager.StartServer: CreateServer({port}) -> {err} (порт занят?)");
            return err;
        }

        Multiplayer.MultiplayerPeer = peer;
        SetRole(withLocalPlayer ? NetRole.Host : NetRole.Dedicated);
        GD.Print($"NetworkManager: {(withLocalPlayer ? "host" : "dedicated")}-сервер, " +
                 $"self = {SelfId}, порт = {port}, макс. игроков = {maxPlayers}, " +
                 $"каналов = {ChannelCount}");
        OnServerUp();
        return Error.Ok;
    }

    /// <summary>Подключиться к серверу как обычный клиент (тот же путь и у того,
    /// кто поднял сервер — он играет вторым процессом, §4 В1).</summary>
    public Error Join(string ip, int port = Port)
    {
        if (HasRealPeer)
        {
            LastError = Error.AlreadyInUse;
            GD.PrintErr("NetworkManager.Join: peer уже активен — нужен Disconnect() сначала");
            return Error.AlreadyInUse;
        }

        var peer = new ENetMultiplayerPeer();
        var err = peer.CreateClient(ip, port, ChannelCount, 0, 0, 0);
        if (err != Error.Ok)
        {
            LastError = err;
            GD.PrintErr($"NetworkManager.Join: CreateClient({ip}:{port}) -> {err}");
            return err;
        }

        Multiplayer.MultiplayerPeer = peer;
        // Роль становится Client только после ConnectedToServer; пока идёт рукопожатие,
        // peer уже активен, но игрока ещё нет — переходное состояние (§3.7).
        SetRole(NetRole.Client);
        GD.Print($"NetworkManager: подключаюсь к {ip}:{port}, self = {SelfId}");
        return Error.Ok;
    }

    /// <summary>Разорвать соединение и вернуться в Offline (assert §3.9 п.5).</summary>
    public void Disconnect()
    {
        if (!Multiplayer.HasMultiplayerPeer())
            return;
        bool wasHost = IsHost;
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
        // Хостовый слот 1: узел игрока остаётся в мире и становится offline-игроком
        // (FindOfflinePlayer найдёт его по авторитету 1), но сетевые таблицы слота 1
        // чистим — иначе коллизия ключа/идемпотентность примут offline-игрока за
        // сетевого игрока хоста (стадия 0-rework).
        if (wasHost)
        {
            _peerPlayers.Remove(1);
            _peerKeys.Remove(1);
            _pendingAttach.Remove(1);
        }
        SetRole(NetRole.Offline);
        GD.Print("NetworkManager: соединение закрыто, роль = Offline");
    }

    private void SetRole(NetRole next)
    {
        if (Role == next)
            return;
        var prev = Role;
        Role = next;
        // Счётчики относятся к ЗАКРЫТОМУ peer'у — на новой роли они обнулены,
        // иначе панель показывает пакеты уже несуществующего соединения (§2.7).
        ResetTrafficStats();
        DebugInfoBus.Report($"role: {prev.ToString().ToLower()} -> {next.ToString().ToLower()}, self = {SelfId}");
        RoleChanged?.Invoke(prev, next);
    }

    /// <summary>Хук: сервер поднят. Спавн мира (корабли раньше игроков, §2.4)
    /// подключается на шаге 4 плана; здесь только лог-заглушка.</summary>
    protected virtual void OnServerUp()
    {
        GD.Print("NetworkManager: мир сервера готов (спавн объектов мира — шаг 4 плана)");
    }

    // ------------------------------------------------------ события соединения

    // Клиент: соединение с сервером установлено. Именно этот сигнал, а не
    // PeerConnected: у клиента пир-сервер в список пиров не приходит (§2.2).
    private void OnConnectedToServer()
    {
        GD.Print($"NetworkManager: ConnectedToServer (self = {SelfId}) — запрошу спавн (шаг 6 плана)");
        RequestSpawn();
    }

    private void OnConnectionFailed()
    {
        GD.PrintErr("NetworkManager: ConnectionFailed — возвращаемся в Offline");
        Multiplayer.MultiplayerPeer = null;
        SetRole(NetRole.Offline);
    }

    private void OnServerDisconnected()
    {
        // Не то же самое, что ConnectionFailed: соединение было установлено.
        GD.PrintErr("NetworkManager: сервер пропал — возвращаемся в Offline");
        Multiplayer.MultiplayerPeer = null;
        SetRole(NetRole.Offline);
    }

    // Сервер: пир подключился. Слот резервируется здесь, спавн — по RequestSpawn (§2.4).
    private void OnPeerConnected(long id)
    {
        GD.Print($"NetworkManager: PeerConnected id = {id}, пиров = {Multiplayer.GetPeers().Length}");
    }

    private void OnPeerDisconnected(long id)
    {
        GD.Print($"NetworkManager: PeerDisconnected id = {id}");
        // Порядок внутри обработчика жёсткий (§2.4): сначала записать LastSeenState
        // по playerKey слота, ПОТОМ удалять узлы.
        OnPeerLeft((int)id);
    }

    // ------------------------------------------------------------- диагностика

    // ------------------------------------------------- счётчики трафика (§2.7)
    //
    // Хостовые счётчики ENet забираются И ОБНУЛЯЮТСЯ вызовом PopStatistic, поэтому
    // читать их можно ровно раз в интервал пересчёта (1 с), иначе часть пакетов
    // потеряется из вида. Значения ниже — «за последнюю секунду».

    private long _pktsSent, _pktsRecv, _bytesSent, _bytesRecv;

    private void UpdateTrafficStats()
    {
        if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet || enet.Host == null)
        {
            ResetTrafficStats();
            return;
        }
        var host = enet.Host;
        // PopStatistic возвращает double и обнуляет счётчик — читаем ровно раз в интервал.
        _pktsSent = (long)host.PopStatistic(ENetConnection.HostStatistic.SentPackets);
        _pktsRecv = (long)host.PopStatistic(ENetConnection.HostStatistic.ReceivedPackets);
        _bytesSent = (long)host.PopStatistic(ENetConnection.HostStatistic.SentData);
        _bytesRecv = (long)host.PopStatistic(ENetConnection.HostStatistic.ReceivedData);
    }

    private void ResetTrafficStats()
    {
        _pktsSent = _pktsRecv = _bytesSent = _bytesRecv = 0;
    }

    /// <summary>RTT по каждому пиру, мс (ENetPacketPeer.GetStatistic, §2.7).
    /// У клиента пир-сервер в GetPeers() не состоит, но соединение с id = 1 есть —
    /// его спрашиваем отдельно.</summary>
    private string BuildRttString()
    {
        if (Multiplayer.MultiplayerPeer is not ENetMultiplayerPeer enet)
            return "rtt = -";
        var parts = new List<string>();
        foreach (long id in Multiplayer.GetPeers())
            AppendRtt(parts, enet, (int)id);
        if (IsClient)
            AppendRtt(parts, enet, 1);
        return parts.Count == 0 ? "rtt = -" : "rtt = " + string.Join(",", parts);
    }

    private static void AppendRtt(List<string> parts, ENetMultiplayerPeer enet, int id)
    {
        var peer = enet.GetPeer(id);
        if (peer == null)
            return;
        double rtt = peer.GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime);
        parts.Add($"{id}={rtt:F0}ms");
    }

    /// <summary>Строка Net для панели и stdout (§2.7): роль, self, пиры, число
    /// игроков, пакеты/с и байты/с (порог плана — < 50 пакетов/с на игрока), RTT.</summary>
    public string GetNetInfoLine()
    {
        var peers = Multiplayer.GetPeers();
        // На сервере пиры = подключившиеся клиенты; у хоста ещё свой игрок (авторитет 1),
        // он не входит в GetPeers() (стадия 0-rework).
        int players = IsServer ? peers.Length + (IsHost ? 1 : 0) : (IsClient ? 1 : 0);
        long packets = _pktsSent + _pktsRecv;
        long bytes = _bytesSent + _bytesRecv;
        string key = PlayerKey;
        return $"net: role = {Role.ToString().ToLower()}, self = {SelfId}, " +
               $"key = {key[..System.Math.Min(8, key.Length)]}, " +
               $"peers = [{string.Join(",", peers)}], players = {players}, " +
               $"pkts/s = {packets} (tx {_pktsSent}/rx {_pktsRecv}), " +
               $"KB/s = {bytes / 1024f:F1}, {BuildRttString()}, err = {LastError}";
    }

    /// <summary>Пересчёт счётчиков и серверная строка — раз в секунду, не в каждом
    /// кадре (§2.7). В headless-CLI сервере DebugLayer нет — та же строка уходит в
    /// stdout; при запуске сервера кнопкой печать остаётся, чтобы прогон одинаково
    /// читался и в окне, и в CI-логе.</summary>
    private float _statsTimer;
    public override void _Process(double delta)
    {
        _statsTimer += (float)delta;
        if (_statsTimer < 1f)
            return;
        _statsTimer = 0f;

        UpdateTrafficStats();
        if (Role == NetRole.Dedicated)
            GD.Print(GetNetInfoLine());
    }
}
