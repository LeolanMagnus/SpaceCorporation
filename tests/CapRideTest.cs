using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Стенд docs/habitable-deck-plan.md, Шаг 7 (тесты захвата Cap1–Cap3).
/// Живой, как CaptureLiveTest: Main+спавнер, авторитет — сервер, корабли
/// идут ручным шагом `TestStepForTest` (детерминизм), EVA-игрок шагает
/// реальной физикой (паузу НЕ ставим — Cap3 требует живого переноса по
///_delta опоры_).
///
///   Cap1_PlayerNeverCaptured: EVA-игрок (Jetpack, |v|≈0) рядом с крупным
///     кораблём, тот разгоняется вперёд — игрок НЕ захватывается: пузырь
///     захвата смотрит только в реестр `nm.Ships`, CharacterBody3D там не
///     живёт. Инвариант: не пристёгнут, режим не сменился, позиция не
///     унеслась родителем (осталась на месте).
///   Cap2_PilotRidesCapturedShip: курьер в пузыре крупного (масса ≥
///     RatioMin) → захвачен; игрок садится в кресло курьера (шов
///     `EvaTestSitAt`, реперент под AttachPoint) → пара едет под
///     торможением большого, позиция игрока следует за курьером шаг в
///     шаг, выбросов нет (шаг 0 маска Ship + капсула-кресло не конфликтуют).
///   Cap3_BootsRideCapturedShip: игрок отстёгивается и прилипает ботинками
///     к палубе курьера (`EvaTestStickTo` на DeckHull) под захватом —
///     carry-цепочка (delta-перенос опоры) везёт игрока вместе с курьером
///     и большим: offset от палубы не растёт, тело не выбивает.
///
/// Запуск: ./run-tests.sh res://tests/CapRideTest.tscn
/// </summary>
public partial class CapRideTest : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    private Player? _player;
    private Ship? _big;    // ContainerFreighter
    private Ship? _small;  // Courier

    private enum Phase
    {
        WaitSpawn, StartServer, FindPlayer,
        Cap1Setup, Cap1Thrust, Cap1Check,
        Cap2Setup, Cap2Attach, Cap2Sit, Cap2Ride, Cap2Check,
        Cap3Setup, Cap3Ride, Cap3Check,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

    private const float SharedSpeed = 100f;
    private const float StartDist = 30f; // внутри пузыря большого, вне пузыря малого
    private const float FarPolygonZ = 200f; // полигон Cap1 вдали от стартового флота

    // Cap1: игрок стоит ЗА кормой большого (ось +Z): полуLENGTH корпуса
    // фрейтера 7 м + капсула ~0.4 м — берём 12 м, чтобы старт был чист
    // (физика не выталкивала бы пересечение). Большой уходит в −Z.
    private const float Cap1BackOffset = 12f;

    private Vector3 _cap1PlayerPos;
    private Vector3 _cap2PlayerPos;
    private Vector3 _cap3Rel;
    private int _cap3Frames;
    private int _findPlayerFrames;
    private AttachPoint _seat = null!;
    private StaticBody3D _deck = null!;

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
                    // Host-роль: сервер + локальный игрок (dedicated игрока НЕ
                    // спавнит — offline-игрока снимает, а нового не даёт).
                    var err = nm.StartServer(withLocalPlayer: true);
                    Check(err == Error.Ok, $"StartServer() в headless -> Ok (получено {err})");
                    Check(nm.IsAuthoritySide, "после StartServer авторитет включён");
                }
                // Живой игрок спавнера (offline, peer 1) — наш EVA-подопытный.
                // Старт сервера пересобирает мир в dedicated оффрейм: прежнего
                // offline-игрока спавнер освобождает, нового заспавнит следом —
                // ссылку берём в фазе FindPlayer по факту валидности.
                _phase = Phase.FindPlayer;
                break;
            }

            case Phase.FindPlayer:
            {
                // Dedicated-пересборка мира асинхронна: old offline-игрок ещё
                // может висеть в дереве «предсвободённым» — ждём несколько
                // кадров, прежде чем искать валидную ссылку.
                if (_findPlayerFrames++ < 10) break;
                _player = FindPlayer();
                if (_player != null && GodotObject.IsInstanceValid(_player))
                {
                    Check(true, "живой игрок найден в группе player");
                    _phase = Phase.Cap1Setup;
                }
                else if (++_findPlayerFrames > 120)
                {
                    Check(false, "живой игрок так и не появился после StartServer");
                    _phase = Phase.Done;
                }
                break;
            }

            // ── Cap1: EVA-игрок и крупный корабль ─────────────────────────
            case Phase.Cap1Setup:
            {
                // Полигон Cap1 — вдали от стартового флота спавнера: тот болтается
                // у старта в пузыре большого и захваченный («ship#1 ← #4») таранил
                // бы подопытного. 200 м по −Z, уход тоже в −Z (от флота).
                _big = _spawner!.SpawnShipFromScene(
                    new Vector3(0f, 0f, -FarPolygonZ), "CapBig", ShipType.ContainerFreighter);
                Check(_big != null, "CapBig (ContainerFreighter) заспавнен");
                if (_big == null) { _phase = Phase.Done; break; }
                _big.PilotAssist = false;
                _big.Flight.PilotAssist = false;
                _big.Flight.AssistDragAccel = 0f;
                _big.TestSetInputForTest(default, manualSteps: true);

                // Живой игрок спавнера автокрепится к дефолтному креслу стартового
                // «Ship» — сажать/лепить придётся после отстёжки.
                if (_player!.IsAttached)
                    _player.EvaTestDetachOrSeat();
                Check(!_player.IsAttached, "Cap1: живой игрок отстёгнут от стартового кресла");
                // Игрок — Jetpack, скафандр свежий, стоит в покое за кормой.
                _player.EvaTestSetMode(EvaMode.Jetpack);
                _player.EvaTestSetSuit(SuitState.Fresh());
                _player.GlobalPosition = _big.GlobalPosition + new Vector3(0f, 0f, Cap1BackOffset);
                _player.Velocity = Vector3.Zero;
                _player.EvaTestSyncFlightFromBody();
                _cap1PlayerPos = _player.GlobalPosition;
                Check(_player.GlobalPosition.DistanceTo(_big.GlobalPosition) < _big.CaptureRadius,
                      $"игрок внутри пузыря большого ({_big.CaptureRadius:F1} м)");
                _phase = Phase.Cap1Thrust;
                break;
            }

            case Phase.Cap1Thrust:
            {
                // «Вперёд» в контенте — локальная −Z (FlightModel), у большого
                // нос смотрит −Z, игрок за кормой (+Z): уход от подопытного.
                _big!.TestSetInputForTest(
                    new InputFrame { Mask = InputMask.ThrustF | InputMask.Boost, AxisF = 1f },
                    manualSteps: true);
                for (int i = 0; i < 60; i++)
                    _big.TestStepForTest();
                // Игрок шагает ЖИВОЙ физикой Main (его _PhysicsProcess крутится
                // сам) — вручную его не дублируем.
                _big.TestSetInputForTest(default, manualSteps: true);
                _phase = Phase.Cap1Check;
                break;
            }

            case Phase.Cap1Check:
            {
                float bigTraveled = _big!.GlobalPosition.DistanceTo(_cap1PlayerPos);
                Check(bigTraveled > 10f,
                      $"большой реально ушёл вперёд: {bigTraveled:F1} м от точки игрока");
                Check(!_player!.IsAttached, "Cap1: игрок НЕ пристёгнут к захвату корабля");
                Check(_player.Mode == EvaMode.Jetpack,
                      $"Cap1: режим не сменился (получено {_player.Mode})");
                float drift = _player.GlobalPosition.DistanceTo(_cap1PlayerPos);
                Check(drift < 2f,
                      $"Cap1: поза игрока НЕ унеслась родителем (снос {drift:F2} м < 2 м)");
                Check(_player.GlobalPosition.DistanceTo(_big.GlobalPosition) > _big.CaptureRadius,
                      "Cap1: большой в итоге за пузырём от игрока (свободный дрейф)");
                _phase = Phase.Cap2Setup;
                break;
            }

            // ── Cap2: пилот в кресле малого под захватом ──────────────────
            case Phase.Cap2Setup:
            {
                // Курьер спавнится относительно текущего места большого.
                _small = _spawner!.SpawnShipFromScene(
                    _big!.GlobalPosition + new Vector3(0f, 0f, -StartDist),
                    "CapSmall", ShipType.Courier);
                Check(_small != null, "CapSmall (Courier) заспавнен в пузыре");
                if (_small == null) { _phase = Phase.Done; break; }

                _small.PilotAssist = false;
                _small.Flight.PilotAssist = false;
                _small.Flight.AssistDragAccel = 0f;
                _big!.LinearVelocity = new Vector3(0f, 0f, -SharedSpeed);
                _small.LinearVelocity = new Vector3(0f, 0f, -SharedSpeed);
                _small.TestSetInputForTest(default, manualSteps: true);
                Check(_big.Flight.Mass / _small.Flight.Mass >= CaptureTuning.RatioMin,
                      $"масса большого/малого = {_big.Flight.Mass / _small.Flight.Mass:F2} ≥ {CaptureTuning.RatioMin}");
                _phase = Phase.Cap2Attach;
                break;
            }

            case Phase.Cap2Attach:
            {
                for (int i = 0; i < 16; i++)
                {
                    _big!.TestStepForTest();
                    _small!.TestStepForTest();
                }
                Check(_small!.CaptureParentNetId == _big!.Net!.ShipNetId,
                      $"Cap2: малый захвачен большим (родитель=#{_small.CaptureParentNetId}, " +
                      $"большой=#{_big.Net!.ShipNetId})");
                _phase = Phase.Cap2Sit;
                break;
            }

            case Phase.Cap2Sit:
            {
                _seat = _small!.GetNode<AttachPoint>("ShipHull/AttachPoints/Seat");
                bool sat = _player!.EvaTestSitAt(_seat);
                Check(sat && _player.IsAttached, "Cap2: посадка в кресло курьера удалась");
                _cap2PlayerPos = _player.GlobalPosition;
                _phase = Phase.Cap2Ride;
                break;
            }

            case Phase.Cap2Ride:
            {
                float dist0 = _small!.GlobalPosition.DistanceTo(_big!.GlobalPosition);
                // Задняя тяга большого, малый — пустой кадр (как CaptureLiveTest).
                _big!.TestSetInputForTest(new InputFrame { Mask = InputMask.ThrustB, AxisB = 1f }, manualSteps: true);
                for (int i = 0; i < 120; i++)
                {
                    _big.TestStepForTest();
                    _small.TestStepForTest();
                }
                _big.TestSetInputForTest(default, manualSteps: true);
                float dist1 = _small.GlobalPosition.DistanceTo(_big.GlobalPosition);
                Check(_small.CaptureParentNetId == _big.Net!.ShipNetId,
                      "Cap2: захват удержан после 2 с торможения большого");
                Check(Mathf.Abs(dist1 - dist0) < 2f,
                      $"Cap2: малый держит дистанцию (|Δd| = {Mathf.Abs(dist1 - dist0):F2} м)");
                Check(_small.GlobalPosition.DistanceTo(_cap2PlayerPos) > 20f,
                      $"Cap2: пара реально проехала с игроком " +
                      $"({_small.GlobalPosition.DistanceTo(_cap2PlayerPos):F1} м)");
                _phase = Phase.Cap2Check;
                break;
            }

            case Phase.Cap2Check:
            {
                float off = _player!.GlobalPosition.DistanceTo(_small!.GlobalPosition);
                Check(_player.IsAttached && _player.AttachedTo == _seat,
                      "Cap2: игрок всё ещё в кресле курьера");
                Check(off < 2f,
                      $"Cap2: позиция игрока следует за курьером (offset {off:F2} м < 2 м)");
                _phase = Phase.Cap3Setup;
                break;
            }

            // ── Cap3: boots-игрок на палубе малого под захватом ───────────
            case Phase.Cap3Setup:
            {
                _player!.EvaTestDetachOrSeat(); // отстёжка из кресла
                Check(!_player.IsAttached, $"Cap3: отстёжка из кресла (IsAttached={_player.IsAttached})");
                _player.EvaTestSetMode(EvaMode.Jetpack);

                _deck = _small!.GetNode<StaticBody3D>("Decks/DeckHull");
                Vector3 deckWorld = _deck.ToGlobal(Vector3.Zero);
                // Капсула над центром палубы: точка контакта — верх палубы.
                _player.GlobalPosition = deckWorld + Vector3.Up * 1.2f;
                _player.Velocity = _small.LinearVelocity;
                _player.EvaTestSyncFlightFromBody();
                _player.EvaTestStickTo(_deck, deckWorld, Vector3.Up);
                Check(_player.EvaTestBootsBody() == _deck, "Cap3: ботинки прилипли к палубе курьера");
                _player.Velocity = _small.LinearVelocity;
                _phase = Phase.Cap3Ride;
                break;
            }

            case Phase.Cap3Ride:
            {
                // Снимаем привязку: игрок относительно палубы до переноса.
                _cap3Rel = _player!.GlobalPosition - _deck!.ToGlobal(Vector3.Zero);
                // Перенос ботинок считается в ШАГЕ ИГРОКА (дека → delta-поза),
                // поэтому шаги кораблей разложены по физкадрам: между пачками
                // игрок успевает отшагать и перенестись следом за палубой.
                for (int i = 0; i < 10; i++)
                {
                    _big!.TestStepForTest();
                    _small!.TestStepForTest();
                }
                _cap3Frames++;
                if (_cap3Frames >= 12) // 12 × 10 шагов = 2 с живого полёта
                    _phase = Phase.Cap3Check;
                break;
            }

            case Phase.Cap3Check:
            {
                Vector3 deckWorld = _deck!.ToGlobal(Vector3.Zero);
                Vector3 rel = _player!.GlobalPosition - deckWorld;
                Check(_player.EvaTestBootsBody() == _deck,
                      "Cap3: контакт ботинок с палубой удержан (не выбило)");
                float relDrift = rel.DistanceTo(_cap3Rel);
                Check(relDrift < 1f,
                      $"Cap3: игрок едет на палубе следом за курьером " +
                      $"(снос в кадре палубы {relDrift:F2} м < 1 м)");
                Check(_small!.CaptureParentNetId == _big!.Net!.ShipNetId,
                      "Cap3: захват носителя всё ещё активен");
                // Скорость в лоб не сверяем: корабли шагают пачками по 10 шагов
                // на физкадр, и переносная скорость ботинок (carry/dt) масштабируется
                // так же — осмысленный критерий «едет следом» уже доказан сносом
                // в кадре палубы выше (0 м при ~2 с полёта на ~100 м/с).
                _phase = Phase.Done;
                break;
            }

            case Phase.Done:
                Finish();
                break;
        }
    }

    private Player? FindPlayer()
    {
        foreach (var n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p))
                return p;
        return null;
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
        GD.Print($"CapRideTest: checks={_checks} failures={_failures} " +
                 $"{(_failures == 0 ? "ALL OK" : "FAILED")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
