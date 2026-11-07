using System.Collections.Generic;
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

    /// <summary>Запасная мировая камера (§3.11 п.10). Единственная камера в проекте
    /// живёт внутри Player.tscn (current = true). Когда окно живёт, а игрока нет
    /// (оконный dedicated из кнопки `Start Server`: одиночный игрок удалён, §2.4),
    /// удаляется и его камера — а Godot НЕ выбирает новую current автоматически,
    /// поэтому viewport остаётся без камеры и экран гаснет («игрок не видит сцену»).
    /// Эта камера подстраховывает вид мира, пока активной камеры игрока нет.</summary>
    private Camera3D? _fallbackCamera;

    /// <summary>Мировой трансформ камеры последнего локального игрока, снятый перед
    /// его удалением — чтобы запасная камера встала ровно туда, где смотрел игрок,
    /// и вид мира не «прыгал» при переходе в серверный режим (§3.11 п.10).</summary>
    private Transform3D _lastPlayerCamXform;
    private bool _hasLastPlayerCamXform;

    public override void _Ready()
    {
        Current = this;

        _spawner = GetNodeOrNull<MultiplayerSpawner>("Spawner");
        if (_spawner != null)
        {
            // Спавнер повторяет узлы у всех пиров; пути — res://, а не пути дерева.
            _spawner.AddSpawnableScene("res://scenes/Ship.tscn");
            _spawner.AddSpawnableScene("res://scenes/Player.tscn");
            // Учебная мишень (этап 3 §3.2): спавнит ТОЛЬКО сервер (кнопка Spawn
            // Target гейтится IsServer), спавнер повторяет её у всех пиров;
            // удаление тоже только сервером (QueueFree → спавнер сам удалит у пира).
            _spawner.AddSpawnableScene("res://scenes/Target.tscn");
            // Станция (Этап 4 §4.3): спавнит сервер при старте мира (SpawnWorld),
            // спавнер повторяет её у всех пиров, включая поздних (шаг 4.3, В19).
            _spawner.AddSpawnableScene("res://scenes/Station.tscn");
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
            // Dedicated/Host из CLI или поднятые до Ready сцены: сигнал RoleChanged
            // уже прошёл, мир спавним отсюда. У хоста дополнительно спавним его
            // игрока с авторитетом 1 (офлайн-игрока к этому моменту уже нет).
            if (nm.IsServer)
                SpawnWorld();
            if (nm.IsHost)
                NetworkManager.Instance?.SpawnHostPlayer();
        }

        // Offline (в т.ч. тесты стадии 1): мир из одного процесса — корабли + игрок.
        if (nm == null || nm.Role == NetworkManager.NetRole.Offline)
            StartSinglePlayerWorld();

        // Кнопки мишеней (этап 3 §3.2, В17): регистрируются на всех ролях, но
        // ГЕЙТ «только сервер» сидит ВНУТРИ обработчика (OnSpawnTargetButton /
        // OnClearTargetsButton) — у клиента нажатие должно быть пустоп, иначе клиент
        // породит у себя мишень, которую спавнер не повторяет (сервер-спавн).
        DebugInfoBus.RegisterDebugButton("Spawn Target", OnSpawnTargetButton);
        DebugInfoBus.RegisterDebugButton("Clear Targets", OnClearTargetsButton);
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
        if (next == NetworkManager.NetRole.Host)
        {
            // Offline → Host (стадия 0-rework): игрок НЕ сохраняется узлом (он мог
            // быть перепришпилён и спавнился до появления peer — MultiplayerSpawner
            // его не зарепликует). Путь как у клиентов: снять LastSeen → удалить
            // offline-игрока → заспавнить заново через SpawnPlayerForPeer(1) с
            // авторитетом 1. Камера игрока current=true перекрывает viewport,
            // запасная НЕ нужна (мигания нет: старая камера освобождается в конце кадра).
            NetworkManager.Instance?.CaptureHostCandidateLastSeen();
            SpawnWorld();
            RemoveOfflinePlayer();
            NetworkManager.Instance?.SpawnHostPlayer();
            DeactivateFallbackCamera();
        }
        else if (next != NetworkManager.NetRole.Offline)
        {
            // Уход из offline в dedicated: одиночный игрок с авторитетом 1 удаляется —
            // в dedicated его быть не может (§2.4), оставить его = «игрок-призрак».
            // Перед удалением снимаем мировой трансформ его камеры, чтобы запасная
            // камера встала на то же место (вид не прыгает, §3.11 п.10).
            CaptureLocalPlayerCamera();
            RemoveOfflinePlayer();
            if (next == NetworkManager.NetRole.Dedicated)
                SpawnWorld();
            // Окно живёт, а игрока (и его камеры) больше нет — включаем запасную
            // камеру, иначе viewport остаётся без current и экран гаснет (§3.11 п.10).
            // В CLI-dedicated этот путь не проходит: мир спавнится прямо из _Ready
            // (RoleChanged сработал до Ready сцены), так что камера создаётся только
            // при оконном `Start Server` через кнопку.
            ActivateFallbackCamera();
        }
        else if (prev != NetworkManager.NetRole.Offline)
        {
            // Disconnect: возвращаем одиночного игрока в offline-мир (§3.9 п.5).
            // У хоста его игрок (авторитет 1) уже в мире — StartSinglePlayerWorld
            // найдёт его через FindOfflinePlayer и не станет спавнить второго.
            StartSinglePlayerWorld();
            // Камера игрока (current = true в Player.tscn) перехватывает viewport —
            // запасную гасим, чтобы не было двух current-камер (§3.11 п.4).
            DeactivateFallbackCamera();
        }
    }

    /// <summary>Снимает мировой трансформ активной камеры последнего локального игрока
    /// (до его удаления) — для бесшовной передачи вида запасной камере (§3.11 п.10).</summary>
    private void CaptureLocalPlayerCamera()
    {
        foreach (var node in GetTree().GetNodesInGroup("player"))
        {
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
            {
                var cam = p.GetActiveCamera();
                if (cam != null && GodotObject.IsInstanceValid(cam))
                {
                    _lastPlayerCamXform = cam.GlobalTransform;
                    _hasLastPlayerCamXform = true;
                }
                return;
            }
        }
    }

    /// <summary>Создать (один раз) и включить запасную мировую камеру. Ставится в
    /// мировой корень (WorldRoot), а не под игрока: игрока в этом режиме нет. Если
    /// удалось снять трансформ камеры удалённого игрока — встаёт ровно на него.</summary>
    private void ActivateFallbackCamera()
    {
        var root = WorldRoot;
        if (root == null)
            return;
        if (_fallbackCamera == null || !GodotObject.IsInstanceValid(_fallbackCamera))
        {
            _fallbackCamera = new Camera3D
            {
                Name = "ServerCamera",
                Far = 4000.0f,
                // Не реагирует на ввод и не мешает дебаг-слою; просто даёт viewport
                // активную камеру, чтобы мир был виден без игрока (§3.11 п.10).
                ProcessMode = ProcessModeEnum.Always,
            };
            root.AddChild(_fallbackCamera);
        }

        if (_hasLastPlayerCamXform)
            _fallbackCamera.GlobalTransform = _lastPlayerCamXform;
        else
            _fallbackCamera.GlobalPosition = root.GlobalPosition + new Vector3(0f, 6f, 12f);

        _fallbackCamera.Current = true;
    }

    /// <summary>Погасить запасную камеру: её сменила камера вернувшегося игрока.</summary>
    private void DeactivateFallbackCamera()
    {
        if (_fallbackCamera != null && GodotObject.IsInstanceValid(_fallbackCamera))
            _fallbackCamera.Current = false;
    }

    private void RemoveOfflinePlayer()
    {
        // Ищем по группе, а не только детьми WorldRoot: в offline игрок
        // автопристёгнут под точку крепления корабля (Reparent) и детьми корневого
        // узла уже не является — обход детей его бы пропустил (§3.6). Пропускаем
        // уже помеченных: повторный переход в том же кадре не зовёт QueueFree дважды.
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
                p.QueueFree();
    }

    /// <summary>Кнопка «Spawn Target»: ставит одну мишень на стартовую точку
    /// (см. TargetSpawnPosition) — только если мы сервер.</summary>
    private void OnSpawnTargetButton()
    {
        Vector3 pos = TargetSpawnPosition();
        SpawnTarget(pos);
    }

    /// <summary>Кнопка «Clear Targets»: снимает все мишени — только если мы сервер.</summary>
    private void OnClearTargetsButton()
    {
        ClearTargets();
    }

    /// <summary>Позиция спавна мишени по кнопке: стартовая точка Main/SpawnPoints
    /// по круговому счётчику, чуть выше точки, чтобы мишень не слиплась с
    /// кораблём/игроком на той же точке. Если точек нет — запасная точка в стороне.
    /// Не завязан на занятость (в отличие от игроков): мишени можно ставить
    /// сколько угодно, счётчик просто раскладывает их по точкам по кругу.</summary>
    private int _targetPointCursor;
    private Vector3 TargetSpawnPosition()
    {
        var set = WorldRoot?.GetNodeOrNull<Node3D>("SpawnPoints");
        if (set == null)
            return new Vector3(0, 0, 25); // запас: перед камерой, но в стороне от кораблей
        var point = set.GetNodeOrNull<Node3D>($"Spawn{_targetPointCursor}");
        if (point == null)
        {
            _targetPointCursor = 0;
            point = set.GetNodeOrNull<Node3D>("Spawn0");
            if (point == null) return new Vector3(0, 0, 25);
        }
        _targetPointCursor++;
        return point.GlobalPosition + Vector3.Up * 2.0f;
    }

    /// <summary>Имя узла, не сталкивающееся с уже занятыми в этом контейнере
    /// (Godot сам делает имена уникальными, но тестам нужен предсказуемый вид
    /// `Target`, `Target2`, `Target3` — как корабли `Ship`, `Ship2`).</summary>
    private string UniqueChildName(string baseName)
    {
        bool Taken(string n)
        {
            foreach (var node in GetChildren())
                if (node.Name == n)
                    return true;
            return false;
        }
        if (!Taken(baseName))
            return baseName;
        for (int i = 2; ; i++)
        {
            string candidate = baseName + i;
            if (!Taken(candidate))
                return candidate;
        }
    }

    /// <summary>Offline-мир стадии 1: корабли + одиночный игрок с авторитетом 1.</summary>
    public void StartSinglePlayerWorld()
    {
        SpawnWorld();
        if (FindOfflinePlayer() != null)
            return;
        SpawnPlayerAt("offline", 1);
    }

    /// <summary>Корабль по имени узла (`Ship`, `Ship2`) — только для автотестов
    /// (§3.9 п.15): в бою адрес корабля — ShipNetId, а не путь/имя, и путь не должен
    /// быть идентификатором. Возвращает живой узел из GameRoot или null.</summary>
    public Ship? FindShipByName(string name)
    {
        foreach (var node in GetChildren())
            if (node is Ship s && node.Name == name && GodotObject.IsInstanceValid(s))
                return s;
        return null;
    }

    private Player? FindOfflinePlayer()
    {
        // Пропускаем помеченных на удаление: QueueFree освобождает узел только в
        // конце кадра, и если StartServer+Disconnect случились в одном кадре,
        // удалённый игрок ещё «валиден» — без этой проверки StartSinglePlayerWorld
        // принял бы его за живого и не возродил одиночный мир (§3.9 п.5).
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
                return p;
        return null;
    }

}
