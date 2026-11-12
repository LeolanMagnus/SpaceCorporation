using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial WorldSpawner: вырезано из WorldSpawner.cs при partial-декомпозиции.
public partial class WorldSpawner : Node3D
{
    /// <summary>Серверный (и offline) путь удаления корабля (§3.9 п.16).
    /// Godot освобождает дерево СНИЗУ ВВЕРХ: игрок, пристёгнутый к точке крепления,
    /// — потомок корабля, и при `QueueFree` корабля он вышел бы из дерева и был бы
    /// удалён РАНЬШЕ, чем сработал бы `Ship._ExitTree` (его цикл отстёжки уже не
    /// нашёл бы игрока — тот вне дерева). Поэтому отстёгиваем пристёгнутых игроков
    /// ДО `QueueFree`: `Detach()` переносит их в мировой корень, и корабль уходит,
    /// не утягивая за собой живых игроков. `Ship._ExitTree` остаётся страховкой для
    /// путей, где корабль удаляют без этой обёртки.</summary>
    public void DespawnShip(Ship ship)
    {
        if (ship == null || !GodotObject.IsInstanceValid(ship) ||
            ship.Net == null || ship.Net.ShipNetId == 0)
            return;

        int netId = ship.Net.ShipNetId;
        foreach (var node in GetTree().GetNodesInGroup("player"))
        {
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                p.IsAttached && p.AttachedTo!.OwnerNetId == netId)
            {
                GD.Print($"DespawnShip ship#{netId}: пристёгнутый игрок отстёгнут " +
                         "до удаления корабля (§3.9 п.16)");
                p.Detach();
            }
        }
        ship.QueueFree();
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
        // Станция (Этап 4, шаг 4.3): спавнится НА СТАРТЕ МИРА, не по кнопке —
        // пир, зашедший в середине сессии, обязан получить её спавн-снимком.
        SpawnStation(StationSpawnPos);
        GD.Print("WorldSpawner: мир заспавнен (корабли раньше игроков, §2.4)");
    }

    /// <summary>Позиция станции: далеко от стартовых точек, чтобы корабли при
    /// спавне не впились в цилиндр (радиус 60 м).</summary>
    private static readonly Vector3 StationSpawnPos = new(0f, 0f, 400f);

    /// <summary>Спавн СТАНЦИИ (Этап 4, §4.3). Только авторитет: страховый гейт
    /// отказывает КЛИЕНТУ (роль Client) — в Offline один процесс, и там станция
    /// тоже нужна (п.5: ровно одна в дереве), поэтому жёсткий `IsServer`-гейт,
    /// как у <see cref="SpawnTarget"/>, не подходит: SpawnWorld вызывается и из
    /// StartSinglePlayerWorld. Адрес выдаётся ДО AddChild (иначе первый
    /// спавн-снимок уйдёт с StationNetId = 0 — правила те же, что у мишеней
    /// §3.1 п.8). Позиция — после AddChild (§3.6).</summary>
    public Station? SpawnStation(Vector3 position)
    {
        var nm = NetworkManager.Instance;
        if (nm != null && nm.Role == NetworkManager.NetRole.Client)
        {
            GD.PushWarning("WorldSpawner: SpawnStation вызван на клиенте — " +
                           "спавн станции разрешён только авторитету (§4.3); " +
                           "клиент получает станцию спавн-снимком.");
            return null;
        }
        var scene = GD.Load<PackedScene>("res://scenes/Station.tscn");
        if (scene == null)
        {
            GD.PushError("WorldSpawner: не удалось загрузить res://scenes/Station.tscn");
            return null;
        }
        var body = scene.Instantiate<Station>();
        NetworkManager.Instance?.AssignStationNetIdIfUnset(body.GetNode<StationState>("StationState"));
        body.Name = UniqueChildName("Station");
        AddChild(body);
        body.GlobalPosition = position;
        var state = body.GetNodeOrNull<StationState>("StationState");
        if (state != null)
        {
            // Поза — в буфер состояния ДО отправки спавн-снимка: свойства с
            // spawn = true читаются синхронизатором при входе узла в дерево,
            // и пир обязан увидеть корректную позицию станции.
            state.NetPosition = position;
            state.NetRotation = body.GlobalTransform.Basis.GetRotationQuaternion();
        }
        return body;
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
    /// снимок реплики уйдёт с ShipNetId = 0, и RPC корабля потеряются (§3.1 п.8).
    ///
    /// <paramref name="shipType"/> — тип корпуса из ShipStatCatalog: сервер пишет
    /// его в реплику NetState.ShipTypeValue ДО AddChild, и Ship._Ready строит по
    /// нему массу/габариты/цвет/базу полётной модели (у пиров тот же байт приезжает
    /// спавн-снимком репликации — spawn=true). По умолчанию — запись каталога
    /// DefaultShip (курьер), см. scripts/core/ShipTypes.cs.</summary>
    public Ship? SpawnShipFromScene(Vector3? position = null, string? nameOverride = null,
                                    ShipType shipType = ShipStatCatalog.DefaultType)
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
        var shipNetState = ship.GetNode<NetState>("NetState");
        NetworkManager.Instance?.AssignNetIdIfUnset(shipNetState);
        // Тип корпуса — тоже ДО AddChild: Ship._Ready читает байт из реплики при
        // построении корпуса и _baseFlight (характеристики для полётной модели
        // корабль получает из общего каталога по типу — требование дизайна).
        shipNetState.ShipTypeValue = (byte)ShipStatCatalog.Find(shipType).Type;
        // Этап H4 (hud-plan.md §1): фракция — тоже ДО AddChild (спавн-снимок
        // читает свойства при входе узла в дерево). Пока все заспавненные корабли
        // — игроцкие; враждебные/союзные появятся с ботами.
        shipNetState.FactionValue = (byte)Faction.Player;
        if (nameOverride != null)
            ship.Name = nameOverride;
        // BUG-008 (рецидив): первоначальный пилот пишем в реестр ДО AddChild.
        // В offline свободный корабль — «без пилота» (PilotPeerId = 0), а не
        // автопилот 1: SelfId = 1 делал бы КАЖДЫЙ корабль «локальным пилотом»
        // игрока, и WASD двигал бы все корабли сразу. Пристёгнутый корабль
        // получает пилота 1 позже, через Player.AttachServerSide (оно же
        // включает чтение ввода). В сети (Host/Dedicated) свободные корабли —
        // автопилот 1, как ждали тесты п.15/п.17.
        //
        // Порядок здесь принципиален: AddChild синхронно вызывает Ship._Ready,
        // который читает реестр пилотов (Ship: RegisterShip → TryGetPilot →
        // ApplyPilot). Запись ПОСЛЕ AddChild означала бы: в _Ready реестр пуст →
        // ApplyPilot не вызван → PilotPeerId остался на дефолте (1), а значение
        // из реестра не применил бы уже НИКТО (TryGetPilot больше не зовётся).
        // Отсюда и «оба корабля слушаются WASD» при зафикшенном гейте SetPilot.
        //
        // Пишем в реестр БЕЗ ApplyPilot: узел ещё не в дереве и не зарегистрирован,
        // а лишний ApplyPilot снял бы дебаг-кнопку чужого корабля (метки кнопок
        // не различают корабли) и сбросил бы очередь ввода уже летящего тела.
        // Ship подхватит значение в _Ready через TryGetPilot.
        if (shipNetState.ShipNetId != 0 &&
            NetworkManager.Instance is { IsAuthoritySide: true } nm)
            nm.SetInitialPilot(shipNetState.ShipNetId, nm.FreeShipPilotId);
        // Позиция ставится ПОСЛЕ AddChild: вне дерева get_global_transform вернёт
        // Transform3D() и напечатает ERROR (§3.6). Первый снимок реплики берётся в
        // _Process синхронизатора, а не внутри AddChild, поэтому позиция, заданная
        // сразу после входа в дерево (тот же кадр, тот же вызов), в снимок попадает.
        AddChild(ship);
        if (position is { } pos)
            ship.GlobalPosition = pos;
        return ship;
    }

    /// <summary>Спавн УЧЕБНОЙ МИШЕНИ (этап 3 §3.2). Спавнит ТОЛЬКО авторитет
    /// (гейт IsServer на кнопке, шаг 3.2/п.16; и здесь страховка): у спавн-сцены
    /// путь на пирах генерирует спавнер, поэтому идентификатор — TargetNetId,
    /// а не путь. Адрес выдаётся ДО AddChild (то же правило, что у кораблей
    /// §3.1 п.8), иначе первый снимок уйдёт с TargetNetId = 0 и RPC урона (шаг 3.6)
    /// не найдут цель. Позиция — после AddChild (§3.6: вне дерева get_global_transform
    /// печатает ERROR). Контейнер — сам WorldSpawner (spawn_path = «..» = GameRoot):
    /// узлы, добавленные сюда, MultiplayerSpawner повторяет у всех пиров (как корабли).</summary>
    public TargetState? SpawnTarget(Vector3 position)
    {
        var nm = NetworkManager.Instance;
        // Гейт по РОЛИ, а не по IsServer: в offline IsServer == false, и кнопка
        // «Spawn Target» в одиночной игре молча ничего не делала (исправленный
        // баг). Авторитетная сторона — IsServer || Offline (как у SpawnStation).
        if (nm == null || nm.IsClient)
        {
            GD.PushWarning("WorldSpawner: SpawnTarget вызван на клиенте (или без " +
                           "NetworkManager) — спавн мишеней разрешён только " +
                           "авторитету (§3.2, В17).");
            return null;
        }
        var scene = GD.Load<PackedScene>("res://scenes/Target.tscn");
        if (scene == null)
        {
            GD.PushError("WorldSpawner: не удалось загрузить res://scenes/Target.tscn");
            return null;
        }
        var body = scene.Instantiate<RigidBody3D>();
        // Адрес мишени из общего счётчика — ДО AddChild (цель узла TargetState
        // берём из инстанса: Net у TargetState заполняется в _Ready/_EnterTree).
        var tState = body.GetNode<TargetState>("TargetState");
        NetworkManager.Instance?.AssignTargetNetIdIfUnset(tState);
        // Этап H4 (hud-plan.md §1): фракция мишени — враждебная (маркер HUD
        // красится по фракции). Пишется ДО AddChild — спавн-снимок.
        tState.FactionValue = (byte)Faction.Hostile;
        // Радар (radar-plan R2): поза — в буфер состояния ДО AddChild (тот же
        // порядок, что у станции в SpawnStationFromScene и фракции выше):
        // свойства со spawn=true читаются при входе узла в дерево, и RadarSource
        // на пире берёт позицию мишени именно из TargetState.NetPosition. Без
        // этой записи мишень для радара стояла бы в (0,0,0).
        tState.NetPosition = position;
        body.Name = UniqueChildName("Target");
        AddChild(body);
        body.GlobalPosition = position;
        return body.GetNodeOrNull<TargetState>("TargetState");
    }

    /// <summary>Удалить ВСЕ мишени (кнопка Clear Targets, этап 3 §3.2/п.16).
    /// Удаляет только сервер: `QueueFree` на сервере → MultiplayerSpawner сам
    /// удаляет узел у каждого пира; вручную RPC-удаление слать НЕЛЬЗЯ (§3.2, таблица
    /// «Clear Targets должен удалять узлы у всех»).</summary>
    public int ClearTargets()
    {
        var nm = NetworkManager.Instance;
        // Гейт по РОЛИ (как в SpawnTarget): в offline IsServer == false, и кнопка
        // «Clear Targets» в одиночной игре молча не удаляла мишени.
        if (nm == null || nm.IsClient)
        {
            GD.PushWarning("WorldSpawner: ClearTargets вызван на клиенте (или без " +
                           "NetworkManager) — удалять мишени может только авторитет (§3.2).");
            return 0;
        }
        int removed = 0;
        // Снимаем копию списка: QueueFree помечает узел, и если идти по живому
        // GetChildren(), итератор пропустит соседей (узлы помечены на удаление).
        var bodies = new List<RigidBody3D>();
        foreach (var node in GetChildren())
            if (node is RigidBody3D b && b.GetNodeOrNull<TargetState>("TargetState") != null)
                bodies.Add(b);
        foreach (var b in bodies)
        {
            b.QueueFree();
            removed++;
        }
        return removed;
    }

    // ────────────────────────── кнопки мишеней (§3.2, п.16) ─────────────────────
    //
    // Обработчики кнопок ВСЕГДА вызываются (кнопка видна на любой роли), но внутри
    // стоит гейт IsServer: SpawnTarget/ClearTargets сами возвращают null/0 у не-
    // сервера и печатают предупреждение. Так клиент, нажавший кнопку, НЕ создаёт
    // у себя мишень — сервер-спавн остаётся единственным источником (§3.2, В17).

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

        // BUG-007: имя узлу даёт сам корень Player.tscn («Player»); специально его
        // МЕНЯТЬ нельзя — на него опираются тесты/поиск. Достаточно, чтобы имя было
        // валидным: см. force_readable_name ниже.

        // Авторитет ставится ДО AddChild: узел с неверным авторитетом хотя бы кадр
        // управляется не тем пиром (§2.5 п.5).
        player.SetMultiplayerAuthority(peerId);

        // Позицию ставим ПОСЛЕ AddChild: до входа в дерево GlobalPosition — это
        // identity, а get_global_transform вне дерева возвращает Transform3D() и
        // печатает ERROR (§3.6). Порядок: авторитет → в дерево → позиция.
        //
        // force_readable_name = true (второй аргумент) — вторая половина фикса
        // BUG-007. При пере-спавне того же пира (восстановление из LastSeenState,
        // force-выдворение занятого ключа) старый узел ещё жив: QueueFree освобождает
        // его только в конце кадра и он держит имя «Player#2». Быстрый путь Godot при
        // коллизии имён резервирует символ @ и переименовывает новинку в
        // «@CharacterBody3D@11», а MultiplayerSpawner на таком узле печатает ERROR
        // «reserved name» (гейт: get_name().validate_node_name() == get_name()).
        // С force_readable_name коллизия разводится читаемым суффиксом — имя
        // остаётся валидным, гейт проходит, ERROR'а нет (§2.5, как у кораблей).
        AddChild(player, true);

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
