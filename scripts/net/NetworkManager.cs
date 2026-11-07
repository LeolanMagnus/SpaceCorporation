using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Autoload-сингл (res://scripts/net/NetworkManager.cs), объявлен в project.godot
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

    /// <summary>Искусственная задержка, мс (шов `TestLatencyMs`, §2.6). Ставится из
    /// CLI `-- --latency N` или напрямую. Каждый процесс задерживает исходящее
    /// (ввод пилота, снимки `NetState`) на столько миллисекунд, поэтому эффективный
    /// RTT ≈ 2×latency. 0 = без задержки (дефолт, автотесты не деградируют).</summary>
    public static int TestLatencyMs { get; set; }

    /// <summary>Автопилот для headless-стенда (§2.6): если включён (CLI `--autopilot`),
    /// пилот-клиент сам генерирует осмысленный ввод (тяга вперёд + периодический рыск),
    /// потому что в headless никто не жмёт WASD. Без него `Prediction Error` и число
    /// откатов в логе стенда всегда нулевые — метрики не измеряются.</summary>
    public static bool Autopilot { get; set; }

    /// <summary>Шаг 4.9: god mode через CLI (headless/dedicated, где дебаг-панели
    /// нет): `-- --god` ставит флаг, и авторитетный Ship применяет его через
    /// единственный серверный метод ShipHealthState.SetGodModeServerOnly (п.24).
    /// С клиента god mode не включается: RPC нет вовсе.</summary>
    public static bool GodModeCli { get; set; }

    /// <summary>Три канала ENet: 0 = снимки (unreliable), 1 = ввод/события (reliable),
    /// 2 = редкие крупные (спавн-данные). См. §2.1.</summary>
    public const int ChannelCount = 3;

    /// <summary>Радиус свободной сферы при выборе стартовой точки (§3.6).</summary>
    public const float SpawnClearance = 2.5f;

    /// <summary>Частота валидации/репорта NetState (не картинки), §2.9.
    /// С шага R4 основной путь — батч SnapshotBroadcaster; репорт остаётся
    /// только аварийным каналом реконструкции после пропадания батча (шаг R5),
    /// поэтому частота снижена 15 → 4 Гц (фактически репорт больше не шлётся
    /// по таймеру — реконструкцию инициирует клиент запросом RpcRequestFullSnapshot).</summary>
    public const int ReportHz = 4;

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

    /// <summary>Авторитетная сторона процесса: сервер (Dedicated/Host) ИЛИ offline
    /// (одиночная игра, где локальный процесс — он же сервер, SelfId = 1).
    /// BUG-008/009: серверную логику (назначение пилота, крепление) выполняем
    /// и в offline — раньше гейк был чистым `IsServer`, и в offline PilotPeerId
    /// никогда не менялся: все корабли оставались с пилотом-автопилотом 1.</summary>
    public bool IsAuthoritySide => IsServer || Role == NetRole.Offline;

    /// <summary>Кому принадлежит свободный (никто не пристёгнут) корабль:
    /// в сети — автопилоту сервера (peerId = 1), в offline — «никому» (0),
    /// иначе SelfId = 1 снова сделал бы каждый корабль «локальным пилотом»
    /// единственного игрока (BUG-008/009).</summary>
    public int FreeShipPilotId => Role == NetRole.Offline ? 0 : 1;

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
        // §2.4 (R4): вещатель батчей живёт под autoload, чтобы путь узла
        // `/NetworkManager/SnapshotBroadcaster` совпадал на сервере и клиенте
        // (RPC по имени метода резолвятся по пути узла, §4 В4 п.3). Сам узел
        // решает, работать или молчать, по роли в `_Process`.
        _broadcaster = new SnapshotBroadcaster { Name = "SnapshotBroadcaster" };
        AddChild(_broadcaster);
    }

    private SnapshotBroadcaster? _broadcaster;

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

        // §3.7 п.26: строка Fire: shots/hits/rejected/targets. Источник вешается на
        // NetworkManager (как «Net»), а не на отдельный Ship: счётчики выстрелов
        // живут на каждом корабле-авторитете (AcceptedFireCount / HitsRegistered /
        // RejectedFireCount), а строка должна быть ОДНА на сервер — иначе при N
        // кораблях в панели появятся N строк (риск №6). Источник регистрируется
        // всегда (синглтон живёт в любой роли, как и «Net»), но смысл строке дают
        // серверные реестры: на клиенте корабли — не авторитет, их счётчики нули.
        DebugInfoBus.RegisterInfoSource("fire", GetFireInfoLine);
    }

    /// <summary>Строка Fire: §3.7. Агрегация серверных счётчиков выстрелов по всем
    /// кораблям-авторитетам в реестре + число живых мишеней. Формат
    /// `Fire: shots=… hits=… rejected=… targets=…` — по образцу В17. Счётчик
    /// выстрелов (shots) — принятые (AcceptedFireCount), отклонённые отдельно
    /// (rejected), попадания (hits) — только те, что прошли хитскан по цели.</summary>
    public string GetFireInfoLine()
    {
        int shots = 0, hits = 0, rejected = 0;
        foreach (var (_, ship) in _ships)
        {
            if (ship == null || !GodotObject.IsInstanceValid(ship))
                continue;
            shots += ship.AcceptedFireCount;
            hits += ship.HitsRegistered;
            rejected += ship.RejectedFireCount;
        }
        int targets = 0;
        foreach (var (_, t) in _targets)
            if (t != null && GodotObject.IsInstanceValid(t) && !t.IsQueuedForDeletion())
                targets++;
        return $"Fire: shots={shots} hits={hits} rejected={rejected} targets={targets}";
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
                case "--latency":
                    // Искусственная задержка, мс (шов §2.6). Применяется к каждому
                    // процессу (и сервер, и клиент задерживают исходящее), поэтому
                    // эффективный RTT ≈ 2×latency.
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int lat))
                        TestLatencyMs = lat;
                    break;
                case "--autopilot":
                    // Пилот-клиент сам жмёт тягу/рыск (headless-стенд §2.6).
                    Autopilot = true;
                    break;
                case "--god":
                    // Шаг 4.9: god mode через CLI для процесса без дебаг-панели
                    // (dedicated/headless). Флаг читает авторитетный Ship в _Ready
                    // и включает god mode серверным методом (п.24).
                    GodModeCli = true;
                    break;
                case "--key":
                    // Явный playerKey для headless-стенда (§2.6): несколько клиентов
                    // в одном прогоне читают общий user://identity.cfg и получили бы
                    // один ключ → вытесняли бы друг друга на сервере. Каждому стенду
                    // даём уникальный ключ, чтобы они сосуществовали как разные игроки.
                    if (i + 1 < args.Length)
                        _playerKey = args[++i];
                    else
                        GD.PrintErr("NetworkManager: после --key ожидается значение");
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
        // BUG-008/009: перераспределение свободных кораблей между «никому» (0)
        // и автопилотом (1) при смене роли — до обработчиков RoleChanged.
        RebalancePilotsForRole(prev, next);
        DebugInfoBus.Report($"role: {prev.ToString().ToLower()} -> {next.ToString().ToLower()}, self = {SelfId}");
        RoleChanged?.Invoke(prev, next);
    }

    /// <summary>BUG-008/009: при смене роли сверяем пилотов свободных кораблей:
    /// «никому» (0) имеет смысл только в offline, автопилот (1) — только в сети.
    /// Offline → сеть: свободные (0) уходят автопилоту (1), иначе тесты п.15/п.17
    /// видят PilotPeerId = 0 там, где ждут 1. Host → Offline: автопилот (1) у
    /// корабля, в котором НЕ сидит локальный игрок хоста (авторитет 1), гасится
    /// до «никого» (0): SelfId = 1 иначе вернул бы удалённый доступ к WASD
    /// (BUG-008). Client → Offline: узлы кораблей были репликами (авторитет чужого
    /// сервера) — локальная подмена пилота не нужна, права разберёт ApplyAuthority
    /// при пересборе.</summary>
    private void RebalancePilotsForRole(NetRole prev, NetRole next)
    {
        if (prev == NetRole.Offline && next != NetRole.Offline)
        {
            foreach (var s in _ships.Values)
                if (GodotObject.IsInstanceValid(s) && s.Net != null && s.PilotPeerId == 0)
                    SetPilot(s.Net.ShipNetId, 1);
        }
        else if (next == NetRole.Offline && (prev == NetRole.Host || prev == NetRole.Dedicated))
        {
            // Корабль, в котором сидит локальный игрок (авторитет 1), — под пилотом 1,
            // остальные автопилоты (1) становятся свободными (0).
            int seatedShipNetId = 0;
            foreach (var node in GetTree().GetNodesInGroup("player"))
                if (node is Player p && GodotObject.IsInstanceValid(p) &&
                    !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1 &&
                    p.IsAttached)
                {
                    seatedShipNetId = p.AttachedTo!.OwnerNetId;
                    break;
                }
            foreach (var s in _ships.Values)
                if (GodotObject.IsInstanceValid(s) && s.Net != null &&
                    s.PilotPeerId == 1 && s.Net.ShipNetId != seatedShipNetId)
                    SetPilot(s.Net.ShipNetId, 0);
        }
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
        // §2.4 (R4): вещатель батчей ведёт список адресатов с этого момента;
        // до спавна клиент получает все тиры с якорем в начале мира.
        if (IsServer && id >= 2)
            _broadcaster?.AddClient((int)id);
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
        // В headless-стенде (§2.6) печатаем трафик/RTT в любом процессе, не только
        // на Dedicated-сервере: критерий (г) «< 50 пакетов/с на игрока» меряется и
        // на клиенте, а в окне (не headless) строка и так идёт в дебаг-панель.
        if (Role == NetRole.Dedicated || IsHeadlessCli())
            GD.Print(GetNetInfoLine());
    }

    /// <summary>Запущены ли в headless-CLI (стенд/CI), а не просто без окна.</summary>
    public static bool IsHeadlessCli() =>
        DisplayServer.GetName().ToLowerInvariant().Contains("headless");
}
