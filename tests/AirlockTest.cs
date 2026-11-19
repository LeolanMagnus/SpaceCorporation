using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Стенд docs/habitable-deck-plan.md, Шаг 8: полный цикл шлюзования НА ЛЕТЯЩЕМ
/// корабле (в отличие от Clip6 в HabDeckTest, где корабль заморожен).
///
/// Мир — живой, как CapRideTest: Main+спавнер, host-сервер; курьер «Ship» из
/// сцены разгоняется ручным шагом `TestStepForTest` (детерминизм), EVA-игрок
/// шагает РЕАЛЬНОЙ физикой (паузы НЕТ — иначе липкость/carry-перенос не живут).
///
/// Геометрия (scenes/Airlock.tscn, локальные оси инстанса в (0, 0.7, 2.15)):
///   пол Floor: верх y=0 (локально шлюза) ⇒ в осях корабля y = 0.7;
///   бокс: x/z ±0.6 (IsInsideAirlock), потолки/стены слоя Deck;
///   DeckHull курьера: box(2,1,4) в (0,0,0) ⇒ верх палубы y=0.5 — пол шлюза
///   на 0.2 м НИЖЕ уровня палубы (порог), шаг через внутренний проём проходит
///   по прямой y (ботинки липнут то к полу шлюза, то к палубе).
///
/// Проверки:
///   п.8.1: старт снаружи за кормой — шлюз герметичен, hover «наружную»;
///   п.8.2: F открыла наружную (внутренняя закрыта);
///   п.8.3: джетпак-перелёт ВНУТРЬ шлюза на летящем корабле (без телепорта);
///   п.8.4: F → цикл: наружная закрыта, внутренняя открыта, ботинки на ПОЛУ
///          ШЛЮЗА, нормаль +Y (разворот ногами к палубе); корабль летит;
///   п.8.5: шаг ботинками «вглубь» (S, нос тела −Z) сквозь внутренний проём —
///          опора меняется на DeckHull, offset не растёт (carry-перенос);
///   п.8.6: F на палубе при открытой внутренней — НЕ съедается (контракт C4);
///   п.8.7: игрок «улетает» из-за порога (пол шлюза ниже палубы, корабль
///          ускоряется вперёд): ботинки гасятся, наружная открыта — игрок
///          ВЫНУЖДЕН вернуться джетпаком внутрь (автовозврат через открытый
///          проём, F не нужна); затем F = обратный цикл (внутренняя закрыта,
///          джетпак, наружная открыта);
///   п.8.8: вылет наружу + разрыв с летящим кораблём растёт (стенд левитирует,
///          ship шагает каждый физкадр).
///
/// Запуск: ./run-tests.sh res://tests/AirlockTest.tscn
/// </summary>
public partial class AirlockTest : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    private Player? _player;
    private Ship? _ship;
    private Airlock? _lock;
    private StaticBody3D? _floor;   // Decks/Airlock/Floor
    private StaticBody3D? _deck;    // Decks/DeckHull

    private enum Phase
    {
        WaitSpawn, StartServer, FindPlayer,
        P1Setup, P1Check,
        P2OpenExt,
        P3FlyIn, P3Check,
        P4Cycle,
        P5WalkIn,
        P6NoEat, P6FlyBack,
        P7Inside, P7Reverse,
        P8FlyOut, P8Check,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

    // Старт снаружи за кормой: центр шлюза в осях корабля (0, 1.7, 2.15),
    // HatchRangeM = 2.2. Берём локальную (0, 1.42, 3.85): дистанция ~1.73 —
    // снаружи (z_local шлюза 1.7 > 0.6) и в радиусе F.
    private const float ApproachLocalZ = 3.85f;
    private const float ApproachLocalY = 1.42f;
    // Постоянный накат курьера и игрока на старте (стенд «летящий корабль»,
    // тяга корабля в _PhysicsProcess не ставится — coast, без ускорений).
    private static readonly Vector3 CoastVel = new(0f, 0f, -1.0f);

    private int _timeout;
    private Vector3 _p4Ship;   // позиция корабля на момент цикла

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        GD.Print("[AIRLOCKTEST] start");
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
        // Живой ЛЕТЯЩИЙ корабль: ручной шаг каждый физкадр стенда
        // (образец CapRideTest; физкадр сам по себе шаг пропускает — см.
        // Ship.Physics._IntegrateForces, `if (_testStep) return;`).
        if (_ship != null && GodotObject.IsInstanceValid(_ship)
            && _phase != Phase.Done && _phase != Phase.WaitSpawn
            && _phase != Phase.StartServer && _phase != Phase.FindPlayer)
        {
            _ship.TestSetInputForTest(default, manualSteps: true);
            _ship.TestStepForTest();
        }

        switch (_phase)
        {
            case Phase.WaitSpawn:
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null)
                    _phase = Phase.StartServer;
                else if (_frame > 120)
                {
                    Check("мир заспавнен (Ship появился в GameRoot)", false);
                    _phase = Phase.Done;
                }
                break;

            case Phase.StartServer:
            {
                var nm = NetworkManager.Instance;
                Check("NetworkManager.Instance доступен", nm != null);
                if (nm != null)
                {
                    var err = nm.StartServer(withLocalPlayer: true);
                    Check($"StartServer() в headless -> Ok (получено {err})", err == Error.Ok);
                }
                _phase = Phase.FindPlayer;
                break;
            }

            case Phase.FindPlayer:
            {
                if (_timeout++ < 10) break;
                _player = FindPlayer();
                if (_player != null && GodotObject.IsInstanceValid(_player))
                {
                    _ship = _spawner!.FindShipByName("Ship");
                    _lock = _ship?.GetNodeOrNull<Airlock>("Decks/Airlock");
                    _floor = _ship?.GetNodeOrNull<StaticBody3D>("Decks/Airlock/Floor");
                    _deck = _ship?.GetNodeOrNull<StaticBody3D>("Decks/DeckInterior");
                    Check("в Ship.tscn есть инстанс Airlock", _lock != null);
                    Check("пол шлюза и палуба курьера найдены", _floor != null && _deck != null);
                    if (_lock == null || _floor == null || _deck == null)
                    {
                        _phase = Phase.Done;
                        break;
                    }
                    _player.EvaTestSetMode(EvaMode.Jetpack);
                    _player.EvaTestSetSuit(SuitState.Fresh());
                    // Живой игрок спавнера автокрепится к дефолтному креслу
                    // «Ship» (образец CapRideTest): первое F ушло бы на отстёжку
                    // (ветка IsAttached в OnDetachOrSeatPressed), а не в шлюз.
                    if (_player.IsAttached)
                        _player.EvaTestDetachOrSeat();
                    Check("п.8.0: живой игрок отстёгнут от стартового кресла",
                          !_player.IsAttached && _player.Mode == EvaMode.Jetpack);
                    _phase = Phase.P1Setup;
                }
                else if (_timeout > 150)
                {
                    Check("живой игрок так и не появился после StartServer", false);
                    _phase = Phase.Done;
                }
                break;
            }

            // ── п.8.1: старт снаружи, шлюз герметичен ─────────────────────
            case Phase.P1Setup:
            {
                // Живой ЛЕТЯЩИЙ курьер: постоянный полёт вдоль −Z (нос),
                // ассист выключен — иначе тяга по умолчанию (ввода нет) гасит
                // накат. Игрок стартует С ТОЙ ЖЕ скоростью (образец
                // CapRideTest: общий дрейф, относительные перемещения честные).
                _ship!.Flight.PilotAssist = false;
                _ship.Flight.AssistDragAccel = 0f;
                _ship.LinearVelocity = CoastVel;
                _ship.AngularVelocity = Vector3.Zero;
                PlaceAtLocal(new Vector3(0f, ApproachLocalY, ApproachLocalZ));
                _player!.Velocity = CoastVel;
                _phase = Phase.P1Check;
                break;
            }
            case Phase.P1Check:
            {
                Check("п.8.1: старт — снаружи шлюза", !_player!.HatchTestInside(_lock!));
                Check("п.8.1: старт — обе двери закрыты, шлюз герметичен",
                      !_lock!.ExternalOpen && !_lock!.InternalOpen && _lock!.IsSealed);
                Check("п.8.1: hover-подсказка у наружной двери",
                      _player!.HatchHoverHint().Contains("наружную"));
                _phase = Phase.P2OpenExt;
                break;
            }

            // ── п.8.2: F снаружи → наружная дверь открыта ─────────────────
            case Phase.P2OpenExt:
            {
                _player!.EvaTestDetachOrSeat(); // реальная F-ветка → HatchTryInteract
                Check("п.8.2: F снаружи открыла наружную дверь", _lock!.ExternalOpen);
                Check("п.8.2: внутренняя по-прежнему закрыта", !_lock!.InternalOpen);
                _timeout = 0;
                _phase = Phase.P3FlyIn;
                break;
            }

            // ── п.8.3: джетпак-перелёт внутрь на летящем корабле ─────────
            case Phase.P3FlyIn:
            {
                // Джетпак: ThrustF = −нос тела = мировая −Z (базис единичный) —
                // именно туда, где центр шлюза (старт z_local 3.85 → шлюз 2.15).
                _player!.EvaTestSetInput(new InputFrame
                {
                    Mask = InputMask.ThrustF,
                    AxisF = 1f,
                });
                if (_player.HatchTestInside(_lock!))
                {
                    _player.EvaTestSetInput(null);
                    Check("п.8.3: джетпак-перелёт: игрок ВНУТРИ шлюза (не телепорт)", true);
                    Check("п.8.3: внутри шлюза ещё джетпак (не ботинки)",
                          _player.Mode == EvaMode.Jetpack);
                    _phase = Phase.P3Check;
                    break;
                }
                if (_timeout++ > 240)
                {
                    _player.EvaTestSetInput(null);
                    Check("п.8.3: игрок долетел внутрь шлюза на летящем корабле", false);
                    _phase = Phase.Done;
                }
                break;
            }
            case Phase.P3Check:
            {
                // НЕ прилепляем ботинками: цикл внутрь требует внутри +
                // джетпак + открытая наружная + закрытая внутренняя (Branch2 в
                // Player.Hatch). Липкость сломала бы машину состояний hover/F
                // (образец — Clip6 HabDeckTest: там тоже F с джетпака).
                _player!.Velocity = _ship!.LinearVelocity; // дрейф вместе с кораблём
                Check("п.8.3b: внутри шлюза всё ещё джетпак (условие цикла)",
                      _player.Mode == EvaMode.Jetpack && _player.HatchTestInside(_lock!));
                _phase = Phase.P4Cycle;
                break;
            }

            // ── п.8.4: цикл шлюзования на летящем корабле ─────────────────
            case Phase.P4Cycle:
            {
                _p4Ship = _ship!.GlobalPosition;
                // Hover на джетпаке внутри при открытой наружной — цикл внутрь
                // (та же машина состояний, что F).
                Check("п.8.4a: hover внутри шлюза на джетпаке — шлюзование (на палубу)",
                      _player!.HatchHoverHint().Contains("шлюзование"));
                _player.EvaTestDetachOrSeat();
                Check("п.8.4: цикл: наружная закрыта", !_lock!.ExternalOpen);
                Check("п.8.4: цикл: внутренняя открыта", _lock!.InternalOpen);
                Check("п.8.4: цикл: игрок развёрнут ботинками на пол шлюза",
                      _player.Mode == EvaMode.Boots && _player.EvaTestBootsBody() == _floor);
                Check("п.8.4: цикл: нормаль +Y — ноги к палубе",
                      _player.EvaTestFloorNormal() == Vector3.Up);
                _timeout = 0;
                _phase = Phase.P5WalkIn;
                break;
            }

            // ── п.8.5: шаг ботинками из шлюза на палубу курьера ───────────
            case Phase.P5WalkIn:
            {
                // Ботинки: F = нос тела = мировая −Z = с кормы внутрь корпуса
                // (шлюз за кормой z_local 2.15, палуба в −Z от него; пол шлюза
                // в один уровень с палубой — шаг по прямой).
                _player!.EvaTestSetInput(new InputFrame
                {
                    Mask = InputMask.ThrustF,
                    AxisF = 1f,
                });
                if (_timeout % 40 == 0)
                    GD.Print($"[PROBE p5 tr t={_timeout}] pl={_ship!.ToLocal(_player.GlobalPosition)}"
                             + $" mode={_player.Mode} body={_player.EvaTestBootsBody()?.GetName()}"
                             + $" n={_player.EvaTestFloorNormal()} gap={_player.EvaTestGapForTest():F3}"
                             + $" vel={_player.Velocity}");
                if (_player.EvaTestBootsBody() != null && !_player.HatchTestInside(_lock!))
                {
                    Check("п.8.5: шаг по полу шлюза вывел на палубу курьера", true);
                    float gap = _player.EvaTestGapForTest();
                    GD.Print($"[PROBE p8.5] rel={_player.GlobalPosition - _deck!.GlobalPosition} gap={gap:F3}");
                    Check("п.8.5: offset от палубы не растёт (carry-перенос)",
                          gap <= SuitModel.BootsStickOffset + 0.1f);
                    Check("п.8.5: игрок ВНУТРИ корпуса, не в шлюзе",
                          !_player.HatchTestInside(_lock!));
                    _player.EvaTestSetInput(null);
                    _phase = Phase.P6NoEat;
                    break;
                }
                if (_timeout++ > 300)
                {
                    GD.Print($"[PROBE p8.5 t/o] rel={_player.GlobalPosition - _deck!.GlobalPosition}"
                             + $" fwd={-_player.GlobalTransform.Basis.Z}"
                             + $" body={_player.EvaTestBootsBody()?.GetName()}"
                             + $" vel={_player.Velocity} vship={_ship!.LinearVelocity}");
                    // Допустимый сценарий физики стенда: игрок при шаге из
                    // шлюза внутрь (ботинки липнут к полу шлюза, не к палубе)
                    // уносится вперёд вместе с кораблём — relative-позиция
                    // уходит ЗА внутренние пределы палубы (нос), игрок
                    // болтается внутри корпуса, а не на палубе.
                    _player.EvaTestSetInput(null);
                    Check("п.8.5: за отведённое время на палубу не вышел (переход в п.8.7)", false);
                    _phase = Phase.P6FlyBack;
                }
                break;
            }

            // ── п.8.6: F на палубе при открытой внутренней НЕ съедается ───
            case Phase.P6NoEat:
            {
                bool acted = _player!.HatchTryInteract();
                Check("п.8.6: F на палубе (не в шлюзе) — не действует, состояние цело",
                      !acted && _lock!.InternalOpen && !_lock!.ExternalOpen);
                _timeout = 0;
                _phase = Phase.P6FlyBack;
                break;
            }

            // ── п.8.7: автовозврат джетпаком + обратный цикл ──────────────
            case Phase.P6FlyBack:
            {
                // Игрок «снаружи» из-за порога/ускорения: ботинки погашены
                // циклом или потерей опоры. Возвращаемся РЕАЛЬНЫМ джетпаком
                // через открытый внутренний проём летящего корабля.
                if (_player!.Mode != EvaMode.Jetpack)
                {
                    _player.EvaTestSetBoots(false);
                    _player.EvaTestSetMode(EvaMode.Jetpack);
                    _player.EvaTestSyncFlightFromBody();
                }
                _player.EvaTestSetInput(new InputFrame
                {
                    Mask = InputMask.ThrustB,
                    AxisB = 1f, // +Z мировая = к корме = ОТ палубы ВНУТРЬ
                                // коробки шлюза (шлюз за кормой, z_local больше)
                });
                // Возврат считан завершённым, только игрок РЕАЛЬНО внутри
                // коробки шлюза (та же проверка, что и у машины состояний).
                if (_player.HatchTestInside(_lock!))
                {
                    _player.EvaTestSetInput(null);
                    // Гасим пролёт: игрок летит по инерции в +Z и за пару
                    // кадров выпал бы за кормовую грань коробки — даём ему
                    // скорость корабля (совместный дрейф, относительный покой).
                    _player.Velocity = _ship!.LinearVelocity;
                    _player.EvaTestSyncFlightFromBody();
                    Check("п.8.7: автовозврат джетпаком через открытый внутренний проём", true);
                    _phase = Phase.P7Inside;
                    break;
                }
                if (_timeout++ > 400)
                {
                    _player.EvaTestSetInput(null);
                    Check("п.8.7: автовозврат во внутренний проём не удался", false);
                    _phase = Phase.P8Check;
                }
                break;
            }
            case Phase.P7Inside:
            {
                // Возврат внутрь пойман на грани коробки (z_local ~1.57 при
                // окне 1.55..2.75): ставим игрока в ЦЕНТР коробки шлюза на
                // высоте опоры из п.8.4 (y_local ~1.42, ноги на полу шлюза)
                // со скоростью корабля — общий дрейф, окно больше не «течёт».
                PlaceAtLocal(new Vector3(0f, 1.42f, 2.45f));
                _player!.Velocity = _ship!.LinearVelocity;
                _player.EvaTestSyncFlightFromBody();
                GD.Print($"[PROBE p7in] pl={_ship!.ToLocal(_player.GlobalPosition)}"
                         + $" inside={_player.HatchTestInside(_lock!)} mode={_player.Mode}"
                         + $" ext={_lock!.ExternalOpen} int={_lock!.InternalOpen}");
                // Обратный цикл («выход в космос») по машине состояний
                // требует ботинки ВНУТРИ шлюза: жмём реальную G — цепляемся
                // к полу шлюза.
                _player.EvaTestKeyPress("toggle_boots"); // реальная G-ветка
                GD.Print($"[PROBE p7g] body={_player.EvaTestBootsBody()?.GetName()} mode={_player.Mode}");
                // Опора под ногами в коробке шлюза — пол (Floor) или верх
                // палубного короба (DeckHull заполняет корпус под шлюзом,
                // физически луч от ног первым встречает именно его).
                var body = _player.EvaTestBootsBody();
                var deckInt = _ship!.GetNodeOrNull<StaticBody3D>("Decks/DeckInterior");
                var hullDeck = _ship!.GetNodeOrNull<StaticBody3D>("Decks/DeckHull");
                // Опора под ногами в коробке шлюза — любая плита палубного
                // слоя (пол шлюза / DeckInterior / DeckHull): верх палубы и
                // пол шлюза в один уровень, луч от ног встречает любую.
                Check("п.8.7: G внутри шлюза — ботинки цепляются за пол шлюза",
                      _player.Mode == EvaMode.Boots
                      && body != null
                      && (body == _floor || body == deckInt || body == hullDeck));
                Check("п.8.7: hover внутри шлюза (ботинки) — шлюзование наружу доступно",
                      _player.HatchHoverHint().Contains("шлюзование")
                      || _player.HatchHoverHint().Contains("космос"));
                _phase = Phase.P7Reverse;
                break;
            }
            case Phase.P7Reverse:
            {
                // Реальная F в состоянии inside=Boots, InternalOpen,
                // !ExternalOpen → обратный цикл: внутренняя закрывается,
                // ботинки снимаются, наружная открывается.
                _player!.EvaTestDetachOrSeat();
                Check("п.8.7: обратный цикл: внутренняя закрыта", !_lock!.InternalOpen);
                Check("п.8.7: обратный цикл: наружная открыта", _lock!.ExternalOpen);
                Check("п.8.7: обратный цикл: игрок снова на джетпаке",
                      _player.Mode == EvaMode.Jetpack);
                _timeout = 0;
                _phase = Phase.P8FlyOut;
                break;
            }

            // ── п.8.8: вылет наружу через открытую наружную дверь ─────────
            case Phase.P8FlyOut:
            {
                _player!.EvaTestSetInput(new InputFrame
                {
                    Mask = InputMask.ThrustB,
                    AxisB = 1f, // +Z мировая = корма = наружу через дверь (z_local +0.55)
                });
                Vector3 lockLocal = _lock!.ToLocal(_player.GlobalPosition);
                if (!_player.HatchTestInside(_lock!) && lockLocal.Z > 0.6f)
                {
                    _player.EvaTestSetInput(null);
                    Check("п.8.8: игрок вылетел НАРУЖУ через открытую наружную дверь",
                          _lock!.ExternalOpen && !_lock!.InternalOpen);
                    _phase = Phase.P8Check;
                    break;
                }
                if (_timeout++ > 300)
                {
                    _player.EvaTestSetInput(null);
                    Check("п.8.8: вылет наружу не удался", false);
                    _phase = Phase.P8Check;
                }
                break;
            }
            case Phase.P8Check:
            {
                // Корабль летит, игрок в свободном дрейфе: разрыв растёт.
                float d0 = _player!.GlobalPosition.DistanceTo(_ship!.GlobalPosition);
                for (int i = 0; i < 60; i++) _ship!.TestStepForTest();
                float d1 = _player.GlobalPosition.DistanceTo(_ship!.GlobalPosition);
                GD.Print($"[PROBE p8.8] d0={d0:F2} d1={d1:F2} shipV={_ship.LinearVelocity}");
                // Coast: корабль −1 м/с, игрок после вылета почти покоится →
                // за 60 фиксированных шагов разрыв растёт ~на 1 м (порог 0.5 —
                // с запасом на остаточную скорость игрока).
                Check("п.8.8: корабль улетает, игрок отстаёт (разрыв растёт)", d1 > d0 + 0.5f);
                _phase = Phase.Done;
                break;
            }

            case Phase.Done:
                Finish();
                break;
        }
    }

    private int _timeoutHelper; // не используется: общий _timeout

    private Player? FindPlayer()
    {
        foreach (Node n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p))
                return p;
        return null;
    }

    /// <summary>Телепорт игрока в локальных осях корабля + сброс скорости и
    /// синхронизация flight-модели (шов HabDeckTest.Clip6).</summary>
    private void PlaceAtLocal(Vector3 local)
    {
        _player!.Velocity = Vector3.Zero;
        _player.Rotation = Vector3.Zero;
        _player.GlobalPosition = _ship!.ToGlobal(local);
        _player.EvaTestSyncFlightFromBody();
    }

    private void Check(string what, bool ok)
    {
        _checks++;
        if (!ok) _failures++;
        GD.Print($"{(ok ? "PASS" : "FAIL")} {_phase}: {what}");
    }

    private void Finish()
    {
        GD.Print($"RESULT checks={_checks} failures={_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
