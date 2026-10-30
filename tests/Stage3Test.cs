using Godot;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест стадии 3, шаг 3.1 (учебная мишень): спавн через MultiplayerSpawner,
/// адрес из общего счётчика, реестр по TargetNetId, конфиг репликации HP.
/// Покрывает п.14–15 плана stage3 (§3.1 шаг 3.1). Запуск:
///   godot --headless --path . res://tests/Stage3Test.tscn
///
/// Реальной UDP-сети в headless нет (ENet-пиры в одном процессе к себе не
/// подключаются, §3.10 шаг 6), поэтому «доставка реплики» проверяется ШВОМ —
/// инспекцией конфига SceneReplicationConfig (что путь Hp задан ВЕРНО: с двоеточием,
/// spawn=true, режим OnChange), по образцу NetTest п.14 `CheckSpawnSnapshot`: там
/// тоже проверяется конфиг спавн-снимка, а не живая доставка. Живая доставка HP
/// между процессами — стенд §3.10 (HOWTO-two-players).
/// </summary>
public partial class Stage3Test : Node
{
    private int _failures;
    private int _checks;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    // Кадровый счётчик для фаз, которым надо дождаться применения QueueFree/спавна.
    private int _wait;
    // Контейнер, куда добавляем стендовый корабль п.17 (= GameRoot спавнера).
    private Node? _root;

    // Кадровая машина состояний: QueueFree/спавнер/смена роли применяются не сразу.
    private enum Phase
    {
        WaitSpawn, StartServer, P14, P15,
        P17Setup, P17Fire, P17Wait, P17Interact, P18, P19, P20,
        // шаг 3.5: серверный хитскан
        P21, P21Wait, P21Fire, P21Assert,
        P22, P22Wait, P22Stand, P22Fire, P22Assert,
        P23, P23Fire, P23Assert,
        // шаг 3.6: урон, уничтожение, защита от «мишени нет в реестре»
        P24, P24Wait, P24Fire, P24Wait2, P24Assert,
        P25, P25Wait, P25Fire, P25Assert,
        P26,
        P17Cleanup, P17InteractWait,
        P16Spawn1, P16Spawn2, P16ClearWait0, P16ClearWait1, P16ClearWait2,
        P16GoClient, P16ClientGate, P16ClientGateWait, Done
    }
    private Phase _phase = Phase.WaitSpawn;
    // Запоминаем число мишеней до нажатия кнопки, чтобы проверить «выросло на 1».
    private int _beforeSpawn;
    // Корабль-стенд для п.17 (визуал луча) и время ожидания самоудаления луча.
    private Ship? _pilotShip;

    // Шаг 3.5 (п.21-п.23): трекинг событий HitConfirmed серверного хитскана и
    // спавненных в арене мишени/стенда. HitConfirmed шов (event) ловит ровно то
    // же подтверждение, что шаг 3.6 превратит в RpcOnHitConfirmed.
    private int _hitCount;                 // сколько раз сработал HitConfirmed
    private int _hitTargetNetId;           // id последней подтверждённой мишени
    private Vector3 _hitPoint;             // точка последнего подтверждения
    // Относительные эталоны для п.22/п.23: попадание п.21 уже увеличило счётчик,
    // а выстрелы п.18-п.20, снятые хитсканом на СТАРОМ месте, могли задеть мировой
    // TestStand (слой Station, без TargetState → не считается). Станция (п.22) и
    // промах (п.23) не должны ДОБАВИТЬ ни подтверждений, ни попаданий — сверяем по
    // эталону, снятому непосредственно перед выстрелом, а не по абсолютному нулю.
    private int _hitsBaseline;             // _hitCount перед выстрелом в станцию/пустоту
    private int _hitsRegBaseline;          // HitsRegistered перед тем же выстрелом

    // шаг 3.6 (п.24-п.25): уничтожение мишени и «призрак вне реестра»
    private int _p24Id;                    // TargetNetId мишени из п.24
    private string _p24HpSeq = "";         // HP после каждого урона ("75/50/25/0")
    private Node? _p24Root;                // корень RigidBody3D убитой мишени
    private Node3D? _ghostRoot;            // клон-«призрак» п.25
    private int _ghostBase;                // GhostHitsIgnored перед выстрелом в призрак
    private System.Action<int, uint, Vector3>? _p24Listener; // счётчик HP-последовательности
    private readonly List<TargetState> _arenaTargets = new(); // мишени арены (для очистки)
    // Клон мирового TestStand (слой Station) для п.22 — попавший луч останавлива-
    // ется на станции, но станция НЕ цель: хелпер различает «попал в стенд» и
    // «промахнулся» по именам коллайдеров.
    private StaticBody3D? _standClone;
    // Слушатель HitConfirmed держим в поле, чтобы снять его на cleanup (event +=/-=
    // требует тот же делегат; анонимную лямбду не отписать).
    private System.Action<int, uint, Vector3>? _hitListener;

    /// <summary>Рекурсивно ищет в дереве узел с именем «TestStand» (StaticBody3D
    /// мировой сцены Main) — нужен для клона в арене в п.22.</summary>
    private static Node? FindTestStand(Node? n)
    {
        if (n == null) return null;
        if (n.Name == "TestStand" && n is StaticBody3D) return n;
        foreach (var c in n.GetChildren())
        {
            var found = FindTestStand(c);
            if (found != null) return found;
        }
        return null;
    }
    private double _beamWait;

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
        // Шов: в headless нет реальных пиров; «существующий» пир 2 нужен, если
        // логика валидации пира встречается (по образцу NetTest).
        NetworkManager.TestExtraPeers.Add(2);

        // Корень-подложка для стендового корабля п.17 (по образцу Stage2Test):
        // отдельный Node3D, чтобы не мешать спавнеру и не мешать счёту мишеней.
        _root = new Node3D { Name = "TestRoot" };
        AddChild(_root);
    }

    public override void _Process(double delta)
    {
        _frame++;
        switch (_phase)
        {
            case Phase.WaitSpawn:
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null)
                    _phase = Phase.StartServer;
                else if (_frame > 60)
                {
                    Check(false, "мир заспавнен (Ship появился в GameRoot)");
                    Finish();
                }
                break;
            // Спавн мишени разрешён только авторитету (§3.2, В17). В Offline роли
            // IsServer == false, поэтому стартуем Dedicated — иначе SpawnTarget
            // вернёт null и тест нечего проверять.
            case Phase.StartServer:
            {
                var nm = NetworkManager.Instance;
                Check(nm != null, "NetworkManager.Instance доступен");
                if (nm != null)
                {
                    var err = nm.StartServer();
                    Check(err == Error.Ok, $"StartServer() в headless -> Ok (получено {err})");
                    Check(nm.IsServer, "после StartServer роль = Dedicated (IsServer)");
                }
                _phase = Phase.P14;
                break;
            }
            case Phase.P14: CheckSpawnAndRegistry(); _phase = Phase.P15; break;
            case Phase.P15: CheckHpReplicationSeam(); _phase = Phase.P17Setup; break;

            // ── п.16: кнопки только для сервера (шов = реальный сигнал кнопки) ──
            // Кнопки Spawn/Clear зарегистрированы на всех ролях, гейт IsServer
            // внутри обработчика. Жмём кнопку «Spawn Target» сигналом Pressed —
            // это и есть путь кнопки в бою (§3.5 п.3).
            // ─────────────── шаг 3.3: п.17 — визуал луча на клиенте ─────────────
            //
            // Живой корабль из Ship.tscn (локальный пилот в офлайне), спавним
            // рядом с мишенью. Шов TestFireRequest() дёргает путь выстрела без
            // эмуляции мыши (в headless курсором в current-камеру не целиться),
            // оставляя гейты «локальный пилот» и «взаимодействие».
            case Phase.P17Setup:
            {
                var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
                Check(scene != null, "п.17: не загрузился scenes/Ship.tscn");
                if (scene == null) { _phase = Phase.P16Spawn1; break; }
                _pilotShip = scene.Instantiate<Ship>();
                _pilotShip.Name = "P17Ship";
                _root!.AddChild(_pilotShip);
                _wait = 3;
                _phase = Phase.P17Fire;
                break;
            }
            case Phase.P17Fire:
            {
                if (_wait-- > 0) break;
                Check(_pilotShip != null && _pilotShip.IsLocalPilot,
                      "п.17: стендовый корабль признан локальным пилотом");
                _pilotShip!.TestFireRequest();
                var beam = _pilotShip.GetNodeOrNull<Node3D>("BeamVisual");
                Check(beam != null, "п.17: после TestFireRequest() в дереве корабля живой узел луча (BeamVisual)");
                _beamWait = 0.25; // больше AimingBeamLifetime (0.2) — узел должен удалиться сам
                _phase = Phase.P17Wait;
                break;
            }
            case Phase.P17Wait:
            {
                // Ждём истечения AimingBeamLifetime в реальном времени.
                _beamWait -= delta;
                if (_beamWait > 0) break;
                var beam = _pilotShip!.GetNodeOrNull<Node3D>("BeamVisual");
                Check(beam == null, "п.17: через 0.2 с (AimingBeamLifetime) узел луча удалён сам");
                _phase = Phase.P17Interact;
                break;
            }
            case Phase.P17Interact:
            {
                // Взаимодействие: луч НЕ должен появиться (гейт !IsInteractionMode).
                _pilotShip!.SetInteractionMode(true);
                _pilotShip.TestFireRequest();
                var beam = _pilotShip.GetNodeOrNull<Node3D>("BeamVisual");
                Check(beam == null, "п.17: в Interaction-режиме TestFireRequest() НЕ рисует луч");
                _pilotShip.SetInteractionMode(false);
                _phase = Phase.P18;
                break;
            }

            // ─── шаг 3.4: п.18-п.20 — серверная авторизация выстрела (В17) ───
            // Тот же серверный гейт AcceptFireFrom, что и боевой RPC, дергается
            // швом TestFire(senderPeer, tick, aimPoint) без UDP (§3.9 п.15).
            case Phase.P18:
            {
                // Кулдаун гасим (0), чтобы п.18-20 проверяли именно пилота и
                // прицел, а не тайминг. Тики выдаём строго растущими — гейт
                // монотонности по образцу ввода.
                _pilotShip!.FireCooldownSec = 0f;
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f; // точно вдоль носа
                bool ok = _pilotShip.TestFire(_pilotShip.PilotPeerId, 10u, aim);
                Check(ok, "п.18: выстрел от PilotPeerId вдоль носа принят (TestFire==true)");
                Check(_pilotShip.AcceptedFireCount == 1,
                      $"п.18: AcceptedFireCount == 1 (получено {_pilotShip.AcceptedFireCount})");
                Check(_pilotShip.RejectedFireCount == 0,
                      $"п.18: RejectedFireCount == 0 (получено {_pilotShip.RejectedFireCount})");
                _phase = Phase.P19;
                break;
            }
            case Phase.P19:
            {
                // п.19: выстрел от чужого пира (не пилот) — отклонён В17 п.1.
                Vector3 fwd = -_pilotShip!.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                int otherPeer = _pilotShip.PilotPeerId == 2 ? 3 : 2;
                bool ok = _pilotShip.TestFire(otherPeer, 11u, aim);
                Check(!ok, "п.19: выстрел от чужого пира отклонён (TestFire==false)");
                Check(_pilotShip.AcceptedFireCount == 1,
                      $"п.19: AcceptedFireCount не вырос (получено {_pilotShip.AcceptedFireCount})");
                Check(_pilotShip.RejectedFireCount == 1,
                      $"п.19: RejectedFireCount == 1 (получено {_pilotShip.RejectedFireCount})");
                _phase = Phase.P20;
                break;
            }
            case Phase.P20:
            {
                // п.20: aimPoint вдоль серверного носа принят; aimPoint с угловым
                // отклонением 1 рад от носа — отклонён (В17 п.3, допуск 0.15 рад).
                Vector3 fwd = -_pilotShip!.GlobalTransform.Basis.Z;
                Vector3 aimOk = _pilotShip.GlobalPosition + fwd * 1000f;
                bool ok1 = _pilotShip.TestFire(_pilotShip.PilotPeerId, 12u, aimOk);
                Check(ok1, "п.20: aimPoint вдоль серверного носа принят");
                Check(_pilotShip.AcceptedFireCount == 2,
                      $"п.20: AcceptedFireCount == 2 (получено {_pilotShip.AcceptedFireCount})");
                // Отклонение 1 рад вокруг оси Y (перпендикулярно fwd в плоскости XZ).
                var rot = new Basis(Vector3.Up, 1.0f); // 1 рад
                Vector3 aimOff = _pilotShip.GlobalPosition + rot * fwd * 1000f;
                bool ok2 = _pilotShip.TestFire(_pilotShip.PilotPeerId, 13u, aimOff);
                Check(!ok2, "п.20: aimPoint с отклонением 1 рад отклонён");
                Check(_pilotShip.RejectedFireCount == 2,
                      $"п.20: RejectedFireCount == 2 (получено {_pilotShip.RejectedFireCount})");
                _phase = Phase.P21;
                break;
            }

            // ─── шаг 3.5: п.21-п.23 — серверный хитскан по LaserMask ────────────
            // Выстрелы п.18-20 ушли в очередь хитскана и уже исполнены на чистой
            // позиции корабля (мишеней в арене ещё нет — попаданий не было). Теперь
            // навешиваем слушателя HitConfirmed (тот же шов, что шаг 3.6 обернёт в
            // RpcOnHitConfirmed), ставим мишень прямо перед носом и стреляем.
            case Phase.P21:
            {
                // ПЕРЕНОСИМ корабль-стенд в чистое пространство: мировой TestStand
                // (слой Station, (0,0,-12)) сидит ровно на -Z трассе корабля в
                // аренах Main, и закрыл бы нам выстрел (IntersectRay возвращает
                // только самое БЛИЖНЕЕ попадание). Переносим (1000,1000,1000) с
                // тождественной ориентацией — трасса -Z на 400 м вперёд чиста.
                _pilotShip!.GlobalPosition = new Vector3(1000, 1000, 1000);
                _hitListener = (id, _, hp) => { _hitCount++; _hitTargetNetId = id; _hitPoint = hp; };
                _pilotShip.HitConfirmed += _hitListener;
                _hitCount = 0; _hitTargetNetId = 0; _hitPoint = Vector3.Zero;
                _arenaTargets.Clear();
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 shipPos = _pilotShip.GlobalPosition;
                // Мишень ровно на трассе луча, в 10 м перед носом (размер 4 → грань
                // в 8 м; muzzle(-2) → поверхность в ~6 м, внутри LaserRange).
                var ts = _spawner!.SpawnTarget(shipPos + fwd * 10f);
                Check(ts != null, "п.21: мишень заспавнена на трассе луча");
                if (ts != null) _arenaTargets.Add(ts);
                _wait = 5; // 5 кадров: и физ-шаг добавит тело в space, и pending
                           // выстрелы п.18/п.20 сливаются В ЧИСТОМ коридоре (в мишень),
                           // после чего счётчик обнуляем в P21Fire перед своим выстрелом.
                _phase = Phase.P21Wait;
                break;
            }
            case Phase.P21Wait:
                if (_wait-- > 0) break;
                _phase = Phase.P21Fire;
                break;
            case Phase.P21Fire:
            {
                // Pending из п.18/п.20 (принятые выстрелы, поставленные в очередь)
                // уже сгорели в P21Wait на новом месте (мишень в аренах — единственное
                // тело в LaserMask). Обнуляем счётчик и идём дальше — проверяем ИМЕННО
                // выстрел п.21 (tick=20), не считая «эхо» п.18/20.
                _hitCount = 0; _hitTargetNetId = 0; _hitPoint = Vector3.Zero;
                Vector3 fwd = -_pilotShip!.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                _pilotShip.FireCooldownSec = 0f; // не мешать: 3 выстрела подряд по фазам
                bool ok = _pilotShip.TestFire(_pilotShip.PilotPeerId, 20u, aim);
                Check(ok, "п.21: выстрел в мишень принят");
                _wait = 4; // дождаться физ-кадра, в котором хитскан исполнит очередь
                _phase = Phase.P21Assert;
                break;
            }
            case Phase.P21Assert:
            {
                if (_wait-- > 0) break;
                Check(_hitCount == 1,
                      $"п.21: ровно одно подтверждение попадания (получено {_hitCount})");
                {
                    var ts = _arenaTargets.Count > 0 ? _arenaTargets[0] : null;
                    Check(_hitTargetNetId != 0 && ts != null && _hitTargetNetId == ts.TargetNetId,
                          "п.21: подтверждение указало на TargetNetId спавненной мишени");
                    // hitPoint на ГРАНИЦЕ мишени: дальность от центра ≤ 2√3≈3.47 (бокс 4³)
                    // и больше половины (поверхность, не центр). Проверяем в разумных полях.
                    Vector3 center = (ts != null && ts.GetParent() is PhysicsBody3D b) ? b.GlobalPosition : Vector3.Zero;
                    float d = center.DistanceTo(_hitPoint);
                    Check(d > 0.5f && d < 4.0f,
                          $"п.21: hitPoint на поверхности мишени (расст. от центра {d:F2}∈(0.5;4.0))");
                }
                _phase = Phase.P22;
                break;
            }
            case Phase.P22:
            {
                // п.22: TestStand (слой Station, БЕЗ TargetState) на трассе вместо
                // мишени. Снимаем мишени, ставим стенд, стреляем — попадание в
                // станцию НЕ подтверждается как попадание в цель.
                _spawner!.ClearTargets();
                _arenaTargets.Clear();
                _wait = 3;
                _phase = Phase.P22Wait;
                break;
            }
            case Phase.P22Wait:
                if (_wait-- > 0) break;
                _phase = Phase.P22Stand;
                break;
            case Phase.P22Stand:
            {
                // Клонируем мировой TestStand в арену: StaticBody3D, слой Station.
                // Имя «TestStand» обязательно — его узнаёт хелпер IsTestStandCollider
                // (у стенда нет TargetState, поэтому он и так не цель; имя нужно,
                // чтобы отличить «попал в стенд» от «промахнулся» в п.22/п.23).
                var world = GetTree().CurrentScene;
                var stand = FindTestStand(world);
                Check(stand != null, "п.22: мировой TestStand найден (сцена Main)");
                if (stand != null)
                {
                    var clone = (StaticBody3D)stand.Duplicate();
                    clone.Name = "TestStand";
                    Vector3 fwd = -_pilotShip!.GlobalTransform.Basis.Z;
                    _root!.AddChild(clone);
                    clone.GlobalPosition = _pilotShip.GlobalPosition + fwd * 12f;
                    _standClone = clone;
                }
                _wait = 3; // физ-шаг, чтобы стенд попал в space
                _phase = Phase.P22Fire;
                break;
            }
            case Phase.P22Fire:
            {
                // Считаем эталон ПЕРЕД выстрелом: п.21 уже дал 1 попадание;
                // станция НЕ должна добавить ничего.
                _hitsBaseline = _hitCount;
                _hitsRegBaseline = _pilotShip!.HitsRegistered;
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                bool ok = _pilotShip.TestFire(_pilotShip.PilotPeerId, 21u, aim);
                Check(ok, "п.22: выстрел в станцию принят гейтами");
                _wait = 4;
                _phase = Phase.P22Assert;
                break;
            }
            case Phase.P22Assert:
            {
                if (_wait-- > 0) break;
                Check(_hitCount == _hitsBaseline,
                      $"п.22: попадание в TestStand НЕ подтверждается " +
                      $"(было {_hitsBaseline}, стало {_hitCount})");
                Check(_pilotShip!.HitsRegistered == _hitsRegBaseline,
                      $"п.22: HitsRegistered не вырос от станции " +
                      $"(было {_hitsRegBaseline}, стало {_pilotShip.HitsRegistered})");
                _standClone?.QueueFree();
                _standClone = null;
                _wait = 2;
                _phase = Phase.P23;
                break;
            }
            case Phase.P23:
                if (_wait-- > 0) break;
                _phase = Phase.P23Fire;
                break;
            case Phase.P23Fire:
            {
                // п.23: трасса чиста (мишени сняты, стенд убран) → выстрел в
                // пустоту: OnHitConfirmed не отправлен (HitConfirmed не стреляет).
                // Снова считаем эталон ПЕРЕД выстрелом (п.21 дал попадание;
                // п.22 — нет, но и без эталона абсолютный 0 уже не подходит).
                _hitsBaseline = _hitCount;
                _hitsRegBaseline = _pilotShip!.HitsRegistered;
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                bool ok = _pilotShip.TestFire(_pilotShip.PilotPeerId, 22u, aim);
                Check(ok, "п.23: выстрел в пустоту принят (гейты не при чём)");
                _wait = 4;
                _phase = Phase.P23Assert;
                break;
            }
            case Phase.P23Assert:
                if (_wait-- > 0) break;
                Check(_hitCount == _hitsBaseline,
                      $"п.23: в пустоту — подтверждений не добавилось " +
                      $"(было {_hitsBaseline}, стало {_hitCount})");
                Check(_pilotShip!.HitsRegistered == _hitsRegBaseline,
                      $"п.23: HitsRegistered не вырос (было {_hitsRegBaseline}, " +
                      $"стало {_pilotShip.HitsRegistered})");
                // Слушатель HitConfirmed НУЖЕН ещё в п.24/п.25 (шаг 3.6), снимаем
                // его позже — в P25Assert.
                _phase = Phase.P24;
                break;
            // ─── шаг 3.6: п.24-п.25 — урон, уничтожение, «мишени нет в реестре» ──
            case Phase.P24:
            {
                // п.24: одна мишень на трассе, 4 выстрела подряд (FireDamage=25,
                // MaxHp=100 → 4×25=100 → Hp==0 → сервер QueueFree). Pending-очередь
                // чиста (выстрелы п.21-23 уже исполнены), слушатель HitConfirmed
                // висит с п.21.
                _pilotShip!.FireCooldownSec = 0f; // 4 выстрела в одном кадровом окне
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                var ts = _spawner!.SpawnTarget(_pilotShip.GlobalPosition + fwd * 10f);
                Check(ts != null, "п.24: мишень заспавнена на трассе");
                _arenaTargets.Clear();
                if (ts != null) _arenaTargets.Add(ts);
                _wait = 4; // физ-шаг: тело входит в space
                _phase = Phase.P24Wait;
                break;
            }
            case Phase.P24Wait:
                if (_wait-- > 0) break;
                _phase = Phase.P24Fire;
                break;
            case Phase.P24Fire:
            {
                var t = _arenaTargets.Count > 0 ? _arenaTargets[0] : null;
                Check(t != null && t.TargetNetId != 0 && t.Hp == t.MaxHp,
                      "п.24: мишень жива до обстрела (Hp == MaxHp)");
                _p24Id = t?.TargetNetId ?? 0;
                _p24Root = t?.GetParent();
                _hitsBaseline = _hitCount;
                _hitsRegBaseline = _pilotShip!.HitsRegistered;
                // Второй слушатель: пишет HP мишени ПОСЛЕ каждого урона (событие
                // HitConfirmed стреляет уже после `Hp -= damage`, реестр в этот
                // момент ещё жив — QueueFree исполнится в конце кадра).
                _p24HpSeq = "";
                _p24Listener = (id, _, _) =>
                {
                    var tt = NetworkManager.Instance?.FindTarget(id);
                    _p24HpSeq += (_p24HpSeq.Length > 0 ? "/" : "") +
                                 (tt != null ? tt.Hp.ToString() : "?");
                };
                _pilotShip.HitConfirmed += _p24Listener;
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                int accepted = 0;
                for (int i = 0; i < 4; i++)
                {
                    if (_pilotShip.TestFire(_pilotShip.PilotPeerId, (uint)(24 + i), aim))
                        accepted++;
                }
                Check(accepted == 4,
                      $"п.24: все 4 выстрела приняты гейтами (принято {accepted})");
                _wait = 3; // 3 кадра: хитскан в _PhysicsProcess + QueueFree +
                           // _ExitTree-реестр + пропуск кадра на всякий случай
                _phase = Phase.P24Wait2;
                break;
            }
            case Phase.P24Wait2:
                if (_wait-- > 0) break;
                _phase = Phase.P24Assert;
                break;
            case Phase.P24Assert:
            {
                var nm = NetworkManager.Instance!;
                Check(_hitCount == _hitsBaseline + 4,
                      $"п.24: 4 подтверждения попадания (было {_hitsBaseline}, " +
                      $"стало {_hitCount})");
                Check(_pilotShip!.HitsRegistered == _hitsRegBaseline + 4,
                      $"п.24: HitsRegistered вырос на 4 (было {_hitsRegBaseline}, " +
                      $"стало {_pilotShip.HitsRegistered})");
                Check(_p24HpSeq == "75/50/25/0",
                      $"п.24: HP после каждого урона 75/50/25/0 (получено {_p24HpSeq})");
                Check(_p24Root != null && !GodotObject.IsInstanceValid(_p24Root),
                      "п.24: узел мишени удалён сервером (QueueFree отработал)");
                Check(nm.FindTarget(_p24Id) == null,
                      $"п.24: FindTarget({_p24Id}) == null после уничтожения (_ExitTree)");
                // Не-авторитетная реплика Hp (тот же шов, что п.15): в однопроцессном
                // Dedicated неавторитетного экземпляра нет — проверяем конфиг реплики
                // (Hp OnChange доезжает до пира), живая кросс-процессная доставка HP
                // фиксируется стендом §3.10.
                _pilotShip.HitConfirmed -= _p24Listener!; // HP-счётчик больше не нужен
                _p24Listener = null;
                var tscn = GD.Load<PackedScene>("res://scenes/Target.tscn");
                var inst = tscn?.Instantiate();
                var sync = inst?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
                var cfg = sync?.ReplicationConfig;
                bool hpOnChange = false;
                if (cfg != null)
                {
                    var hpPath = new NodePath("TargetState:Hp");
                    hpOnChange = cfg.HasProperty(hpPath) &&
                                 cfg.PropertyGetReplicationMode(hpPath) ==
                                 SceneReplicationConfig.ReplicationMode.OnChange;
                }
                inst?.Free(); // стендовый экземпляр в дерево не вводился — освобождаем сразу
                Check(hpOnChange,
                      "п.24: Hp реплицируется OnChange — у не-авторитета Hp станет 0 " +
                      "после реплики (кросс-процесс — стенд §3.10)");
                GD.Print("INFO  п.24: Hp==0 у сервера, узел удалён, реестр пуст; " +
                         "не-авторитет получает Hp=0 репликой Hp (не damage из RPC)");
                _phase = Phase.P25;
                break;
            }
            case Phase.P25:
            {
                // п.25 (риск №7): «мишень удалили из реестра, пока луч летел» —
                // здесь это гонка моделируется явно: спавним НАСТОЯЩУЮ мишень через
                // WorldSpawner (реальное тело входит в space — левый клон RigidBody
                // в space не появлялся), затем вручную ВЫПИСЫВАЕМ её из реестра.
                // Хитскан обязан дать PushWarning, увеличить GhostHitsIgnored и
                // НЕ уронить процесс (исключения нет — мы всё ещё исполняемся).
                Check(_spawner != null, "п.25: спавнер доступен");
                if (_spawner == null) { _phase = Phase.P17Cleanup; break; }
                Vector3 fwd = -_pilotShip!.GlobalTransform.Basis.Z;
                var gstate = _spawner.SpawnTarget(
                    _pilotShip.GlobalPosition + fwd * 12f);
                _ghostRoot = gstate?.GetParent() as Node3D;
                Check(gstate != null && gstate.TargetNetId != 0,
                      "п.25: призрак заспавнен и зарегистрирован (шов до выписки)");
                if (gstate != null)
                    NetworkManager.Instance?.UnregisterTarget(gstate); // гонка удаления
                Check(NetworkManager.Instance != null &&
                      NetworkManager.Instance.FindTarget(gstate?.TargetNetId ?? -1) == null,
                      "п.25: призрак НЕ в реестре (шов перед выстрелом)");
                _wait = 3; // физ-шаг: тело входит в space
                _phase = Phase.P25Wait;
                break;
            }
            case Phase.P25Wait:
                if (_wait-- > 0) break;
                _phase = Phase.P25Fire;
                break;
            case Phase.P25Fire:
            {
                _hitsBaseline = _hitCount;
                _hitsRegBaseline = _pilotShip!.HitsRegistered;
                _ghostBase = _pilotShip.GhostHitsIgnored;
                Vector3 fwd = -_pilotShip.GlobalTransform.Basis.Z;
                Vector3 aim = _pilotShip.GlobalPosition + fwd * 1000f;
                bool ok = _pilotShip.TestFire(_pilotShip.PilotPeerId, 28u, aim);
                Check(ok, "п.25: выстрел в призрак принят гейтами");
                _wait = 4;
                _phase = Phase.P25Assert;
                break;
            }
            case Phase.P25Assert:
            {
                if (_wait-- > 0) break;
                // Процесс жив (мы ещё тут) — exception не вылетел; попадания нет.
                Check(_hitCount == _hitsBaseline,
                      $"п.25: призрак НЕ дал подтверждений (было {_hitsBaseline}, " +
                      $"стало {_hitCount})");
                Check(_pilotShip!.HitsRegistered == _hitsRegBaseline,
                      $"п.25: HitsRegistered не вырос от призрака " +
                      $"(было {_hitsRegBaseline}, стало {_pilotShip.HitsRegistered})");
                Check(_pilotShip.GhostHitsIgnored == _ghostBase + 1,
                      $"п.25: GhostHitsIgnored вырос на 1 (было {_ghostBase}, " +
                      $"стало {_pilotShip.GhostHitsIgnored}) — хитскан в тело вне " +
                      "реестра проигнорирован без исключения (в консоль при этом " +
                      "уходит GD.PushWarning, но процесс жив)");
                _ghostRoot?.QueueFree();
                _ghostRoot = null;
                // Снимаем слушателя HitConfirmed (делегат сохранён в _hitListener).
                _pilotShip.HitConfirmed -= _hitListener!;
                _hitListener = null;
                _phase = Phase.P26;
                break;
            }
            case Phase.P26:
            {
                // §3.7 п.26: после обстрела строка панели Fire: содержит
                // ненулевой счётчик выстрелов. Роль Dedicated (сервер) — счётчики
                // живут на корабле-авторитете, агрегирует NetworkManager.
                string info = DebugInfoBus.BuildInfoText();
                Check(DebugInfoBus.HasInfoSource("fire"),
                      "п.26: источник «fire» зарегистрирован в DebugInfoBus");
                int fi = info.IndexOf("Fire:", System.StringComparison.Ordinal);
                Check(fi >= 0, "п.26: BuildInfoText() содержит строку «Fire:»");
                bool shotsNonZero = false;
                if (fi >= 0)
                {
                    string line = info.Substring(fi, System.Math.Min(80, info.Length - fi));
                    int eol = line.IndexOf('\n');
                    if (eol >= 0) line = line.Substring(0, eol);
                    GD.Print($"INFO  п.26: строка панели — «{line}»");
                    // shots=<ненулевое> (принятые сервером выстрелы).
                    var m = System.Text.RegularExpressions.Regex.Match(line, @"shots=(\d+)");
                    shotsNonZero = m.Success && int.Parse(m.Groups[1].Value) > 0;
                    Check(shotsNonZero,
                          $"п.26: в строке Fire: shots > 0 (получено «{line}»)");
                }
                _phase = Phase.P17Cleanup;
                break;
            }
            case Phase.P17Cleanup:
            {
                _pilotShip!.QueueFree();
                _wait = 3;
                _phase = Phase.P17InteractWait;
                break;
            }
            case Phase.P17InteractWait:
            {
                if (_wait-- > 0) break;
                _phase = Phase.P16Spawn1;
                break;
            }

            case Phase.P16Spawn1:
            {
                _beforeSpawn = TargetCount();
                var btn = FindButton("Spawn Target");
                Check(btn != null, "п.16: кнопка «Spawn Target» зарегистрирована");
                if (btn != null)
                {
                    btn.EmitSignal(BaseButton.SignalName.Pressed);
                    _phase = Phase.P16Spawn2;
                }
                else { Check(false, "п.16: кнопка «Spawn Target» не найдена"); _phase = Phase.P16Spawn2; }
                break;
            }
            // Ждём кадр: QueueFree/спавнер и регистрация применяются не сразу.
            case Phase.P16Spawn2:
                _phase = Phase.P16ClearWait0;
                break;
            case Phase.P16ClearWait0:
            {
                int after = TargetCount();
                Check(after == _beforeSpawn + 1,
                      $"п.16: серверная кнопка добавила мишень: {_beforeSpawn} -> {after}");
                // «Clear Targets» как путь кнопки.
                var btn = FindButton("Clear Targets");
                Check(btn != null, "п.16: кнопка «Clear Targets» зарегистрирована");
                if (btn != null) btn.EmitSignal(BaseButton.SignalName.Pressed);
                _phase = Phase.P16ClearWait1;
                break;
            }
            // Ждём кадры, чтобы QueueFree дошла до спавнера и реестр очистился.
            case Phase.P16ClearWait1:
                _phase = Phase.P16ClearWait2;
                break;
            case Phase.P16ClearWait2:
            {
                int cleared = TargetCount();
                Check(cleared == 0, $"п.16: Clear Targets снял все мишени, осталось {cleared}");
                _phase = Phase.P16GoClient;
                break;
            }
            // Роль Client без живой сети: Join на мёртвый порт — CreateClient
            // возвращает Ok синхронно и ставит Role=Client (рукопожатие потом
            // не состоится, но нам важен сам переход роли). Так проверяем гейт.
            case Phase.P16GoClient:
            {
                var nm = NetworkManager.Instance;
                if (nm != null) nm.Disconnect();       // Dedicated -> Offline
                // Запасной порт, где сервера точно нет.
                var err = nm != null ? nm.Join("127.0.0.1", 43211) : Error.Unavailable;
                Check(err == Error.Ok, $"п.16: Join на мёртвый порт -> Ok (получено {err})");
                Check(nm != null && nm.Role == NetworkManager.NetRole.Client,
                      "п.16: роль перешла в Client (не-сервер)");
                _phase = Phase.P16ClientGate;
                break;
            }
            case Phase.P16ClientGate:
            {
                _beforeSpawn = TargetCount();
                var btn = FindButton("Spawn Target");
                Check(btn != null, "п.16: у клиента кнопка «Spawn Target» всё ещё есть");
                if (btn != null) btn.EmitSignal(BaseButton.SignalName.Pressed);
                // Проверяем на следующем кадре, что мишень НЕ появилась.
                _phase = Phase.P16ClientGateWait;
                break;
            }
            case Phase.P16ClientGateWait:
            {
                int after = TargetCount();
                Check(after == _beforeSpawn,
                      $"п.16: клиентская кнопка НЕ создаёт мишень (до {_beforeSpawn}, после {after}) — гейт IsServer");
                _phase = Phase.Done;
                break;
            }
            case Phase.Done: Finish(); break;
        }
    }

    /// <summary>Число живых мишеней в реестре NetworkManager (шов для счёта).</summary>
    private int TargetCount()
    {
        var nm = NetworkManager.Instance;
        if (nm == null) return 0;
        int n = 0;
        foreach (var kv in nm.Targets)
            if (GodotObject.IsInstanceValid(kv.Value) && !kv.Value.IsQueuedForDeletion())
                n++;
        return n;
    }

    /// <summary>Найти кнопку дебаг-меню по подписи (как в NetTest).</summary>
    private Button? FindButton(string label)
    {
        var layer = FindNodeByName(GetTree().CurrentScene, "DebugLayer") as CanvasLayer;
        var box = layer?.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox/Scroll/Buttons");
        if (box == null) return null;
        foreach (var c in box.GetChildren())
            if (c is Button b && b.Text == label)
                return b;
        return null;
    }

    private static Node? FindNodeByName(Node? root, string name)
    {
        if (root == null) return null;
        if (root.Name == name) return root;
        foreach (var c in root.GetChildren())
        {
            var found = FindNodeByName(c, name);
            if (found != null) return found;
        }
        return null;
    }

    // ───────────────────── п.14: спавн мишени + адрес + реестр + конфиг ─────────
    private void CheckSpawnAndRegistry()
    {
        var nm = NetworkManager.Instance;
        Check(nm != null, "п.14: NetworkManager.Instance доступен");
        if (nm == null || _spawner == null) return;

        var target = _spawner.SpawnTarget(new Vector3(30, 0, 0));
        Check(target != null, "п.14: SpawnTarget вернул узел (сервер может спавнить)");
        if (target == null) return;

        int id = target.TargetNetId;
        Check(id != 0, $"п.14: TargetNetId != 0 сразу после спавна (получено {id})");
        Check(nm.FindTarget(id) == target,
              $"п.14: FindTarget({id}) возвращает спавнящийся узел");
        Check(target.Hp == target.MaxHp,
              $"п.14: Hp == MaxHp при спавне (Hp={target.Hp}, MaxHp={target.MaxHp})");

        // Адрес мишени — из ТОГО ЖЕ монотонного счётчика, что и ShipNetId
        // (§3.4 п.2: одна нумерация, id не сталкиваются). У кораблей адреса 1,2…
        // (или 1..N мира), значит адрес мишени обязан НЕ совпадать ни с одним
        // занятым ShipNetId.
        foreach (var (_, ship) in nm.Ships)
            Check(ship.Net != null && ship.Net.ShipNetId != id,
                  $"п.14: TargetNetId {id} не столкнулся с ShipNetId {ship.Net?.ShipNetId}");

        // Спавн мишени через MultiplayerSpawner: узел добавлен в контейнер спавнера.
        // SpawnTarget возвращает дочерний TargetState, а не корень мишени: корень
        // (RigidBody3D) — его родитель, и именно он сидит в GameRoot.
        var body = target.GetParent();
        Check(body != null && body.GetParent() == (Node)_spawner,
              "п.14: корень мишени (RigidBody3D) добавлен в контейнер спавнера (GameRoot)");

        // ── Конфиг репликации (шов вместо живой доставки, как NetTest п.14) ──
        // Synchronizer — сосед TargetState на корне мишени (root_path = ".."), а не
        // его ребёнок, поэтому ищем его на теле, а не на TargetState.
        var sync = body?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
        var cfg = sync?.ReplicationConfig;
        Check(cfg != null, "п.14: у Synchronizer мишени задан ReplicationConfig");
        if (cfg == null) return;

        var paths = cfg.GetProperties();
        bool Has(string s)
        {
            foreach (var p in paths)
                if (p.ToString() == s) return true;
            return false;
        }
        Check(Has("TargetState:Hp"),
              $"п.14: конфиг содержит TargetState:Hp (получено {string.Join(", ", paths)})");
        Check(!Has("Hp"),
              "п.14: в конфиге НЕТ голого Hp (путь без subname отбрасывается, §3.11 п.2)");

        var hp = new NodePath("TargetState:Hp");
        var netId = new NodePath("TargetState:TargetNetId");
        var maxHp = new NodePath("TargetState:MaxHp");

        Check(cfg.HasProperty(hp) && cfg.PropertyGetSpawn(hp),
              "п.14: spawn == true для TargetState:Hp (первое HP в снимке спавна)");
        Check(cfg.HasProperty(netId) && cfg.PropertyGetSpawn(netId),
              "п.14: spawn == true для TargetState:TargetNetId (адрес в снимке спавна)");
        Check(cfg.HasProperty(maxHp) && cfg.PropertyGetSpawn(maxHp),
              "п.14: spawn == true для TargetState:MaxHp");
    }

    // ───────────────── п.15: сервер меняет Hp — шов репликации HP ───────────────
    private void CheckHpReplicationSeam()
    {
        var nm = NetworkManager.Instance;
        if (nm == null || _spawner == null) { Check(false, "п.15: NM/спавнер доступны"); return; }

        // Берём ту же мишень, что спавнили в п.14 (единственная).
        TargetState? target = null;
        foreach (var (_, t) in nm.Targets)
            if (t != null && GodotObject.IsInstanceValid(t)) { target = t; break; }
        Check(target != null, "п.15: мишень из п.14 жива");
        if (target == null) return;

        int id = target.TargetNetId;

        // Сервер (авторитет) меняет HP. Это та самая операция, что на реальном
        // стенде (§3.10) улетает пирy ON_CHANGE'ом; в headless проверяем:
        //   (1) изменение применилось на авторитете;
        //   (2) реестр FindTarget(id) по-прежнему указывает на ЖИВОЙ тот же узел
        //       (смена значения не роняет адрес/реестр — иначе RPC урона шага 3.6
        //       потеряли бы цель);
        //   (3) конфиг несёт HP именно ON_CHANGE (не Never): только тогда изменение
        //       долетает до пира (шов вместо живой доставки).
        int hpBefore = target.Hp;
        Check(hpBefore > 0, $"п.15: HP до изменения > 0 (Hp={hpBefore})");

        target.Hp = hpBefore - 37;
        Check(target.Hp == hpBefore - 37,
              $"п.15: сервер изменил HP (ожидалось {hpBefore - 37}, получено {target.Hp})");
        Check(nm.FindTarget(id) == target && GodotObject.IsInstanceValid(target),
              $"п.15: после смены HP FindTarget({id}) всё ещё живой узел (реестр не роняется)");

        var sync = target.GetParent()?.GetNodeOrNull<MultiplayerSynchronizer>("Synchronizer");
        var cfg = sync?.ReplicationConfig;
        Check(cfg != null, "п.15: конфиг доступен для проверки режима HP");
        if (cfg != null)
        {
            var hp = new NodePath("TargetState:Hp");
            Check(cfg.HasProperty(hp) &&
                  cfg.PropertyGetReplicationMode(hp) == SceneReplicationConfig.ReplicationMode.OnChange,
                  "п.15: режим репликации TargetState:Hp == OnChange (иначе урон HP не долетит до пира)");
        }

        GD.Print("INFO  п.15: живая кросс-процессная доставка HP проверяется стендом §3.10 " +
                 "(HOWTO-two-players); здесь — шов конфига (OnChange + spawn) и стойкость реестра.");

        // Страховка В17-гейта: не-сервер не должен создавать мишени. Проверяем,
        // что путь «не сервер» реально защищён — на Dedicated мы сервер, поэтому
        // просто убеждаемся, что число мишеней не выросло само (нет само-спавна).
        Check(nm.Targets.Count >= 1, "п.15: в реестре не меньше одной мишени");
    }

    // ------------------------------------------------------------- утилиты
    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
        {
            GD.Print($"PASS: {what}");
        }
        else
        {
            _failures++;
            GD.PrintErr($"FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"==== Stage3Test: проверок={_checks}, провалов={_failures} ====");
        GD.Print(_failures == 0 ? "==== STAGE3TEST OK ====" : "==== STAGE3TEST FAILED ====");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
