using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Autoload-сингл (res://scripts/core/NetworkManager.cs), объявлен в project.godot
/// ВЫШЕ DebugInfoBus — иначе DebugLayer при старте читает ещё не готовый синглтон.
///
/// Единственный источник правды о режиме работы процесса (решение В1: dedicated,
/// listen-server в проекте нет):
///
///   Offline    — нет peer'а; всё как на стадии 1, стадия 1 работает без изменений.
///   Dedicated  — CLI `-- --server` или кнопка `Start Server`. Процесс НЕ является
///                игроком: Player не создаётся никогда, ни сейчас, ни позже.
///   Client     — CLI `-- --join <ip>` или кнопка `Join`. Свой игрок приходит
///                репликой спавнера.
///
/// Правила, из которых код не имеет права выпадать:
///   - GetUniqueId() == 1 бывает только у серверного процесса; авторитет Player
///     никогда не равен 1 (всегда id >= 2);
///   - «мой игрок» — player.GetMultiplayerAuthority() == Multiplayer.GetUniqueId();
///   - режим определяется Role, а не наличием UI: серверная ветка не имеет права
///     знать, запущена она из CLI или кнопкой (§2.6).
/// </summary>
public partial class NetworkManager : Node
{
    public enum NetRole { Offline, Dedicated, Client }

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
    public bool IsServer => Role == NetRole.Dedicated;
    public bool IsClient => Role == NetRole.Client;
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
        DebugInfoBus.RegisterDebugButton("Start Server", () => StartServer());
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
    /// Поднять dedicated-сервер в ЭТОМ процессе (не «хост»: игрока здесь не будет, §4 В1).
    /// Имя метода — StartServer, а не Host: слово «host» в проекте означает listen-server.
    /// </summary>
    public Error StartServer(int port = Port, int maxPlayers = MaxPlayers)
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
        SetRole(NetRole.Dedicated);
        GD.Print($"NetworkManager: dedicated-сервер, self = {SelfId}, порт = {port}, " +
                 $"макс. игроков = {maxPlayers}, каналов = {ChannelCount}");
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
        Multiplayer.MultiplayerPeer?.Close();
        Multiplayer.MultiplayerPeer = null;
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
        // по playerKey слота, ПОТОМ удалять узлы. Таблицы — шаг 6 плана.
    }

    /// <summary>
    /// Клиентский запрос входа в игру. playerKey (user://identity.cfg) и таблицы
    /// peerId → Player / playerKey → LastSeenState подключаются на шаге 6 плана;
    /// сейчас это заглушка, чтобы цепочка ConnectedToServer → RequestSpawn была на месте.
    /// </summary>
    private void RequestSpawn()
    {
        if (!IsClient)
            return;
        // RpcId(1L, nameof(RequestSpawnServer), PlayerKey) появится на шаге 6.
        GD.Print("NetworkManager.RequestSpawn: заглушка (playerKey/таблицы — шаг 6)");
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
        int players = IsServer ? peers.Length : (IsClient ? 1 : 0);
        long packets = _pktsSent + _pktsRecv;
        long bytes = _bytesSent + _bytesRecv;
        return $"net: role = {Role.ToString().ToLower()}, self = {SelfId}, " +
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
