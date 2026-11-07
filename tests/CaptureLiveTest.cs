using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Живой тест-сценарий frame-plan шаг 4: «малый внутри большого держит
/// дистанцию». Два корабля из каталога — Courier (1200 кг) в пузыре
/// ContainerFreighter (10000 кг, отношение 8.33 ≥ RatioMin=3) — летят рядом
/// с одинаковыми скоростями; правило захвата (TickCaptureRule, раз в
/// CheckEveryN шагов) обязано поймать малого, а интеграция «в кадре»
/// (AdvanceFlight: шаг идёт по v_rel = v − v_frame, позиция сносится членом
/// v_frame·dt) — держать дистанцию практически постоянной, когда большой
/// тормозит задней тягой, а малый НЕ имеет тяги вовсе.
/// Без захвата дистанция за 2 с выросла бы на ≈ 32 м (большой гаснет на
/// 16 м/с², малый летит по инерции).
///
/// Ось сцены: ориентация identity ⇒ «вперёд» корпуса = мировая −Z
/// (конвенция FlightModel), поэтому общий вектор скорости пары — (0,0,−100),
/// а торможение большого — AxisB=1 (мировая +Z).
///
/// Физика — детерминированная: TestSetInputForTest(..., manualSteps: true)
/// гасит `_IntegrateForces`, шаг ведёт `TestStepForTest` (тот же AdvanceFlight
/// + TickCaptureRule, §3.8). Старт сервера — как Stage5Test: авторитет есть,
/// реестр Ships заполнен спавнером/SpawnShipFromScene.
/// Запуск: ./run-tests.sh res://tests/CaptureLiveTest.tscn
/// </summary>
public partial class CaptureLiveTest : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    private Ship? _small;
    private Ship? _big;

    private enum Phase
    {
        WaitSpawn, StartServer, Setup, Attach, Settle,
        ThrustBig, HoldDistance, ReleaseCheck,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

    // Радиусы пузыря из каталога (HullSize.Length()/2 + CaptureTuning.Margin):
    // Courier (2,1,4):  √21/2 ≈ 2.29 + 25 ≈ 27.3
    // Freighter (5,3.5,14): √233.25/2 ≈ 7.64 + 25 ≈ 32.6
    private const float StartDist = 30f;   // внутри пузыря большого, вне пузыря малого
    private const float SharedSpeed = 100f;
    private const float VRelTol = 1f;      // держим |v_rel| < 1 м/с
    private const float DistTol = 2f;      // |Δd| за 2 с торможения — не больше 2 м

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
        NetworkManager.TestExtraPeers.Add(2);
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
                else if (_frame > 120)
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
                    Check(nm.IsAuthoritySide, "после StartServer авторитет включён");
                }
                _phase = Phase.Setup;
                break;
            }

            case Phase.Setup:
                SetupPair();
                _phase = _small != null && _big != null ? Phase.Attach : Phase.Done;
                break;

            case Phase.Attach:
            {
                // Тики кораблей к этому моменту не кратны CheckEveryN по-разному,
                // но 16 ручных шагов гарантируют ≥ 4 проверки правила на каждом.
                for (int i = 0; i < 16; i++)
                {
                    _small!.TestStepForTest();
                    _big!.TestStepForTest();
                }
                Check(_small.CaptureParentNetId == _big.Net!.ShipNetId,
                      $"малый захвачен большим (родитель=#{_small.CaptureParentNetId}, " +
                      $"большой=#{_big.Net!.ShipNetId})");
                // Шаг 6 (сетевой контракт): переход ОПУБЛИКОВАН в NetState —
                // именно эти поля шлёт OnChange-синхронизатор (SceneReplication
                // Config properties/13-14); клиент-пилот по CaptureParentNetId
                // гейтит предсказание (§6.2), пир-наблюдатель — нет.
                Check(_small.Net!.CaptureParentNetId == _big.Net!.ShipNetId,
                      $"NetState: реплика родителя у малого = #{_big.Net!.ShipNetId} " +
                      $"(получено #{_small.Net!.CaptureParentNetId})");
                Check(_small.Net!.CaptureTick >= 0 && _small.Net!.CaptureTick <= _small.Net!.SimTick,
                      $"NetState: CaptureTick перехода = сим-тик авторитета на переходе " +
                      $"(0 <= {_small.Net!.CaptureTick} <= SimTick={_small.Net!.SimTick})");
                Check(_big.Net!.CaptureParentNetId == 0,
                      "NetState: у большого реплика родителя = 0 (не захвачен)");
                _phase = Phase.Settle;
                break;
            }

            case Phase.Settle:
                // Вложенность наоборот: большой НЕ захвачен малым (ни по массе,
                // ни «вторым проходом» правила).
                Check(_big!.CaptureParentNetId == 0,
                      $"большой не захвачен малым (получено #{_big.CaptureParentNetId})");
                _phase = Phase.ThrustBig;
                break;

            case Phase.ThrustBig:
            {
                float distBefore = _small!.GlobalPosition.DistanceTo(_big!.GlobalPosition);
                Check(distBefore < _big.CaptureRadius,
                      $"дистанция {distBefore:F1} внутри пузыря большого {(_big.CaptureRadius):F1}");
                // Большой даёт заднюю тягу (тормоз вдоль −Z), малый — пустой кадр.
                // ОБЯЗАТЕЛЕН бит ThrustB в Mask: FlightModel гейтит PilotAssist по
                // (Mask & LinearBits)==0 — без бита ассист гасил бы тягу каждый шаг.
                _big.TestSetInputForTest(new InputFrame { Mask = InputMask.ThrustB, AxisB = 1f }, manualSteps: true);
                _phase = Phase.HoldDistance;
                break;
            }

            case Phase.HoldDistance:
            {
                float d0 = _small!.GlobalPosition.DistanceTo(_big!.GlobalPosition);
                const int steps = 120; // 2 секунды сим-времени (шаг 1/60)
                for (int i = 0; i < steps; i++)
                {
                    // ПРИЧИНЫЙ порядок: сначала родитель, потом пассажир.
                    // Перенос снимается с предыдущей пробы и внедряется текущей:
                    // если малый шагает до большого, его v_rel отстаёт ровно на
                    // шаг (|v_rel| = a_родителя·dt = 75/60 = 1,25 м/с при тяге
                    // модулей) — при causal-порядке пассажир получает Δv родителя
                    // В ТОМ ЖЕ КАДРЕ: |v_rel| = 0 ровно, дистанция не ползёт.
                    // В бою порядок задаёт дерево сцены; лаг в один шаг лежит
                    // много ниже порогов выхода (VReleaseMax) — это норма.
                    _big.TestStepForTest();
                    _small.TestStepForTest();
                }
                float d1 = _small.GlobalPosition.DistanceTo(_big.GlobalPosition);
                Vector3 vRel = _small.LinearVelocity - _big.LinearVelocity;
                float vBig = _big.LinearVelocity.Length();
                Check(_small.CaptureParentNetId == _big.Net!.ShipNetId,
                      "захват удержан после 2 с торможения большого");
                Check(vBig < SharedSpeed - 25f && vBig > 1f,
                      $"большой реально тормозит задней тягой: |v| = {vBig:F1} м/с " +
                      $"(было {SharedSpeed})");
                Check(vRel.Length() < VRelTol,
                      $"|v_rel| = {vRel.Length():F2} < {VRelTol} — малый летит в кадре большого");
                Check(Mathf.Abs(d1 - d0) < DistTol,
                      $"малый держит дистанцию: |Δd| = {Mathf.Abs(d1 - d0):F2} м за 2 с " +
                      $"(было {d0:F1}, стало {d1:F1}; без захвата выросло бы на ~32 м)");
                _phase = Phase.ReleaseCheck;
                break;
            }

            case Phase.ReleaseCheck:
            {
                // Выталкивание за гистерезис (Radius × Hysteresis) — правило на
                // ближайшей проверке снимает захват (LeftBubble).
                Vector3 dir = _small!.GlobalPosition - _big!.GlobalPosition;
                dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector3.Right;
                _small.GlobalPosition = _big.GlobalPosition
                                        + dir * (_big.CaptureRadius * CaptureTuning.Hysteresis + 2f);
                for (int i = 0; i < 8; i++)
                {
                    _small.TestStepForTest();
                    _big.TestStepForTest();
                }
                Check(_small.CaptureParentNetId == 0,
                      $"выход: малый за гистерезисом — захват снят (получено #{_small.CaptureParentNetId})");
                Check(_small.Net!.CaptureParentNetId == 0,
                      "NetState: реплика родителя сброшена при выходе (0)");
                _phase = Phase.Done;
                break;
            }

            case Phase.Done:
                Finish();
                break;
        }
    }

    private void SetupPair()
    {
        // Свободные корабли без пилота: TestSetInputForTest даёт кадр ввода
        // напрямую, авторитет — у сервера (Dedicated в headless, как Stage5Test).
        _big = _spawner!.SpawnShipFromScene(Vector3.Zero, "CapBig", ShipType.ContainerFreighter);
        _small = _spawner.SpawnShipFromScene(new Vector3(0f, 0f, -StartDist), "CapSmall",
                                             ShipType.Courier);
        Check(_big != null && _small != null, "пара кораблей заспавнена");
        if (_big == null || _small == null) return;

        // Общая скорость пары: −Z («вперёд» при identity-ориентации), |v_rel|=0.
        _big.LinearVelocity = new Vector3(0f, 0f, -SharedSpeed);
        _small.LinearVelocity = new Vector3(0f, 0f, -SharedSpeed);

        // Пилот-ассист («жидкий космос», Этап 6 Р1) гасит скорость постоянным
        // ускорением, ОДИНАКОВЫМ ПО ВЕЛИЧИНЕ ТЯГЕ данного корпуса — у Courier и
        // ContainerFreighter оно РАЗНОЕ (разные тяга/масса). Живой прогон
        // показал: на пустом вводе ассист тормозит пару с разной выдержкой и
        // сам раскачивает |v_rel| до ~1.2 м/с ещё до всякого захвата. Сценарий
        // проверяет ПЕРЕНОС кадра, а не ассист — ассист гасим у обоих.
        _big.PilotAssist = false;
        _small.PilotAssist = false;
        // Сеттер PilotAssist трогает только Loadout; Flight пересобирается в
        // RebuildFlight (приватный, зовётся при экипировке модулей). Для теста
        // правим итоговые параметры напрямую — тот же результат, что Build:
        // PilotAssist=false => AssistDragAccel=0 (ShipLoadout.cs:192).
        _big.Flight.PilotAssist = false;
        _big.Flight.AssistDragAccel = 0f;
        _small.Flight.PilotAssist = false;
        _small.Flight.AssistDragAccel = 0f;
        // Ручной шаг с этого момента: реальные физкадры пропускаются.
        _big.TestSetInputForTest(default, manualSteps: true);
        _small.TestSetInputForTest(default, manualSteps: true);

        Check(StartDist < _big.CaptureRadius,
              $"стартовая дистанция {StartDist} внутри пузыря большого ({(_big.CaptureRadius):F1})");
        Check(StartDist > _small.CaptureRadius,
              $"стартовая дистанция {StartDist} вне пузыря малого ({(_small.CaptureRadius):F1}) " +
              "— пузырь малого не накрывает большого");
        Check(_small.CaptureParentNetId == 0, "до шагов: малый не захвачен");
        Check(_big.Flight.Mass / _small.Flight.Mass >= CaptureTuning.RatioMin,
              $"масса большого/малого = {_big.Flight.Mass / _small.Flight.Mass:F2} ≥ RatioMin={CaptureTuning.RatioMin}");
    }

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"  PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"  FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"=== CaptureLiveTest: {_checks - _failures}/{_checks} проверок, " +
                 $"{_failures} провалов ===");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
