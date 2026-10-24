using Godot;

namespace SpaceCorporation;

/// <summary>
/// Скрипт узла `Main/GameRoot` (шаг 4 переделки стадии 1, §3.6): корабль и игрок
/// больше НЕ лежат в Main.tscn — их создаёт код, и тот же код в offline.
///
/// Почему так: если `Ship` инстанцирован в сцене, он существует в каждом процессе
/// отдельно (свой InstanceId у каждого), и `MultiplayerSynchronizer` не понимает,
/// чей это корабль. Спавн через `MultiplayerSpawner` + `AddSpawnableScene`
/// создаёт узел на сервере и повторяет его у всех пиров (§2.4).
///
/// Порядок, обязательный по §2.4: сначала объекты мира (корабли), потом игроки —
/// игрок, пристёгнутый к кораблю, ссылается на точку через (OwnerNetId, PointNetId),
/// и без заспавненного корабля этого реестра нет.
///
/// Роли (§2.1):
///   - Offline   — «single-player world»: тот же путь спавна, авторитет = 1;
///   - Dedicated — корабли есть, игроков нет (игрок появится по RequestSpawn, шаг 6);
///   - Client    — сам ничего не спавнит: узлы приходят через MultiplayerSpawner.
/// </summary>
public partial class WorldSpawner : Node3D
{
    /// <summary>Активный спавнер сцены (в проекте он один: один Main за раз).</summary>
    public static WorldSpawner? Current { get; private set; }

    /// <summary>Сцена, в которую спавнятся игроки (мировой корень Detach).
    /// Это верхний Node3D-предок GameRoot (в игре — Main), а НЕ
    /// GetTree().CurrentScene: в тестах (Stage1Test.tscn) текущей сценой является
    /// обёртка-Node, и CurrentScene as Node3D давал null — SpawnPoints не
    /// находились, корабли не спавнились (§3.5 п.1, тот же приём, что в Player).</summary>
    public Node3D? WorldRoot => FindWorldRoot();

    private Node3D? FindWorldRoot()
    {
        Node3D? top = null;
        for (var n = GetParent(); n != null; n = n.GetParent())
            if (n is Node3D n3)
                top = n3;
        return top;
    }

    private MultiplayerSpawner? _spawner;
    private bool _worldSpawned;

    public override void _Ready()
    {
        Current = this;

        _spawner = GetNodeOrNull<MultiplayerSpawner>("Spawner");
        if (_spawner != null)
        {
            // Спавнер повторяет узлы у всех пиров; пути — res://, а не пути дерева.
            _spawner.AddSpawnableScene("res://scenes/Ship.tscn");
            _spawner.AddSpawnableScene("res://scenes/Player.tscn");
        }
        else
        {
            GD.PushError("WorldSpawner: у GameRoot нет дочернего MultiplayerSpawner " +
                         "'Spawner' — в сети корабли и игроки не доедут до клиентов (§3.6).");
        }

        var nm = NetworkManager.Instance;
        if (nm != null)
        {
            nm.RoleChanged += OnRoleChanged;
            // Dedicated из CLI: StartServer отработал в _Ready autoload'а, ДОReady этой
            // сцены, — сигнал RoleChanged уже прошёл, мир спавним отсюда.
            if (nm.IsServer)
                SpawnWorld();
        }

        // Offline (в т.ч. тесты стадии 1): мир из одного процесса — корабли + игрок.
        if (nm == null || nm.Role == NetworkManager.NetRole.Offline)
            StartSinglePlayerWorld();
    }

    public override void _ExitTree()
    {
        if (ReferenceEquals(Current, this))
            Current = null;
        if (NetworkManager.Instance is { } nm)
            nm.RoleChanged -= OnRoleChanged;
    }

    /// <summary>Переходы ролей через кнопки панели (§3.11 п.10): Offline→Dedicated
    /// убирает одиночного игрока (в dedicated его быть не может), Disconnect
    /// возвращает. Корабли при этом не пересоздаются: они уже с адресом и реестром.</summary>
    private void OnRoleChanged(NetworkManager.NetRole prev, NetworkManager.NetRole next)
    {
        if (next != NetworkManager.NetRole.Offline)
        {
            // Уход из offline в сеть: одиночный игрок с авторитетом 1 удаляется —
            // в сети авторитет игрока всегда ≥ 2 (§2.4), оставить его = «игрок-призрак».
            RemoveOfflinePlayer();
            if (next == NetworkManager.NetRole.Dedicated)
                SpawnWorld();
        }
        else if (prev != NetworkManager.NetRole.Offline)
        {
            // Disconnect: возвращаем одиночного игрока в offline-мир (§3.9 п.5).
            StartSinglePlayerWorld();
        }
    }

    private void RemoveOfflinePlayer()
    {
        if (WorldRoot == null)
            return;
        foreach (var node in WorldRoot.GetChildren())
            if (node is Player p && p.GetMultiplayerAuthority() == 1)
                p.QueueFree();
    }

    /// <summary>Спавн объектов мира (только сервер и offline). Идемпотентен:
    /// повторный вызов не создаёт второй мир.</summary>
    public void SpawnWorld()
    {
        if (_worldSpawned)
            return;
        _worldSpawned = true;

        // §4 В4 = B: в мире несколько кораблей. Первый — на первой стартовой
        // точке, второй — на второй (если она есть). Имена разные, чтобы пути
        // (`GameRoot/Ship`, `GameRoot/Ship2`) не совпадали: путь не должен быть
        // идентификатором, но пересечение путей ломает отладку (§3.9 п.15).
        SpawnShipAtSpawnPoint(0, "Ship");
        SpawnShipAtSpawnPoint(1, "Ship2");
        GD.Print("WorldSpawner: мир заспавнен (корабли раньше игроков, §2.4)");
    }

    /// <summary>Заспавнить корабль на стартовой точке с указанным индексом
    /// (`SpawnPoints/Spawn<N>` в Main). Если точки с таким индексом нет — корабль
    /// не спавнится: второй корабль опционален, сцена может дать одну точку.</summary>
    private void SpawnShipAtSpawnPoint(int index, string nameOverride)
    {
        var marker = WorldRoot?.GetNodeOrNull<Marker3D>($"SpawnPoints/Spawn{index}");
        if (marker == null)
        {
            GD.PushWarning($"WorldSpawner: стартовая точка SpawnPoints/Spawn{index} " +
                           $"не найдена — корабль «{nameOverride}» не заспавнен");
            return;
        }
        SpawnShipFromScene(marker.GlobalPosition, nameOverride);
    }

    /// <summary>Спавн одного корабля: адрес выдаётся ДО AddChild — иначе первый
    /// снимок реплики уйдёт с ShipNetId = 0, и RPC корабля потеряются (§3.1 п.8).</summary>
    public Ship? SpawnShipFromScene(Vector3? position = null, string? nameOverride = null)
    {
        var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (scene == null)
        {
            GD.PushError("WorldSpawner: не удалось загрузить res://scenes/Ship.tscn");
            return null;
        }
        var ship = scene.Instantiate<Ship>();
        // Ship.Net заполняется в _Ready, а адрес нужен ДО AddChild — берём NetState
        // из инстанса напрямую (§3.1 п.8).
        NetworkManager.Instance?.AssignNetIdIfUnset(ship.GetNode<NetState>("NetState"));
        if (nameOverride != null)
            ship.Name = nameOverride;
        // Позиция ставится до AddChild: первый снимок реплики обязан быть корректным.
        if (position is { } pos)
            ship.GlobalPosition = pos;
        AddChild(ship);
        return ship;
    }

    /// <summary>Offline-мир стадии 1: корабли + одиночный игрок с авторитетом 1.</summary>
    public void StartSinglePlayerWorld()
    {
        SpawnWorld();
        if (FindOfflinePlayer() != null)
            return;
        SpawnPlayerAt("offline", 1);
    }

    private Player? FindOfflinePlayer()
    {
        if (WorldRoot == null)
            return null;
        foreach (var node in WorldRoot.GetChildren())
            if (node is Player p && p.GetMultiplayerAuthority() == 1)
                return p;
        // Игрок мог быть пристёгнут (Reparent под точку крепления) — ищем шире.
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                p.GetMultiplayerAuthority() == 1)
                return p;
        return null;
    }

    /// <summary>Спавн игрока на первой свободной стартовой точке (§2.4 «Где
    /// спавнится игрок», п.3). Позиция — данные сцены Main/SpawnPoints, хардкод
    /// Vector3.Zero запрещён; если точек нет — громкая ошибка и спавн в GameRoot
    /// (§3.11 п.9): это единственный разрешённый случай позиции «в нуле».</summary>
    public Player? SpawnPlayerAt(string playerKey, int peerId, Vector3? position = null,
                                 bool hasRotation = false, Basis rotation = default)
    {
        var scene = GD.Load<PackedScene>("res://scenes/Player.tscn");
        if (scene == null)
        {
            GD.PushError("WorldSpawner: не удалось загрузить res://scenes/Player.tscn");
            return null;
        }
        var player = scene.Instantiate<Player>();
        player.AddToGroup("player");

        // Авторитет ставится ДО AddChild: узел с неверным авторитетом хотя бы кадр
        // управляется не тем пиром (§2.5 п.5).
        player.SetMultiplayerAuthority(peerId);

        if (position is { } pos)
        {
            player.GlobalPosition = pos;
            if (hasRotation)
                player.GlobalTransform = new Transform3D(rotation, pos);
        }
        else
        {
            var start = FindFreeSpawnPoint();
            if (start != null)
                player.GlobalPosition = start.GlobalPosition;
            else
                DebugInfoBus.Report($"spawn = start:NONE (нет SpawnPoints в сцене) — " +
                                    "игрок в GameRoot, это ошибка конфигурации (§3.6)");
        }

        AddChild(player);
        DebugInfoBus.Report($"spawn = {playerKey[..System.Math.Min(8, playerKey.Length)]} " +
                            $"@ peer {peerId}");
        return player;
    }

    /// <summary>Первая свободная стартовая точка: обход по ИМЕНИ (Spawn0, Spawn1, …),
    /// не по порядку детей (§3.6); свободна — вокруг неё нет другого игрока ближе
    /// SpawnClearance. Список перебирается по кругу (§2.4 п.3).</summary>
    private Node3D? FindFreeSpawnPoint()
    {
        var set = WorldRoot?.GetNodeOrNull<Node3D>("SpawnPoints");
        if (set == null)
            return null;

        var players = GetTree().GetNodesInGroup("player");
        for (int i = 0; ; i++)
        {
            var point = set.GetNodeOrNull<Node3D>($"Spawn{i}");
            if (point == null)
                return i == 0 ? null : set.GetNodeOrNull<Node3D>("Spawn0");
            bool free = true;
            foreach (var node in players)
                if (node is Player p && GodotObject.IsInstanceValid(p) &&
                    p.GlobalPosition.DistanceTo(point.GlobalPosition) <
                        NetworkManager.SpawnClearance)
                {
                    free = false;
                    break;
                }
            if (free)
                return point;
        }
    }
}
