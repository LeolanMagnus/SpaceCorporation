using Godot;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест Этапа 4: станция как статичный объект, HP корабля как «правда
/// сервера», чистая функция урона, серверный учёт контакта, смерть по Hp&lt;=0,
/// god mode только сервером и главный критерий — «клиент не может отменить урон».
/// Покрывает п.1–п.27 плана stage4 (§3.8). Запуск:
///   godot --headless --path . res://tests/Stage4Test.tscn
///
/// Живой UDP-сетью в headless не проверяем (ENet-пиры к себе не подключаются,
/// §3.10), поэтому «доставку реплики HP/god» проверяем ШВОМ: инспекцией конфига
/// SceneReplicationConfig (п.9: OnChange + spawn=true) и in-process копией
/// не-авторитета через TestSimulateAuthorityOwnerForTest(999) (п.11/п.27).
/// Физика арены — детерминированной подстановкой позы/скорости и шагом
/// TestStepForTest (как в пробах tests/probes/probe_ci_*.tscn).
/// </summary>
public partial class Stage4Test : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private int _wait;
    private Node? _main;
    private WorldSpawner? _spawner;
    // Подложка для стендовых узлов арены (отдельно от GameRoot спавнера, чтобы
    // не мешать счёту станций/кораблей в реестре).
    private Node3D? _root;

    private enum Phase
    {
        WaitSpawn, StartServer,
        P1, P2, P3, P5count, P4, P6, P7, P8, P9, P10, P11,
        P12to16,
        // физика арены (п.17-п.21, п.25): warmup → setup → step → assert
        ArenaSetup, Warmup,
        P17Setup, P17Step, P17Assert,
        P18Setup, P18Step, P18Assert,
        P19Setup, P19Step1, P19Step2, P19Assert,
        P20Setup, P20Step, P20Assert,
        P21Setup, P21Step, P21Assert,
        P22, P23, P24,
        P25Setup, P25Step, P25Assert,
        P26, P27, Done
    }
    private Phase _phase = Phase.WaitSpawn;

    // Арена: ручной Station + корабль стенда.
    private StaticBody3D? _arenaStation;
    private StationState? _arenaStationState;
    private Ship? _arenaShip;

    // п.24: отдельный авторитетный корабль для god-mode шва.
    private Ship? _godShip;

    // ─────────────────────────────── запуск ───────────────────────────────
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
        // В headless нет реальных пиров; пир 2 «существует» для проверок наличия
        // пира (по образцу NetTest/Stage3Test).
        NetworkManager.TestExtraPeers.Add(2);

        _root = new Node3D { Name = "TestRoot" };
        AddChild(_root);
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        switch (_phase)
        {
            case Phase.WaitSpawn:
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null)
                    _phase = Phase.StartServer;
                else if (_frame > 90)
                {
                    Check(false, "мир заспавнен (Ship появился в GameRoot)");
                    Finish();
                }
                break;

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
                _phase = Phase.P1;
                break;
            }

            case Phase.P1:    CheckStationScene();  _phase = Phase.P2;    break;
            case Phase.P2:    CheckStationNetId();  _phase = Phase.P3;    break;
            case Phase.P3:    CheckRegistry();      _phase = Phase.P5count; break;
            case Phase.P5count: CountOfflineStations(); _phase = Phase.P4; break;
            case Phase.P4:    TwoSpawnNoCollision(); _phase = Phase.P6;   break;
            case Phase.P6:    CheckStationSpawnConfig(); _phase = Phase.P7; break;
            case Phase.P7:    InfoRecRate();       _phase = Phase.P8;     break;
            case Phase.P8:    CheckStationNotInSpatialIndex(); _phase = Phase.P9; break;
            case Phase.P9:    CheckHealthReplicationConfig(); _phase = Phase.P10; break;
            case Phase.P10:   CheckOfflineHp();    _phase = Phase.P11;    break;
            case Phase.P11:   CheckNonAuthWriteGuard(); _phase = Phase.P12to16; break;
            case Phase.P12to16: CheckPureDamageFn(); _phase = Phase.ArenaSetup; break;

            case Phase.ArenaSetup: BuildArena(); _wait = 4; _phase = Phase.Warmup; break;
            case Phase.Warmup:
                if (_wait-- > 0) break;
                _phase = Phase.P17Setup;
                break;

            case Phase.P17Setup: RamArena(new Vector3(1938, 0, 0), 40f, hp: 100, god: false); _phase = Phase.P17Step; break;
            case Phase.P17Step:  _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P17Assert; break;
            case Phase.P17Assert:
            {
                var h = _arenaShip!.Health;
                Check(h != null, "п.17: ShipHealthState на арена-корабле есть");
                if (h != null)
                {
                    Check(h.Hp == 44, $"п.17: 40 м/с в станцию -> Hp = 100-56 = 44 (получено {h.Hp})");
                    Check(h.DamageTakenCount == 1,
                          $"п.17: DamageTakenCount == 1 (получено {h.DamageTakenCount})");
                }
                Check(_arenaShip.LinearVelocity.X < 0,
                      $"п.17: LinearVelocity развёрнуто по нормали (отскок), X<0 (получено X={_arenaShip.LinearVelocity.X})");
                _phase = Phase.P18Setup;
                break;
            }

            case Phase.P18Setup: RamArena(new Vector3(1938, 0, 0), 2f, hp: 100, god: false); _phase = Phase.P18Step; break;
            case Phase.P18Step:  _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P18Assert; break;
            case Phase.P18Assert:
            {
                var h = _arenaShip!.Health;
                Check(h != null && h.Hp == 100,
                      $"п.18: медленное касание 2 м/с -> урона нет, Hp==100 (получено {h?.Hp})");
                Check(h != null && h.DamageTakenCount == 0,
                      $"п.18: DamageTakenCount == 0 при 2 м/с (получено {h?.DamageTakenCount})");
                _phase = Phase.P19Setup;
                break;
            }

            case Phase.P19Setup: RamArena(new Vector3(1960, 0, 0), 40f, hp: 100, god: false); _phase = Phase.P19Step1; break;
            case Phase.P19Step1:
                _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P19Step2; break;
            case Phase.P19Step2:
                // Второй шаг подряд в той же зоне: кулдаун (0.5 с) не должен дать
                // повторного урона, счётчик не растёт.
                _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P19Assert; break;
            case Phase.P19Assert:
            {
                var h = _arenaShip!.Health;
                Check(h != null && h.DamageTakenCount == 1,
                      $"п.19: 2-й кадр в кулдауне — счётчик не вырос (DamageTakenCount==1, получено {h?.DamageTakenCount})");
                _phase = Phase.P20Setup;
                break;
            }

            case Phase.P20Setup: RamArena(new Vector3(1938, 0, 0), 40f, hp: 100, god: true); _phase = Phase.P20Step; break;
            case Phase.P20Step:  _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P20Assert; break;
            case Phase.P20Assert:
            {
                var h = _arenaShip!.Health;
                Check(h != null && h.Hp == 100,
                      $"п.20: God Mode — урона нет, Hp==100 (получено {h?.Hp})");
                Check(_arenaShip!.LinearVelocity.X < 0,
                      "п.20: отскок при God Mode сохраняется (vel.X<0)");
                _phase = Phase.P21Setup;
                break;
            }

            case Phase.P21Setup: RamArena(new Vector3(1938, 0, 0), 40f, hp: 10, god: false); _phase = Phase.P21Step; break;
            case Phase.P21Step:
                _arenaShip!.TestStepForTest(FlightModel.FixedDt);
                // Смерть (QueueFree) обрабатывается в конце физкадра — читаем
                // PilotPeerId/IsQueuedForDeletion, пока узел ещё жив в ЭТОМ кадре.
                _p21PilotBefore = _arenaShip.PilotPeerId;
                _p21PilotWasZero = _arenaShip.IsQueuedForDeletion() && _arenaShip.PilotPeerId == 0;
                _phase = Phase.P21Assert; break;
            case Phase.P21Assert:
            {
                // Hp=10 - 56 <= 0 -> ServerDieShip: пилот снят (0) + QueueFree.
                bool dead = _arenaShip == null || !GodotObject.IsInstanceValid(_arenaShip)
                            || _arenaShip.IsQueuedForDeletion();
                Check(dead, "п.21: серверная смерть (Hp<=0) — корабль удалён (IsQueuedForDeletion/освобождён)");
                Check(_arenaShip != null && GodotObject.IsInstanceValid(_arenaShip)
                      ? _arenaShip.PilotPeerId == 0
                      : _p21PilotWasZero,
                      $"п.21: PilotPeerId == 0 после смерти (получено {_p21PilotBefore})");
                _arenaShip = null;
                _phase = Phase.P22;
                break;
            }

            case Phase.P22: CheckNonAuthDeathNoFree(); _phase = Phase.P23; break;
            case Phase.P23: CheckGodButtonServerOnly(); _phase = Phase.P24; break;
            case Phase.P24: CheckGodModeServerSeam(); _phase = Phase.P25Setup; break;

            case Phase.P25Setup: RamArena(new Vector3(1938, 0, 0), 60f, hp: 100, god: true); _phase = Phase.P25Step; break;
            case Phase.P25Step:  _arenaShip!.TestStepForTest(FlightModel.FixedDt); _phase = Phase.P25Assert; break;
            case Phase.P25Assert:
            {
                var h = _arenaShip!.Health;
                Check(h != null && h.Hp == 100,
                      $"п.25: God Mode + 60 м/с в станцию -> Hp==100 (получено {h?.Hp})");
                _phase = Phase.P26;
                break;
            }

            case Phase.P26: CheckHpInDebugInfo(); _phase = Phase.P27; break;
            case Phase.P27: CheckClientCannotCancelDamage(); _phase = Phase.Done; break;

            case Phase.Done: Finish(); break;
        }
    }
}
