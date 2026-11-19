using Godot;

namespace SpaceCorporation.Tests;

/// <summary>Стенд docs/habitable-deck-plan.md, Шаг 0 (маска коллизий игрока +
/// контекстные исключения). Шаги стенда — те же швы, что Stage7Test:
/// `EvaTestSetPhysicsPaused(true)` + явные `EvaTestStep()`, чтобы физика мира
/// не разгоняла тело между фазами. Все кейсы оффлайновые (self-host).</summary>
public partial class HabDeckTest : Node
{
    private enum Phase
    {
        Intro,
        Clip1, Clip2, Clip3, Clip4, Clip5,
        Clip6Enter, Clip6Cycle, Clip6Exit,
        Outro,
    }

    private Phase _phase;
    private Node3D _world = null!;
    private Player _player = null!;
    private Ship _ship = null!;
    private StaticBody3D _wall = null!; // стена на слое Ship (образец обшивки)
    private Vector3 _p0;

    public override void _Ready()
    {
        GD.Print("[HABDECK] start");
        _world = new Node3D { Name = "World" };
        AddChild(_world);
        Next(Phase.Intro);
    }

    private void Next(Phase p)
    {
        _phase = p;
        if (p == Phase.Outro)
        {
            GD.Print($"RESULT checks={_checks} failures={_failures}");
            GetTree().Quit(_failures == 0 ? 0 : 1);
        }
    }

    private int _checks, _failures;

    private void Check(string what, bool ok)
    {
        _checks++;
        if (!ok) _failures++;
        GD.Print($"{(ok ? "PASS" : "FAIL")} {_phase}: {what}");
    }

    public override void _PhysicsProcess(double delta)
    {
        switch (_phase)
        {
            case Phase.Intro: BuildWorld(); Next(Phase.Clip1); break;
            case Phase.Clip1: Clip1_MaskAndFree(); Next(Phase.Clip2); break;
            case Phase.Clip2: Clip2_SeatInsideHull(); Next(Phase.Clip3); break;
            case Phase.Clip3: Clip3_BootsOnShipDeck(); Next(Phase.Clip4); break;
            case Phase.Clip4: Clip4_JetpackBlockedByHull(); Next(Phase.Clip5); break;
            case Phase.Clip5: Clip5_WallBlocksJetpack(); Next(Phase.Clip6Enter); break;
            case Phase.Clip6Enter: if (Clip6_HatchCycle()) Next(Phase.Outro); break;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Мир стенда: живой Ship из сцены (заморожен, как донор Stage7) +
    // отдельная «стена обшивки» на слое Ship (образец корпуса вне корабля).
    // ─────────────────────────────────────────────────────────────────────
    private void BuildWorld()
    {
        _ship = ((PackedScene)GD.Load("res://scenes/Ship.tscn")).Instantiate<Ship>();
        _ship.Name = "TestShip";
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _world.AddChild(_ship);

        _wall = new StaticBody3D
        {
            Name = "HullWall",
            CollisionLayer = CollisionLayers.Ship, // образец обшивки
            CollisionMask = 0u,
            Transform = new Transform3D(Basis.Identity, new Vector3(14f, 0f, 0f)),
        };
        _wall.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1f, 4f, 8f) },
        });
        _world.AddChild(_wall);

        _player = ((PackedScene)GD.Load("res://scenes/Player.tscn"))
            .Instantiate<Player>();
        _player.Name = "TestPlayer";
        _player.AutoAttachOnSpawn = false;
        _world.AddChild(_player);
        _player.AddToGroup("player");
        _player.GlobalPosition = new Vector3(0f, 3f, 0f);
        _player.Velocity = Vector3.Zero;
        _player.EvaTestSetPhysicsPaused(true);
    }

    /// <summary>N шагов EVA по 1/60 с (тот же шов, что Stage7Test).</summary>
    private void EvaSteps(int n)
    {
        for (int i = 0; i < n; i++)
            _player.EvaTestStep();
    }

    private Vector3 BodyPos => _player.GlobalPosition;

    // ─────────────────────────────────────────────────────────────────────
    // Clip1: свободный режим — маска включает Ship и Deck, исключений нет.
    // ─────────────────────────────────────────────────────────────────────
    private void Clip1_MaskAndFree()
    {
        uint mask = _player.ClipTestMask();
        Check("п.0.1: маска капсулы включает Ship",
              (mask & CollisionLayers.Ship) != 0);
        Check("п.0.1: база-маска = Station|Target|Ship",
              _player.ClipTestBaseMask()
              == (CollisionLayers.Station | CollisionLayers.Target | CollisionLayers.Ship));
        Check("п.0.1: в свободном режиме исключений нет",
              _player.ClipTestCount() == 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Clip2: посадка в кресло (Reparent под hull) — капсула не выталкивается
    // корпусом: исключения на всё дерево корабля.
    // ─────────────────────────────────────────────────────────────────────
    private void Clip2_SeatInsideHull()
    {
        var seat = _ship.GetNodeOrNull<AttachPoint>("ShipHull/AttachPoints/Seat");
        if (seat == null)
        {
            Check("п.0.2: кресло найдено", false);
            return;
        }
        bool sat = _player.EvaTestSitAt(seat);
        EvaSteps(2);
        Check("п.0.2: посадка в кресле удалась (режим Seat)",
              sat && _player.IsAttached);
        Check("п.0.2: в кресле исключения на корабль выставлены",
              _player.ClipTestCount() > 0);
        // Капсула внутри hull (y 0.9-1.4 при потолке корпуса 0.5 без исключений
        // её бы выбросило): поза осталась штатной (PlayerOffset y=0.9 + кресло y0.5).
        Vector3 want = seat.ToGlobal(Vector3.Zero) + new Vector3(0f, 0.9f, 0f);
        Check("п.0.2: корпус не вытолкнул капсулу из кресла",
              BodyPos.DistanceTo(want) < 0.3f);
        _player.Detach();
        EvaSteps(1);
        Check("п.0.2: после Detach исключения сняты",
              _player.ClipTestCount() == 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Clip3: Boots на палубе СВОЕГО корабля — палуба/внутренняя обшивка
    // исключены (липкость не проваливает капсулу в пол при шаге), носитель
    // найден по предкам опоры.
    // ─────────────────────────────────────────────────────────────────────
    private void Clip3_BootsOnShipDeck()
    {
        // Как Boot0 в Stage7Test: шов `EvaTestStickTo` (луч в headless недоступен
        // напрямую с курсором) сажаем на реальную палубу корабля — DeckHull
        // (верх y = ship.y + 0.5; у Ship.tscn корабль стоит y=-0.251).
        _player.EvaTestSetMode(EvaMode.Jetpack);
        var deckHull = _ship.GetNode<StaticBody3D>("Decks/DeckHull");
        _player.EvaTestSetSuit(SuitState.Fresh());
        _player.EvaTestStickTo(deckHull, deckHull.GlobalPosition + Vector3.Up, Vector3.Up);
        Node3D? body = _player.EvaTestBootsBody();
        GD.Print($"[PROBE clip3] body={body?.Name} pos={BodyPos}");
        Check("п.0.3: ботинки липнут к палубе своего корабля",
              body == deckHull);
        Check("п.0.3: носитель-корабль найден, исключения выставлены",
              _player.ClipTestCount() > 0);
        Vector3 pStick = BodyPos;
        EvaSteps(3);
        Check("п.0.3: липкость держит капсулу над палубой (не провалилась)",
              _player.EvaTestGapForTest() <= SuitModel.BootsStickOffset + 0.05f
              && BodyPos.DistanceTo(pStick) < 0.2f);
        _player.EvaTestSetBoots(false);
        EvaSteps(1);
        Check("п.0.3: снятие ботинок — исключения сняты",
              _player.ClipTestCount() == 0);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Clip4: джетпак толкает игрока в борт корабля — капсула упирается в
    // обшивку (MoveAndSlide не пропускает сквозь hull Ship.tscn).
    // ─────────────────────────────────────────────────────────────────────
    private void Clip4_JetpackBlockedByHull()
    {
        // Игрок снаружи слева от корабля на уровне палубы; толкаем +X сквозь
        // левую грань hull (x=-1). Без слоя Ship в маске он бы пролетел насквозь.
        _player.GlobalPosition = new Vector3(-3.5f, 0.55f, 0f);
        // Тяга джетпа — по локальному «вперёд» (−Z). Разворот yaw −90°
        // ставит −Z вдоль +X: игрок реально жалит «в борт».
        _player.GlobalTransform = new Transform3D(
            new Basis(Vector3.Up, -Mathf.Pi / 2f), _player.GlobalPosition);
        _player.EvaTestSyncFlightFromBody();
        for (int i = 0; i < 90; i++)
        {
            // Кормим кадром ПЕРЕД каждым шагом (EvaTestSetInput сгорает за шаг).
            _player.EvaTestSetInput(InputFrame.FromMask((uint)i, InputMask.ThrustF));
            _player.EvaTestStep(1f / 60f);
        }
        _player.EvaTestSetInput(null);
        // Левая грань hull x=-1, радиус капсулы 0.3 ⇒ упор ~-1.3.
        Check("п.0.4: обшивка корабля остановила капсулу (упор ~-1.3)",
              BodyPos.X > -1.6f && BodyPos.X < -1.0f && IsFiniteVec(BodyPos));
        GD.Print($"[PROBE clip4] pos={BodyPos} vel={_player.Velocity}"
                 + $" clip={_player.ClipTestCount()}");
        _player.EvaTestSetInput(null);
        _player.Velocity = Vector3.Zero;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Clip5: та же проверка на «стене-образце» слоя Ship вне корабля —
    // игрок останавливается перед стеной (x=14±0.5, капсура r=0.3).
    // ─────────────────────────────────────────────────────────────────────
    private void Clip5_WallBlocksJetpack()
    {
        _player.GlobalPosition = new Vector3(9f, 0.55f, 0f);
        _player.GlobalTransform = new Transform3D(
            new Basis(Vector3.Up, -Mathf.Pi / 2f), _player.GlobalPosition);
        _player.Velocity = new Vector3(6f, 0f, 0f);
        _player.EvaTestSyncFlightFromBody();
        _p0 = BodyPos;
        for (int i = 0; i < 240; i++)
        {
            _player.EvaTestSetInput(InputFrame.FromMask((uint)i, InputMask.ThrustF));
            _player.EvaTestStep(1f / 60f);
            if (i % 30 == 29)
                GD.Print($"[PROBE clip5 s{i + 1}] x={BodyPos.X} vel={_player.Velocity}");
        }
        _player.EvaTestSetInput(null);
        // Грань стены x = 14 - 0.5 = 13.5; радиус капсулы 0.3 ⇒ упор ~13.2.
        Check("п.0.4: стена слоя Ship остановила капсулу (упор ~13.2)",
              BodyPos.X > 12.9f && BodyPos.X < 13.4f && IsFiniteVec(BodyPos));
        GD.Print($"[PROBE clip5] p0={_p0} pos={BodyPos} vel={_player.Velocity}");
    }

    private static bool IsFiniteVec(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // ─────────────────────────────────────────────────────────────────────
    // Clip6: полный цикл шлюзования на живом курьере (Шаг 4 плана).
    // Снаружи у кормы → F (открыть наружную) → влететь в шлюз → F (закрыть
    // наружную, ботинки, открыть внутреннюю) → игрок на палубе в ботинках →
    // F (обратный цикл: закрыть внутреннюю, снять ботинки, открыть наружную).
    // Машина пофазная: `_clip6Step` идёт вперёд только когда условие шага
    // выполнено; иначе — кадр ожидания (физика сама доведёт капсулу).
    // ─────────────────────────────────────────────────────────────────────
    private int _c6Step = -1;

    private bool Clip6_HatchCycle()
    {
        var hatch = _ship.GetNodeOrNull<Airlock>("Decks/Airlock");
        if (hatch == null)
        {
            Check("п.4.0: в Ship.tscn есть инстанс Airlock", false);
            return true;
        }
        if (_c6Step < 0)
        {
            _c6Step = 0;
            // Наружная дверь шлюза: world z = ship.z + 2.15 + 0.6 = ship.z + 2.75;
            // старт снаружи за габаритом (|z_local| > 0.6) в радиусе действия F.
            _player.GlobalPosition = _ship.GlobalPosition + new Vector3(0f, 1.0f, 4.0f);
            _player.Velocity = Vector3.Zero;
            _player.EvaTestSyncFlightFromBody();
            _player.EvaTestSetMode(EvaMode.Jetpack);
            Sync();
            return false;
        }
        Sync(); // гоним физику (шаг 0, physics paused — мир и так стоит)

        switch (_c6Step)
        {
            case 0: // старт: снаружи, шлюз герметичен
                Check("п.4.1: старт — снаружи, двери закрыты",
                      !hatch.ExternalOpen && !hatch.InternalOpen
                      && !_player.HatchTestInside(hatch));
                Check("п.4.1a: hover-подсказка у наружной двери",
                      _player.HatchHoverHint().Contains("наружную"));
                _c6Step++;
                return false;
            case 1: // F → наружная дверь открыта
                _player.EvaTestDetachOrSeat();
                if (!hatch.ExternalOpen)
                    return false; // не открылась (не должно) — ждём, таймаут ниже
                Check("п.4.2: F снаружи открыла наружную дверь", hatch.ExternalOpen);
                _c6Step++;
                return false;
            case 2: // внутрь: телепорт в бокс (дверь открыта; physics-перелёт — не предмет клипа)
                if (_c6Frames < 10)
                {
                    _player.EvaTestSetBoots(false);
                    _player.EvaTestSetMode(EvaMode.Jetpack);
                    _player.Velocity = Vector3.Zero;
                    _player.Rotation = Vector3.Zero;
                    _player.GlobalPosition = _ship.ToGlobal(new Vector3(0f, 1.4f, 2.15f));
                    _c6Frames++;
                    return false;
                }
                if (_player.HatchTestInside(hatch))
                {
                    _player.EvaTestSyncFlightFromBody();
                    _c6Step++;
                }
                return false;
            case 3: // F → цикл шлюзования (одно нажатие, иначе следующий шаг
                   // увидит уже открытую наружную и запустит обратный цикл)
                Check("п.4.3a: hover-подсказка внутри шлюза на джетпаке",
                      _player.HatchHoverHint().Contains("шлюзование"));
                _player.EvaTestDetachOrSeat();
                Sync();
                Check("п.4.3: цикл: наружная закрыта, внутренняя открыта",
                      !hatch.ExternalOpen && hatch.InternalOpen);
                var deck = _ship.GetNodeOrNull<StaticBody3D>("Decks/Airlock/Floor");
                var hullDeck = _ship.GetNodeOrNull<StaticBody3D>("Decks/DeckHull");
                var deckInt = _ship.GetNodeOrNull<StaticBody3D>("Decks/DeckInterior");
                // Пол шлюза в один уровень с палубой (верх DeckInterior y=-0.40
                // local = верх пола шлюза): луч от ног встречает любую плиту
                // слоя палубы — Floor, DeckInterior или DeckHull. Любая значит
                // «ногами к палубе», нормаль +Y проверяется дальше.
                Check("п.4.4: цикл: игрок в ботинках, сцеплен с полом шлюза",
                      _player.Mode == EvaMode.Boots
                      && (_player.EvaTestBootsBody() == deck
                          || _player.EvaTestBootsBody() == hullDeck
                          || _player.EvaTestBootsBody() == deckInt));
                GD.Print($"[PROBE habdeck4.4] body={_player.EvaTestBootsBody()?.GetName()} mode={_player.Mode}");
                // Нормаль пола указывает от поверхности (как в Stage7: floorN=(0,1,0)).
                Check("п.4.5: цикл: пол шлюза дал нормаль +Y (ноги к палубе)",
                      _player.EvaTestFloorNormal() == Vector3.Up);
                _c6Step++;
                return false;
            case 5: // F → обратный цикл наружу: внутренняя закрыта, джетпак, наружная открыта
                Check("п.4.6a: hover-подсказка внутри в ботинках",
                      _player.HatchHoverHint().Contains("космос"));
                _player.EvaTestDetachOrSeat();
                Sync();
                Check("п.4.6: обратный цикл: внутренняя закрыта, наружная открыта",
                      hatch.InternalOpen == false && hatch.ExternalOpen);
                Check("п.4.7: обратный цикл: игрок снова на джетпаке",
                      _player.Mode == EvaMode.Jetpack);
                _c6Step++;
                return false;
            case 6: // выбраться наружу — не обязательно для проверки; финал
                _player.EvaTestSetInput(null);
                return true;
        }
        return true;
    }

    /// <summary>Таймаут Clip6: ~1200 кадров физики на весь цикл.</summary>
    private int _c6Frames;
    private void Sync()
    {
        if (++_c6Frames > 1200)
        {
            Check($"п.4: Clip6 таймаут на шаге {_c6Step} (hatch ext={_ship.GetNodeOrNull<Airlock>("Decks/Airlock")?.ExternalOpen} int={_ship.GetNodeOrNull<Airlock>("Decks/Airlock")?.InternalOpen} mode={_player.Mode} pos={BodyPos})", false);
            _c6Step = 6;
        }
    }
}
