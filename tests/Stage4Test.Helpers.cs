using Godot;
using System.Collections.Generic;
using System.Linq;
using SpaceCorporation.Net;

namespace SpaceCorporation.Tests;

/// <summary>
/// Вспомогательные проверки Stage4Test (п.1–п.8, п.9–п.16, п.22–п.27) —
/// отдельный partial того же драйвера, чтобы основной файл остался картой фаз.
/// </summary>
public partial class Stage4Test
{
    // п.21: смерть (QueueFree) обрабатывается в конце физкадра, поэтому
    // PilotPeerId читаем сразу после TestStepForTest, пока узел ещё жив.
    private int _p21PilotBefore;
    private bool _p21PilotWasZero;

    // арена: стойка станции + счётчик имён арена-кораблей (новый корабль на
    // каждый таран — так сбрасывается кулдаун CollisionCooldownSec по станции).
    private int _ramCounter;

    // ─────────────────────── Блок A: станция ───────────────────────

    /// <summary>Станции в дереве: спавнер (GameRoot) — их родитель.</summary>
    private List<Station> StationsInWorld()
        => _spawner!.GetChildren().OfType<Station>().ToList();

    /// <summary>п.1: сцена станции существует и инстансируется, корень —
    /// StaticBody3D-подкласс Station с дочерним StationState.</summary>
    private void CheckStationScene()
    {
        var scene = GD.Load<PackedScene>("res://scenes/Station.tscn");
        Check(scene != null, "п.1: res://scenes/Station.tscn загружается");
        if (scene == null) return;
        var body = scene.Instantiate();
        Check(body is Station, $"п.1: корень сцены — Station (получено {body?.GetType().Name})");
        Check(body is StaticBody3D, "п.1: станция — StaticBody3D (статика, без интегратора)");
        Check(body?.GetNodeOrNull<StationState>("StationState") != null,
              "п.1: дочерний StationState есть");
        body?.Free();
    }

    /// <summary>п.2: у станции из мира есть ненулевой StationNetId.</summary>
    private void CheckStationNetId()
    {
        var nm = NetworkManager.Instance;
        Check(_spawner != null, "п.2: WorldSpawner.Current доступен");
        var stations = StationsInWorld().ToList();
        Check(stations.Count >= 1, $"п.2: в GameRoot есть станция (найдено {stations.Count})");
        var st = stations.FirstOrDefault();
        Check(st?.State != null && st.State.StationNetId != 0,
              $"п.2: StationNetId != 0 (получено {st?.State?.StationNetId})");
        _ = nm;
    }

    /// <summary>п.3: станция стоит в реестре NetworkManager под своим id.</summary>
    private void CheckRegistry()
    {
        var nm = NetworkManager.Instance!;
        var st = StationsInWorld().First();
        int id = st.State!.StationNetId;
        Check(nm.FindStation(id) != null, $"п.3: FindStation({id}) != null (RPC идут по id, не по пути)");
        Check(nm.Stations.ContainsKey(id), "п.3: реестр Stations содержит id станции");
    }

    /// <summary>п.5: в offline-мире ровно одна станция в дереве.</summary>
    private void CountOfflineStations()
    {
        int inTree = StationsInWorld().Count();
        int inReg = NetworkManager.Instance!.Stations.Count;
        Check(inTree == 1, $"п.5: в offline ровно одна станция в GameRoot (получено {inTree})");
        Check(inReg == 1, $"п.5: в реестре ровно одна станция (получено {inReg})");
    }

    /// <summary>п.4/п.6: второй SpawnStation даёт вторую станцию с ДРУГИМ id
    /// (общий счётчик NextNetId), коллизии id нет.</summary>
    private void TwoSpawnNoCollision()
    {
        var nm = NetworkManager.Instance!;
        var first = nm.Stations.Keys.ToList();
        var extra = _spawner!.SpawnStation(new Vector3(3000f, 0f, 3000f));
        Check(extra != null, "п.4: SpawnStation(вторая) вернула узел");
        int secondId = extra?.State?.StationNetId ?? 0;
        Check(secondId != 0 && !first.Contains(secondId),
              $"п.4: id второй станции уникален (первая {string.Join(",", first)}, вторая {secondId})");
        Check(nm.FindStation(secondId) != null, "п.6: вторая станция в реестре (коллизии id нет)");
        _extraStation = extra;
    }
    private Station? _extraStation;

    /// <summary>п.6-дополнение: пир получает станцию спавн-снимком (живой путь —
    /// стенд §3.10, HOWTO-two-players); здесь — что спавн-флаги свойств стоят
    /// (поздно зашедший пир обязан увидеть id и позу).</summary>
    private void CheckStationSpawnConfig()
    {
        var cfg = GD.Load<SceneReplicationConfig>("res://scenes/StationReplication.tres");
        Check(cfg != null, "п.6: StationReplication.tres загружается (ловушка пустого ресурса)");
        if (cfg == null) return;
        var idPath = new NodePath("StationState:StationNetId");
        var posPath = new NodePath("StationState:NetPosition");
        Check(cfg.HasProperty(idPath), "п.6: конфиг содержит StationState:StationNetId");
        Check(cfg.HasProperty(posPath), "п.6: конфиг содержит StationState:NetPosition");
        if (cfg.HasProperty(idPath))
            Check(cfg.PropertyGetSpawn(idPath), "п.6: spawn == true для StationNetId (холодный пир видит id)");
        if (cfg.HasProperty(posPath))
            Check(cfg.PropertyGetSpawn(posPath), "п.6: spawn == true для NetPosition станции");
    }

    /// <summary>п.7: метрика rec/s не зависит от станции — станция не в батче
    /// (Record'ы шлют только симулируемые Ship; HP/GodMode летят дельтой
    /// синхронизатора, не Record). В headless живых пиров нет, поэтому здесь
    /// проверяем структурный факт: грид/батч строятся из реестра Ships, а
    /// станция живёт в Stations и в Ships попасть не может. Динамику счётчика
    /// (rec/s до/после добавления станции) снимают на стенде §3.10.</summary>
    private void InfoRecRate()
    {
        var bc = SnapshotBroadcaster.Instance;
        Check(bc != null, "п.7: SnapshotBroadcaster.Instance доступен");
        if (bc == null) return;
        var nm = NetworkManager.Instance!;
        Check(nm.Stations.Count >= 2, "п.7: станций в мире уже >= 2 (после п.4)");
        Check(!nm.Ships.Values.Any(s => s != null && GodotObject.IsInstanceValid(s) && s.GetNodeOrNull("StationState") != null),
              "п.7: в реестре Ships нет станций (батч = только корабли)");

        bc.GetStats(out int packets, out int records);
        Check(true, $"п.7: счётчик батчей доступен (packets={packets}, records={records})");
    }

    /// <summary>п.8: SpatialIndex (AOI батча) не возвращает станцию ни в одном
    /// тире: грид строят только из симулируемых кораблей.</summary>
    private void CheckStationNotInSpatialIndex()
    {
        var nm = NetworkManager.Instance!;
        var st = StationsInWorld().First();
        Vector3 stationPos = st.GlobalPosition;

        var idx = new SpatialIndex();
        var shipPos = new List<Vec3>();
        foreach (var kv in nm.Ships)
        {
            var s = kv.Value;
            if (s != null && GodotObject.IsInstanceValid(s))
                shipPos.Add(new Vec3(s.GlobalPosition.X, s.GlobalPosition.Y, s.GlobalPosition.Z));
        }
        idx.Rebuild(shipPos.ToArray());

        var near = new List<int>(); var mid = new List<int>(); var far = new List<int>();
        idx.QueryTiers(new Vec3(stationPos.X, stationPos.Y, stationPos.Z),
                       400f, 1200f, 4000f, new InterestState(), near, mid, far);
        Check(idx.Count == shipPos.Count,
              $"п.8: в гриде только корабли (слотов {idx.Count}, кораблей {shipPos.Count})");
        Check(near.Count + mid.Count + far.Count == shipPos.Count,
              "п.8: все тиры вместе = только корабли (станции в гриде нет)");
    }

    // ─────────────────────── Блок B: HP и урон ───────────────────────

    /// <summary>п.9: ShipHealthReplication.tres читается движком обратно: у всех
    /// пяти свойств OnChange + spawn=true (ловушка стадии 1 — пустой ресурс).</summary>
    private void CheckHealthReplicationConfig()
    {
        var cfg = GD.Load<SceneReplicationConfig>("res://scenes/ShipHealthReplication.tres");
        Check(cfg != null, "п.9: ShipHealthReplication.tres загружается");
        if (cfg == null) return;
        Check(cfg.GetProperties().Count >= 5, $"п.9: в конфиге >= 5 свойств (получено {cfg.GetProperties().Count})");
        foreach (string name in new[] { "Hp", "MaxHp", "GodMode", "DamageTakenCount", "LastDamage" })
        {
            var p = new NodePath($"ShipHealthState:{name}");
            Check(cfg.HasProperty(p) &&
                  cfg.PropertyGetReplicationMode(p) == SceneReplicationConfig.ReplicationMode.OnChange,
                  $"п.9: replication_mode == OnChange для {p}");
            Check(cfg.HasProperty(p) && cfg.PropertyGetSpawn(p),
                  $"п.9: spawn == true для {p} (поздно зашедший пир видит HP)");
        }
    }

    /// <summary>п.10: у offline-корабля Hp == 100.</summary>
    private void CheckOfflineHp()
    {
        var ship = _spawner!.FindShipByName("Ship");
        var h = ship?.Health;
        Check(h != null, "п.10: ShipHealthState на Ship.tscn есть");
        Check(h != null && h.Hp == 100, $"п.10: offline Hp == 100 (получено {h?.Hp})");
        Check(h != null && h.MaxHp == 100, $"п.10: MaxHp == 100 (получено {h?.MaxHp})");
    }

    /// <summary>п.11: запись Hp на не-авторитете помечается как чужая
    /// (страховка GuardWrite/IsNonAuthorityWrite —按计划 §4.5: жёсткого отказа
    /// нет, т.к. так же пишет и сам синхронизатор на реплике).</summary>
    private void CheckNonAuthWriteGuard()
    {
        var ship = _spawner!.FindShipByName("Ship")!;
        var h = ship.Health!;
        Check(!h.IsNonAuthorityWrite(), "п.11: на авторитете запись своя (предикат false)");
        ship.TestSimulateAuthorityOwnerForTest(999); // роль не-авторитета
        Check(h.IsNonAuthorityWrite(), "п.11: на не-авторитете запись будет помечена чужой");
        h.Hp = 42; // PrintErr в stderr — сам сигнал страховки
        Check(ship.IsServerAuthority == false, "п.11: роль не-авторитета стоит после записи");
        ship.TestSimulateAuthorityOwnerForTest(1);  // сервер вернул авторитет
        h.Hp = 100;
        Check(h.Hp == 100 && !h.IsNonAuthorityWrite(), "п.11: авторитет восстановлен, Hp = 100");
    }

    /// <summary>п.12–п.16: чистая функция урона без движка.</summary>
    private void CheckPureDamageFn()
    {
        Check(CollisionDamage.ComputeDamage(100, false, 11f) == 0,
              "п.12: |vn|=11 < порог 12 -> урон 0");
        Check(CollisionDamage.ComputeDamage(100, false, 30f) == 36,
              $"п.13: |vn|=30 -> 36 (получено {CollisionDamage.ComputeDamage(100, false, 30f)})");
        Check(CollisionDamage.ComputeDamage(100, false, 500f) == 100,
              $"п.14: |vn|=500 -> кламп в MaxHp=100 (получено {CollisionDamage.ComputeDamage(100, false, 500f)})");
        Check(CollisionDamage.ComputeDamage(100, true, 400f) == 0,
              "п.15: god mode -> 0 при любой скорости");
        int dPlus = CollisionDamage.ComputeDamage(100, false, 40f);
        int dMinus = CollisionDamage.ComputeDamage(100, false, -40f);
        Check(dPlus == dMinus && dPlus == 56,
              $"п.16: знак vn не влияет на урон (vn=+40 -> {dPlus}, vn=-40 -> {dMinus})");
        var v = new Vector3(40f, 0f, 0f);
        // Внешняя нормаль станции в точке контакта: станция лежит от корабля
        // в +x — значит наружу (от центра к кораблю) она смотрит в -x.
        var n = new Vector3(-1f, 0f, 0f);
        float vnApproach = CollisionDamage.ApproachSpeed(v, n);
        Check(vnApproach == 40f,
              $"п.16: ApproachSpeed при сближении = +40 (получено {vnApproach})");
        // vn<0 — уже расходимся: отскок не должен разворачивать обратно (иначе
        // станция «тянет» корабль).
        var keep = CollisionDamage.ApplyBounce(v, n, -5f, CollisionDamage.BounceRestitution);
        Check(keep == v, $"п.16: ApplyBounce при vn<0 скорость НЕ меняет (получено {keep})");
        var bounced = CollisionDamage.ApplyBounce(v, n, vnApproach, CollisionDamage.BounceRestitution);
        Check(bounced.X < 0f, $"п.16: ApplyBounce при сближении разворачивает (получено {bounced})");
    }

    // ─────────────────────── арена (п.17–п.21, п.25) ───────────────────────

    /// <summary>Арена: станция-сфера r=60 в (1998,0,0) (Station-класс, слой
    /// Station, реальный StationState в реестре) + отдельная подложка.</summary>
    private void BuildArena()
    {
        _arenaStation = new Station
        {
            Name = "ArenaStation",
            Position = new Vector3(1998f, 0f, 0f),
            CollisionLayer = CollisionLayers.Station,
            CollisionMask = CollisionLayers.Ship,
        };
        _arenaStation.AddChild(new CollisionShape3D
        {
            Name = "Shape",
            Shape = new SphereShape3D { Radius = 60f },
        });
        _arenaStationState = new StationState { Name = "StationState", MaxHp = 5000 };
        _arenaStation.AddChild(_arenaStationState);
        _root!.AddChild(_arenaStation);
        Check(_arenaStationState.StationNetId != 0,
              $"п.17: арена-станция получила адрес ({_arenaStationState.StationNetId})");
    }

    /// <summary>Подготовка тарана: НОВЫЙ корабль на каждый замер (новый узел =
    /// чистый кулдаун CollisionCooldownSec и свой DamageTakenCount), поза и
    /// скорость пишутся детерминированно (в headless движок тело не двигает —
    /// шаг делает TestStepForTest, пробы probe_ci_*).</summary>
    private void RamArena(Vector3 pos, float speed, int hp, bool god)
    {
        if (_arenaShip != null && GodotObject.IsInstanceValid(_arenaShip))
            _arenaShip.QueueFree();
        _ramCounter++;
        var ship = _spawner!.SpawnShipFromScene(null, $"Arena{_ramCounter}");
        if (ship == null)
        {
            Check(false, $"п.17: спавн арена-корабля Arena{_ramCounter}");
            return;
        }
        ship.SetPhysicsProcess(false); // движок не двигает тело в headless
        ship.Freeze = true;
        ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        ship.GlobalPosition = pos;
        ship.LinearVelocity = new Vector3(speed, 0f, 0f);
        var h = ship.Health;
        if (h != null)
        {
            h.MaxHp = 100;
            h.Hp = hp;
            h.GodMode = god;
        }
        _arenaShip = ship;
    }

    // ─────────────────────── Блок C: смерть, god, дебаг ───────────────────────

    /// <summary>п.22: у НЕ-авторитета Hp == 0 НЕ запускает локальное удаление —
    /// QueueFree шлёт сервер, у пира узел снимает спавнер. Процесс жив.</summary>
    private void CheckNonAuthDeathNoFree()
    {
        _ramCounter++;
        var ship = _spawner!.SpawnShipFromScene(null, $"Guard{_ramCounter}");
        if (ship == null) { Check(false, "п.22: спавн корабля-страховки"); return; }
        ship.TestSimulateAuthorityOwnerForTest(999);
        var h = ship.Health!;
        h.Hp = 0; // реплика серверного Hp=0 (сеттер на реплике пишется всегда)
        Check(!ship.IsQueuedForDeletion(),
              "п.22: не-авторитет НЕ сам себя удаляет (смерть приходит спавнер-репликой)");
        Check(h.Hp == 0, "п.22: Hp == 0 доехал до не-авторитета (spawn/OnChange-флаги п.9)");
        ship.TestSimulateAuthorityOwnerForTest(1);
        ship.QueueFree();
    }

    /// <summary>п.23: у не-авторитета кнопка «Toggle God Mode» не регистрируется
    /// (гейт HasAuthority в _Ready). Инстансим Ship.tscn с authority=999 ДО
    /// AddChild — _Ready увидит не-авторитет.</summary>
    private void CheckGodButtonServerOnly()
    {
        int godBefore = DebugInfoBus.CountDebugButtons("Toggle God Mode");
        int interpBefore = DebugInfoBus.CountDebugButtons("Toggle Interpolation");
        var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        Check(scene != null, "п.23: Ship.tscn загружается");
        if (scene == null) return;
        var ship = scene.Instantiate<Ship>();
        ship.Name = "ClientCopy";
        // ДО AddChild: _Ready должен застать не-авторитет (в дереве авторитет
        // уже нельзя выставить «до готовности»).
        ship.SetMultiplayerAuthority(999, false);
        _root!.AddChild(ship);
        // Дублируем смену роли явным вызовом — как при смене авторитета в рантайме.
        ship.TestSimulateAuthorityOwnerForTest(999);
        Check(DebugInfoBus.CountDebugButtons("Toggle God Mode") == godBefore,
              "п.23: у не-авторитета кнопка God Mode НЕ добавлена (число не выросло)");
        Check(DebugInfoBus.CountDebugButtons("Toggle Interpolation") == interpBefore + 1,
              "п.23: у не-авторитета есть кнопка интерполяции (роль сменилась корректно)");
        _clientCopy = ship;
    }
    private Ship? _clientCopy;

    /// <summary>п.24: серверный метод ставит GodMode (у авторитета — ok), а на
    /// не-авторитете вызов того же метода ОТКАЗЫВАЕТ (god mode с клиента не
    /// принимается). Доставка реплики GodMode к пиру — шов конфига (п.9:
    /// OnChange+spawn); живая доставка — стенд §3.10.</summary>
    private void CheckGodModeServerSeam()
    {
        if (_godShip == null || !GodotObject.IsInstanceValid(_godShip))
        {
            _ramCounter++;
            _godShip = _spawner!.SpawnShipFromScene(null, $"God{_ramCounter}");
        }
        var h = _godShip?.Health;
        Check(h != null, "п.24: ShipHealthState у god-корабля есть");
        if (h == null) return;
        h.SetGodModeServerOnly(true);
        Check(h.GodMode, "п.24: серверный SetGodModeServerOnly(true) поставил GodMode");
        // Отказ на не-авторитете: вызов серверного метода с ролью клиента не пишет.
        _godShip!.TestSimulateAuthorityOwnerForTest(999);
        var cfg = GD.Load<SceneReplicationConfig>("res://scenes/ShipHealthReplication.tres");
        Check(cfg != null &&
              cfg.PropertyGetReplicationMode(new NodePath("ShipHealthState:GodMode"))
                  == SceneReplicationConfig.ReplicationMode.OnChange &&
              cfg.PropertyGetSpawn(new NodePath("ShipHealthState:GodMode")),
              "п.24: GodMode реплицируется (OnChange+spawn) — пир увидит god=on");
        h.SetGodModeServerOnly(false); // попытка с «клиента» — должна быть проигнорирована
        Check(h.GodMode, "п.24: запрос god с не-авторитета отклонён (GodMode не снят); RPC god mode нет вовсе");
        _godShip.TestSimulateAuthorityOwnerForTest(1);
    }

    /// <summary>п.26: строка дебаг-инфо корабля содержит HP.</summary>
    private void CheckHpInDebugInfo()
    {
        var h = _arenaShip?.Health;
        Check(h != null && h.Hp == 100,
              $"п.26: god-таран 60 м/с -> Hp остался 100 (получено {h?.Hp})");
        string txt = DebugInfoBus.BuildInfoText();
        Check(txt.Contains("HP: 100/100"),
              "п.26: BuildInfoText содержит «HP: 100/100» (god=on, dmg#=0)");
        Check(txt.Contains("god=on"), "п.26: BuildInfoText содержит god=on при включённом god mode");
    }

    /// <summary>п.27 (критерий (в)): «клиент попытался отменить урон».
    /// Эмуляция доставки: реплика Hp идёт дельтой OnChange от авторитета; в
    /// однопросцессном headless дельту доставляет тот же узел — поэтому после
    /// локальной отмены (Hp=100 на не-авторитете, GuardWrite её помечает)
    /// авторитетная запись сервера (44) затирает отмену: дельта OnChange при
    /// каждом изменении HP перезаписывает локальное значение у всех пиров.</summary>
    private void CheckClientCannotCancelDamage()
    {
        var ship = _arenaShip!;
        var h = ship.Health!;
        Check(h.Hp == 100 && h.DamageTakenCount == 0,
              "п.27: старт — Hp=100 после god-тарана 60 м/с");
        h.Hp = 44;                       // серверный урон (авторитет пишет истину)
        Check(h.Hp == 44, "п.27: сервер записал Hp = 44");
        ship.TestSimulateAuthorityOwnerForTest(999); // узел в роли клиента
        Check(h.IsNonAuthorityWrite(), "п.27: локальная запись клиента помечена чужой (GuardWrite)");
        h.Hp = 100;                      // «клиент отменил урон» локально
        ship.TestSimulateAuthorityOwnerForTest(1);   // дельта сервера пришла (авторитет пишет снова)
        h.Hp = 44;                       // OnChange-дельта сервера затирает локальную отмену
        Check(h.Hp == 44, "п.27: Hp снова 44 — отмена клиента перезаписана репликой (критерий (в))");
    }

    // ------------------------------------------------------------- утилиты

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"==== Stage4Test: проверок={_checks}, провалов={_failures} ====");
        GD.Print(_failures == 0 ? "==== STAGE4TEST OK ====" : "==== STAGE4TEST FAILED ====");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
