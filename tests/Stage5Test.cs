using Godot;
using System.Linq;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест Этапа 5 (стадия 5, В20): «Стыковка с серверной авторитетностью».
/// Покрывает п.1–п.36 плана stage5 (§2). Запуск:
///   godot --headless --path . res://tests/Stage5Test.tscn
///
/// Как в Stage4Test: живой UDP-сетью в headless не проверяем (ENet-пиры к себе
/// не подключаются), поэтому доставку реплик — шВОМ (инспекция
/// SceneReplicationConfig + in-process смена авторитета через
/// TestSimulateAuthorityOwnerForTest). Физика захвата — детерминированной
/// подстановкой позы/скорости и серверным шагом (TestStepForTest).
/// Блок A (п.1–п.9): чистые функции, свойства станции/корабля, оба конфига,
/// реестр DockRequests. Порядок фаз — карта ниже; тела проверок — в
/// Stage5Test.Helpers.cs.
/// </summary>
public partial class Stage5Test : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private int _wait;
    private Node? _main;
    private WorldSpawner? _spawner;
    // Подложка для стендовых узлов арены (отдельно от GameRoot спавнера).
    private Node3D? _root;

    private enum Phase
    {
        WaitSpawn, StartServer,
        P1, P2, P3, P4, P5, P6, P7, P8,
        P9, P10, P11, P12, P13,
        P14, P15, P16, P17, P18, P19,
        P20, P21, P22,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

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
        // пира (по образцу NetTest/Stage3Test/Stage4Test).
        NetworkManager.TestExtraPeers.Add(2);

        _root = new Node3D { Name = "TestRoot" };
        AddChild(_root);

        // Страховка от «пустой .tres» (§3.1): печатаем пути обоих конфигов при
        // старте, чтобы «огни не загораются» было видно сразу в логе.
        DumpReplicationConfig("res://scenes/StationReplication.tres");
        DumpReplicationConfig("res://scenes/ShipReplication.tres");
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

            case Phase.P1: CheckPureDockingFn();   _phase = Phase.P2; break;
            case Phase.P2: CheckStationDefaults(); _phase = Phase.P3; break;
            case Phase.P3: CheckSetOnServer();     _phase = Phase.P4; break;
            case Phase.P4: CheckNonAuthReject();   _phase = Phase.P5; break;
            case Phase.P5: CheckDockReplicationConfigs(); _phase = Phase.P6; break;
            case Phase.P6: CheckBeginDockInvariant(); _phase = Phase.P7; break;
            case Phase.P7: CheckAbortDock();          _phase = Phase.P8; break;
            case Phase.P8: CheckStationBusyReject();  _phase = Phase.P9; break;
            case Phase.P9: CheckDockAcceptFlow();   _phase = Phase.P10; break;
            case Phase.P10: CheckDockRejectGates(); _phase = Phase.P11; break;
            case Phase.P11: CheckDockZoneSpeed();   _phase = Phase.P12; break;
            case Phase.P12: CheckDockWindowExpiry(); _phase = Phase.P13; break;
            case Phase.P13: CheckDockBeaconLights(); _phase = Phase.P14; break;
            case Phase.P14: CheckBeaconOnReplica();      _phase = Phase.P15; break;
            case Phase.P15: CheckDockSpringCapture();    _phase = Phase.P16; break;
            case Phase.P16: CheckDockLock();             _phase = Phase.P17; break;
            case Phase.P17: CheckNoDockStillRams();      _phase = Phase.P18; break;
            case Phase.P18: CheckClientPilotDockGate();  _phase = Phase.P19; break;
            case Phase.P19: CheckPeerLeftDockAbort();    _phase = Phase.P20; break;
            case Phase.P20: CheckTeleportButtonServerOnly(); _phase = Phase.P21; break;
            case Phase.P21: CheckServerTeleportThenDock();   _phase = Phase.P22; break;
            case Phase.P22: CheckDockDebugInfo();           _phase = Phase.Done; break;

            case Phase.Done: Finish(); break;
        }
    }
}
