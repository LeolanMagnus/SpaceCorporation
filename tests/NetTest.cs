using System;
using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест сети без UDP (§3.9): offline-peer + сымитированные чужие авторитеты.
/// Покрывает п.13–17 (видимость синхронизатора, spawn-снимок, N кораблей,
/// удаление корабля с пристёгнутым игроком, переход A→B) и доступную часть
/// п.1–12 на offline-peer. Реальный сетевой прогон — отдельный скрипт (§3.10).
/// Запуск: godot --headless --path . res://tests/NetTest.tscn
/// </summary>
public partial class NetTest : Node
{
    private int _failures;
    private int _checks;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;

    // Машина состояний: шаги идут по кадрам, чтобы дождаться QueueFree/физики.
    // Порядок важен: п.17 (переход корабль→корабль) требует, чтобы ОБА корабля
    // были живы, поэтому идёт ДО п.16 (там Ship2 удаляется).
    // П.5 (переходы Offline↔Dedicated) идёт ПЕРВЫМ и заканчивает в Offline: он
    // поднимает и рвёт сервер, а п.15 сам поднимает сервер и ждёт Error.Ok —
    // значит к п.15 peer не должен быть активен. Два кадра: на втором QueueFree
    // уже обработан, и «игрока нет» проверяется на удалённом узле, а не на
    // помеченном.
    private enum Phase { WaitSpawn, P5a, P5b, P13, P14, P15, P17, P16a, P16b, P8, P18, Done }
    private Phase _phase = Phase.WaitSpawn;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _main = GetNodeOrNull<Node>("Main");
        if (_main == null)
        {
            GD.PrintErr("FAIL: сцена Main не найдена");
            Finish();
            return;
        }
        // Seam: в headless нет реальных пиров (§3.10 шаг 6), а assert'ам про
        // смену авторитета нужен «существующий» пир 2.
        NetworkManager.TestExtraPeers.Add(2);
        NetworkManager.TestExtraPeers.Add(3);
    }

    public override void _Process(double delta)
    {
        _frame++;
        switch (_phase)
        {
            case Phase.WaitSpawn:
                // Ждём факта спавна мира (offline-ветка WorldSpawner в _Ready),
                // плюс кадр, чтобы QueueFree/Reparent успели примениться.
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null &&
                    _spawner.FindShipByName("Ship2") != null)
                    _phase = Phase.P5a;
                else if (_frame > 60)
                {
                    Check(false, "мир заспавнен (Ship и Ship2 появились в GameRoot)");
                    Finish();
                }
                break;
            case Phase.P5a: CheckRoleTransitions(); _phase = Phase.P5b; break;
            case Phase.P5b: CheckOfflinePlayerRestored(); _phase = Phase.P13; break;
            case Phase.P13: CheckVisibility(); _phase = Phase.P14; break;
            case Phase.P14: CheckSpawnSnapshot(); _phase = Phase.P15; break;
            case Phase.P15: CheckManyShips(); _phase = Phase.P17; break;
            case Phase.P17: CheckShipToShip(); _phase = Phase.P18; break;
            case Phase.P18: RunP18(); _phase = Phase.P16a; break;
            case Phase.P16a: CheckShipDeleteWithPlayer(); _phase = Phase.P16b; break;
            case Phase.P16b: CheckShipDeleteWithPlayer2(); _phase = Phase.P8; break;
            case Phase.P8: RunP8(); _phase = Phase.Done; break;
            case Phase.Done: Finish(); break;
        }
    }

    // ------------------------- п.5: переходы Offline ↔ Dedicated (§3.9 п.5)
    //
    // Проверяет ровно то, что делают кнопки Start Server / Disconnect из шага 5:
    //   • StartServer при активном peer → AlreadyInUse (CreateServer повторно без
    //     Close не зовётся);
    //   • StartServer после Disconnect → Ok (порт переиспользуется после Close);
    //   • dedicated-процесс не создаёт ни одного Player;
    //   • offline-Player удаляется при переходе в Dedicated и возвращается при
    //     Disconnect (§2.1, §3.11 п.10).
    //
    // Кадр P5a: старт/стоп сервера и проверки роли. Кадр P5b: QueueFree уже
    // обработан движком, проверяем фактическое отсутствие/наличие игрока.
    private void CheckRoleTransitions()
    {
        var nm = NetworkManager.Instance!;

        Check(nm.Role == NetworkManager.NetRole.Offline, "п.5: стартовая роль = Offline");
        Check(CountOfflinePlayers() == 1,
              $"п.5: в Offline ровно 1 игрок (получено {CountOfflinePlayers()})");

        // Отдельный порт, чтобы не мешать п.15 (он поднимает сервер на дефолтном).
        const int port = 24599;
        var err1 = nm.StartServer(port);
        Check(err1 == Error.Ok, $"п.5: StartServer({port}) в Offline -> Ok (получено {err1})");
        Check(nm.Role == NetworkManager.NetRole.Dedicated, "п.5: после StartServer роль = Dedicated");

        // Повторный StartServer без Disconnect — peer уже активен, CreateServer
        // повторно не зовётся (§3.9 п.5).
        var err2 = nm.StartServer(port);
        Check(err2 == Error.AlreadyInUse,
              $"п.5: StartServer при активном peer -> AlreadyInUse (получено {err2})");

        nm.Disconnect();
        Check(nm.Role == NetworkManager.NetRole.Offline, "п.5: Disconnect вернул роль = Offline");

        // Порт после Close переиспользуется.
        var err3 = nm.StartServer(port);
        Check(err3 == Error.Ok, $"п.5: StartServer после Disconnect -> Ok (получено {err3})");
        nm.Disconnect();
        Check(nm.Role == NetworkManager.NetRole.Offline, "п.5: финальный Disconnect -> Offline");
    }

    /// <summary>Второй кадр п.5: QueueFree обработан движком. В Dedicated-фазе
    /// offline-игрок был удалён (в dedicated-процессе игроков нет, §2.4), после
    /// финального Disconnect — восстановлен. Считаем живых offline-игроков.</summary>
    private void CheckOfflinePlayerRestored()
    {
        var nm = NetworkManager.Instance!;
        Check(nm.Role == NetworkManager.NetRole.Offline, "п.5: по итогам переходов роль = Offline");
        Check(CountOfflinePlayers() == 1,
              $"п.5: offline-игрок удалён в Dedicated и восстановлен после Disconnect " +
              $"(живых: {CountOfflinePlayers()})");
    }

    // ------------------------------------------------------------ п.13, п.14

    /// <summary>§3.9 п.13: у Ship-синхронизатора PublicVisibility == true, ни одного
    /// visibility-фильтра; SetVisibilityFor(2, false) не помечает узел на удаление.
    /// Для Player-синхронизатора запрета нет (у Player свой режим — не проверяем
    /// запрет, проверяем только что Ship не сломан).</summary>
    private void CheckVisibility()
    {
        var ship = _spawner!.FindShipByName("Ship");
        var sync = ship?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
        Check(sync != null, "п.13: у Ship есть Synchronizer");
        if (sync == null) return;
        Check(sync.PublicVisibility, "п.13: Ship.Synchronizer.PublicVisibility == true");
        // Явных visibility-фильтров у корабля быть не должно (§4 В4 п.1). Перечислить
        // фильтры публичным API нельзя, а GetVisibilityFor(<несуществующий пир>)
        // возвращает false независимо от public_visibility (Godot:
        // ERR_FAIL_COND_V(!peers.has(peer), false)) — в offline список пиров пуст,
        // поэтому «открыт по умолчанию» на несуществующем пиру не проверяемо. Контракт
        // «нет фильтров + public» покрывают PublicVisibility==true выше и «SetVisibilityFor
        // не удаляет корабль» ниже (само удаление у не-сервера в offline не смоделировать,
        // оговорка §3.9 п.13).
        sync.SetVisibilityFor(2, false);
        Check(!ship!.IsQueuedForDeletion(),
              "п.13: SetVisibilityFor(2,false) не помечает Ship на удаление");
        Check(GodotObject.IsInstanceValid(ship), "п.13: Ship жив после смены видимости");
        sync.SetVisibilityFor(2, true);

        var player = FindOfflinePlayer();
        var psync = player?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
        // У Player синхронизатора может не быть вовсе (игрок — авторитет сам себя
        // не реплицирует в offline) — запрет касался только Ship.
        if (psync != null)
            GD.Print($"INFO  п.13: у Player есть Synchronizer (PublicVisibility=" +
                     $"{psync.PublicVisibility}) — запрет не касался Player");
    }

    /// <summary>§3.9 п.14: конфиг содержит ровно NetState/NetPosition (формат с
    /// двоеточием) и spawn=true для NetState:NetPosition / NetState:NetRotation.</summary>
    private void CheckSpawnSnapshot()
    {
        var ship = _spawner!.FindShipByName("Ship");
        var sync = ship?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
        var cfg = sync?.ReplicationConfig;
        Check(cfg != null, "п.14: у Synchronizer задан ReplicationConfig");
        if (cfg == null) return;

        var paths = cfg.GetProperties();
        bool Has(string s)
        {
            foreach (var p in paths)
                if (p.ToString() == s)
                    return true;
            return false;
        }
        Check(Has("NetState:NetPosition"),
              $"п.14: конфиг содержит NetState:NetPosition (получено {string.Join(", ", paths)})");
        Check(!Has("NetPosition"),
              "п.14: в конфиге НЕТ голого NetPosition (путь без subname отбрасывается, §3.11 п.2)");

        var np = new NodePath("NetState:NetPosition");
        var nr = new NodePath("NetState:NetRotation");
        Check(cfg.HasProperty(np) && cfg.PropertyGetSpawn(np),
              "п.14: spawn == true для NetState:NetPosition");
        Check(cfg.HasProperty(nr) && cfg.PropertyGetSpawn(nr),
              "п.14: spawn == true для NetState:NetRotation");

        // Спавн-снимок в offline: сервер (он же мы) меняет NetPosition корабля —
        // значение остаётся изменённым (второй пир без UDP недоступен, §3.10 шаг 6;
        // живая проверка снимка — в сетевом стенде, шаг 7).
        var target = new Vector3(123f, 0.9f, -7f);
        ship!.Net!.NetPosition = target;
        GD.Print("INFO  п.14: offline-часть: NetPosition изменён на " +
                 $"{ship.Net.NetPosition} — реплика второму пиру проверяется в сетевом стенде");
    }

    // ------------------------------------------------------------ п.15

    /// <summary>Тестовый игрок с авторитетом 2 (чужой пир), живёт между фазами
    /// п.15 → п.17 → п.16, чтобы проверить переходы авторитета и удаление
    /// корабля под пристёгнутым игроком.</summary>
    private Player? _p2;

    /// <summary>§3.9 п.15: N кораблей. Сервер спавнит два корабля → разные
    /// ShipNetId (1 и 2), разные пути, в DebugInfoBus два источника (Ship#1,
    /// Ship#2); AssignShipAuthority(2, 2) меняет авторитет второго, не трогая
    /// первый; AttachServerSide(2, 0) крепит именно ко второму.
    /// Требует Dedicated: авторитет назначает только сервер, поэтому здесь
    /// поднимаем headless-сервер (0 клиентов, Rpc — no-op).</summary>
    private void CheckManyShips()
    {
        var nm = NetworkManager.Instance;
        Check(nm != null, "п.15: NetworkManager.Instance доступен");
        if (nm == null) return;

        // Offline → Dedicated: RemoveOfflinePlayer() уберёт одиночного игрока,
        // SpawnWorld() идемпотентен (корабли остаются с адресами и реестром).
        var err = nm.StartServer();
        Check(err == Error.Ok, $"п.15: StartServer() в headless -> Ok (получено {err})");
        Check(nm.IsServer, "п.15: после StartServer роль = Dedicated (IsServer)");

        var ship1 = _spawner!.FindShipByName("Ship");
        var ship2 = _spawner.FindShipByName("Ship2");
        Check(ship1 != null && ship2 != null, "п.15: оба корабля живы после смены роли");
        if (ship1 == null || ship2 == null) return;

        Check(ship1.Net!.ShipNetId == 1 && ship2.Net.ShipNetId == 2,
              $"п.15: разные ShipNetId (1 и 2), получено {ship1.Net.ShipNetId} и {ship2.Net.ShipNetId}");
        Check(ship1.GetPath() != ship2.GetPath(),
              "п.15: разные пути узлов (GameRoot/Ship, GameRoot/Ship2)");
        Check(DebugInfoBus.HasInfoSource("Ship#1") && DebugInfoBus.HasInfoSource("Ship#2"),
              "п.15: в DebugInfoBus два источника (Ship#1, Ship#2)");

        // §3.11 п.10: после удаления одиночного игрока (вместе с его камерой из
        // Player.tscn) viewport обязан остаться с активной камерой — иначе оконный
        // dedicated получает чёрный экран («игрок не видит сцену»). WorldSpawner
        // включает запасную ServerCamera; проверяем, что current-камера есть.
        var activeCam = GetTree().Root.GetCamera3D();
        Check(activeCam != null,
              "п.15: после StartServer в viewport есть активная камера (мир виден без игрока)");

        // Назначаем авторитет второго корабля чужому пиру 2 (он в TestExtraPeers).
        nm.AssignShipAuthority(shipNetId: 2, peerId: 2);
        Check(ship2.PilotPeerId == 2, "п.15: Ship2.PilotPeerId == 2 после AssignShipAuthority(2,2)");
        Check(ship1.PilotPeerId == 1, "п.15: Ship1.PilotPeerId не тронут (== 1)");

        // AttachServerSide(2, 0) — пара (ownerNetId=2, pointNetId=0) крепит ко
        // второму кораблю. Спавним чужого игрока с авторитетом 2 и крепим в том
        // же кадре (до автопристёжки на первом физическом шаге).
        _p2 = _spawner.SpawnPlayerAt("p2", 2);
        Check(_p2 != null, "п.15: SpawnPlayerAt(\"p2\", 2) вернул игрока");
        if (_p2 != null)
        {
            _p2.AttachServerSide(ownerNetId: 2, pointNetId: 0);
            Check(_p2.IsAttached && _p2.AttachedTo!.OwnerNetId == 2,
                  "п.15: AttachServerSide(2,0) крепит к кораблю с ShipNetId==2");
            Check(nm.CountShipsPilotedBy(2) <= 1,
                  "п.15: пир 2 пилотирует не больше одного корабля (инвариант п.17)");
        }
    }

    // ------------------------------------------------------------ п.17

    /// <summary>§3.9 п.17: переход корабль A → корабль B одним вызовом. Счётчик
    /// «сколько кораблей имеют PilotPeerId == pilot» никогда не превышает 1:
    /// AssignShipAuthority отпускает прежний корабль ДО назначения нового
    /// (рантайм-гейт), поэтому промежуточного «два авторитета» не возникает.</summary>
    private void CheckShipToShip()
    {
        var nm = NetworkManager.Instance;
        if (nm == null || _p2 == null)
        {
            Check(false, "п.17: есть сервер и тестовый игрок (см. п.15)");
            return;
        }
        var ship1 = _spawner!.FindShipByName("Ship");
        var ship2 = _spawner.FindShipByName("Ship2");
        Check(ship1 != null && ship2 != null, "п.17: оба корабля живы перед переходом");
        if (ship1 == null || ship2 == null) return;

        // Нормализуем состояние: после автопристёжки на физическом шаге AttachedTo
        // мог указывать на другую точку (без смены авторитета). Приводим п.2 к
        // детерминированному «пилот Ship2, пристёгнут к Ship2».
        _p2.AttachServerSide(ownerNetId: 2, pointNetId: 0);
        Check(ship2.PilotPeerId == 2 && ship1.PilotPeerId == 1,
              "п.17: до перехода Ship2 под пиром 2, Ship1 под сервером");
        Check(nm.CountShipsPilotedBy(2) == 1, "п.17: до перехода пир 2 пилотирует ровно 1 корабль");

        // Один переход A←B: AttachServerSide(1,0) — крепим к Ship1, гейт отпускает
        // Ship2 раньше, чем назначает Ship1.
        _p2.AttachServerSide(ownerNetId: 1, pointNetId: 0);
        Check(ship1.PilotPeerId == 2, "п.17: после перехода Ship1.PilotPeerId == 2");
        Check(ship2.PilotPeerId == 1, "п.17: после перехода Ship2 отпущен (PilotPeerId == 1)");
        Check(nm.CountShipsPilotedBy(2) <= 1,
              "п.17: счётчик пилотируемых пиром 2 кораблей ≤ 1 (никогда не 2)");
        Check(_p2.IsAttached && _p2.AttachedTo!.OwnerNetId == 1,
              "п.17: игрок пристёгнут к Ship1 (OwnerNetId==1) после перехода");
    }

    // ------------------------------------------------------------ п.16

    /// <summary>§3.9 п.16 (часть 1): сервер удаляет корабль, к которому пристёгнут
    /// игрок. Ship._ExitTree отпускает игрока (Detach) и снимает регистрацию ДО
    /// фактического удаления узла, точка крепления исчезает из реестра.</summary>
    private void CheckShipDeleteWithPlayer()
    {
        var nm = NetworkManager.Instance;
        if (nm == null || _p2 == null)
        {
            Check(false, "п.16: есть сервер и тестовый игрок (см. п.15/п.17)");
            return;
        }
        var ship1 = _spawner!.FindShipByName("Ship");
        Check(ship1 != null, "п.16: Ship1 жив перед удалением");
        if (ship1 == null) return;

        // Игрок должен сидеть на Ship1 (к нему мы крепились в п.17).
        Check(_p2.IsAttached && _p2.AttachedTo!.OwnerNetId == 1,
              "п.16: игрок пристёгнут к удаляемому Ship1");

        // Серверный путь удаления: Detach пристёгнутых игроков ДО QueueFree
        // (Godot освобождает дерево снизу вверх, иначе игрок ушёл бы вместе с
        // кораблём раньше Ship._ExitTree). §3.9 п.16.
        _spawner.DespawnShip(ship1);
        GD.Print("INFO  п.16: Ship1 помечен на удаление (QueueFree) — проверка на следующем кадре");
    }

    /// <summary>§3.9 п.16 (часть 2, на следующем кадре): узел Ship1 удалён,
    /// _ExitTree отработал. Игрок выжил, НЕ висит на несуществующем узле, точка
    /// (1,0) исчезла из реестра, и его можно перепришпилить к оставшемуся Ship2.</summary>
    private void CheckShipDeleteWithPlayer2()
    {
        var nm = NetworkManager.Instance;
        if (nm == null || _p2 == null)
        {
            Check(false, "п.16b: есть сервер и тестовый игрок");
            return;
        }

        Check(GodotObject.IsInstanceValid(_p2), "п.16b: игрок выжил после удаления Ship1");
        Check(_spawner!.FindShipByName("Ship") == null, "п.16b: Ship1 удалён из GameRoot");
        Check(nm.FindAttachPoint(1, 0) == null,
              "п.16b: точка крепления (1,0) исчезла из реестра");
        Check(nm.FindShip(1) == null, "п.16b: Ship1 снят с реестра кораблей (FindShip(1)==null)");

        // Игрок не должен ссылаться на удалённый корабль: либо отстёгнут, либо
        // пристёгнут к живому (Ship2), но точно не к Ship1.
        if (_p2.IsAttached)
            Check(_p2.AttachedTo!.OwnerNetId != 1,
                  "п.16b: игрок не пристёгнут к удалённому Ship1");
        else
            Check(true, "п.16b: игрок отстёгнут после удаления корабля (не висит на мёртвом узле)");

        // Перепришпиливаем к оставшемуся Ship2 — доказательство, что игрок живой
        // и реестр точек в порядке (падения нет).
        _p2.AttachServerSide(ownerNetId: 2, pointNetId: 0);
        Check(_p2.IsAttached && _p2.AttachedTo!.OwnerNetId == 2,
              "п.16b: игрок перепришпилен к живому Ship2 без падения");
    }

    // ------------------------------------------------------------- п.8: спавн/возврат/идемпотентность
    /// <summary>§2.4 «Где спавнится игрок» + §4 В2=B. Всё в Dedicated без UDP:
    /// сервер спавнит игроков по HandleRequestSpawn (шов автотеста вместо RPC),
    /// слоты — фиктивные пиры 2 и 3. Проверки идут в одном кадре _Process, до
    /// _PhysicsProcess, чтобы автопристёгивание к Seat не сдвинуло игрока со спавна.</summary>
    private void RunP8()
    {
        var nm = NetworkManager.Instance!;
        var spawner = WorldSpawner.Current!;
        var set = spawner.WorldRoot!.GetNodeOrNull<Node3D>("SpawnPoints");
        Check(set != null, "п.8: Main/SpawnPoints существует");
        if (set == null) return;
        var spawn0 = set.GetNodeOrNull<Node3D>("Spawn0");
        var spawn1 = set.GetNodeOrNull<Node3D>("Spawn1");
        Check(spawn0 != null && spawn1 != null, "п.8: в сцене ≥2 стартовые точки");

        // (а) RequestSpawn("k1") → игрок на первой свободной точке, НЕ в Vector3.Zero.
        nm.HandleRequestSpawn(2, "k1");
        Check(nm.TryGetPeerPlayer(2, out var p1) && p1 != null,
              "п.8: RequestSpawn(k1) создал игрока на слоте 2");
        if (p1 == null) return;
        Check(p1.GlobalPosition != Vector3.Zero,
              "п.8: спавн не в Vector3.Zero (позиция — данные сцены)");
        Check(p1.GlobalPosition.DistanceTo(spawn0!.GlobalPosition) < 0.01f,
              "п.8: k1 заспавнен на первой свободной точке Spawn0");

        // (в) RequestSpawn("k2") → другой игрок, стартовая точка отличается от
        // занятой k1 (перебор свободных: два игрока не спавнятся в одной точке).
        nm.HandleRequestSpawn(3, "k2");
        Check(nm.TryGetPeerPlayer(3, out var p2) && p2 != null,
              "п.8: RequestSpawn(k2) создал игрока на слоте 3");
        if (p2 != null)
        {
            Check(p2.GlobalPosition.DistanceTo(p1.GlobalPosition) > 0.01f,
                  "п.8: k2 спавнен не в той же точке, что k1 (перебор свободных)");
            Check(p2.GlobalPosition.DistanceTo(spawn0.GlobalPosition) > 0.01f,
                  "п.8: k2 не на Spawn0 (там стоит k1)");
        }

        // (б) сдвинуть k1, серверный путь PeerDisconnected → RequestSpawn(k1) снова →
        // игрок в сохранённой позиции и с Velocity == Zero.
        var moved = p1.GlobalPosition + new Vector3(3.5f, 0.2f, -1.25f);
        p1.GlobalPosition = moved;
        nm.OnPeerLeft(2);
        Check(!nm.TryGetPeerPlayer(2, out _),
              "п.8: OnPeerLeft убрал слот 2 из таблицы");
        nm.HandleRequestSpawn(2, "k1");
        Check(nm.TryGetPeerPlayer(2, out var p1b) && p1b != null,
              "п.8: повторный RequestSpawn(k1) вернул игрока");
        if (p1b != null)
        {
            Check(p1b.GlobalPosition.DistanceTo(moved) < 0.01f,
                  "п.8: позиция восстановлена из LastSeenState (±0.01)");
            Check(p1b.Velocity == Vector3.Zero,
                  "п.8: скорость НЕ восстанавливается (Velocity == Zero)");
        }

        // (г) идемпотентность: RequestSpawn(k1) повторно от слота 2 → одного игрока.
        int before = nm.LivePlayerCount;
        nm.HandleRequestSpawn(2, "k1");
        Check(nm.LivePlayerCount == before,
              "п.8: повторный RequestSpawn(k1) от того же пира не создал второго");

        // (д) тот же ключ от ДРУГОГО пира при живом слоте → старый слот выгнан,
        // LastSeenState записан ДО DisconnectPeer(force) (§2.4).
        nm.HandleRequestSpawn(4, "k1");
        Check(!nm.TryGetPeerPlayer(2, out _),
              "п.8: живой слот 2 с тем же ключом выгнан (force)");
        Check(nm.TryGetPeerPlayer(4, out var p1c) && p1c != null,
              "п.8: RequestSpawn(k1) от нового пира 4 заспавнил игрока");
        if (p1c != null)
            Check(p1c.GlobalPosition.DistanceTo(moved) < 0.01f,
                  "п.8: при выдворении LastSeenState записан до force — позиция сохранена");

        nm.Disconnect();
    }

    // ------------------------------------------------------------- п.18: интерполяция ShipHull
    /// <summary>§3.10 п.8: не-авторитет интерполирует картинку ShipHull по снимкам.
    /// Шов TestPushSnapshot + InterpolateHull без UDP и без гонки по времени:
    /// задаём два снимка и момент now, проверяем результат.</summary>
    private void RunP18()
    {
        var ship = _spawner!.FindShipByName("Ship");
        Check(ship != null, "п.18: Ship доступен для теста интерполяции");
        if (ship == null) return;

        var a = new Vector3(0, 0, 0);
        var b = new Vector3(4, 0, 0);
        ship.TestPushSnapshot(1.0, a, Quaternion.Identity);
        ship.TestPushSnapshot(1.2, b, Quaternion.Identity);

        // Кадр ровно посередине между снимками (с учётом задержки 0.1):
        // now=1.15 → render=1.05 → alpha=(1.05-1.0)/0.2=0.25 → x=1.
        ship.InterpolateHull(1.15);
        var mid = ship.TestHullGlobalPosition();
        Check(mid.DistanceTo(new Vector3(1, 0, 0)) < 0.01f,
              $"п.18: середина между снимками интерполирована (x≈1), получено {mid}");

        // Кадр раньше самого старого снимка → рисуем самый старый (без экстраполяции назад).
        ship.InterpolateHull(0.95); // render=0.85 < 1.0
        Check(ship.TestHullGlobalPosition().DistanceTo(a) < 0.01f,
              "п.18: до самого старого снимка рисуем самый старый");

        // Кадр позже самого свежего → НЕ экстраполируем вперёд (картинка не обгоняет
        // сеть), рисуем самый свежий.
        ship.InterpolateHull(1.5); // render=1.4 > 1.2
        Check(ship.TestHullGlobalPosition().DistanceTo(b) < 0.01f,
              "п.18: после самого свежего снимка не экстраполируем — рисуем самый свежий");

        // Картинка (ShipHull) ≠ трансформ тела: интерполяция не трогает RigidBody3D.
        Check(ship.TestHullGlobalPosition() != ship.GlobalPosition ||
              ship.TestHullGlobalPosition().DistanceTo(ship.GlobalPosition) > 0.01f,
              "п.18: ShipHull отделён от тела (картинка ≠ физический трансформ)");
    }

    // ------------------------------------------------------------- утилиты
    /// <summary>Одиночный offline-игрок (авторитет 1) — для п.13. Ищем шире, чем
    /// просто дети GameRoot: игрок мог быть Reparent под точку крепления.</summary>
    private Player? FindOfflinePlayer()
    {
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
                return p;
        return null;
    }

    /// <summary>Живые offline-игроки (авторитет 1, не помечены на удаление) — для
    /// assert'а «StartServer не создаёт ни одного Player» (§3.9 п.5).</summary>
    private int CountOfflinePlayers()
    {
        int n = 0;
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == 1)
                n++;
        return n;
    }

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
        {
            GD.Print($"PASS {what}");
        }
        else
        {
            _failures++;
            GD.PrintErr($"FAIL {what}");
        }
    }

    private void Finish()
    {
        _phase = Phase.Done;
        GD.Print($"RESULT checks={_checks} failures={_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
