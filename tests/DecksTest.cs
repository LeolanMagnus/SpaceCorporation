using Godot;

namespace SpaceCorporation.Tests;

/// <summary>Стенд docs/habitable-deck-phase2-plan.md, F2.3 (жилая палуба
/// типов каталога, построенная кодом). Кейсы п.7.4–7.5 плана:
///  • у Hauler/Freighter генератор строит каюту по HabitableVolume: пол/
///    потолок/стены на слое Deck, в габарите корпуса;
///  • сцена-палуба курьера на время чужой каюты вырезана из дерева — её
///    плиты не должны встречаться лучам внутри Hauler;
///  • шлюз перенесён в кормовую стенку каюты: игрок с код-палубы попадает
///    в шлюз и проходит цикл наружу (как HabDeckTest Clip6, на код-палубе);
///  • обратное переключение на курьера восстанавливает сцена-палубу и
///    сцена-позицию шлюза.
/// Мир — живой Ship из сцены (заморожен, как в HabDeckTest), смена типа —
/// шов ShipTestSetType (только оффлайн). Драйвер — как HabDeckTest:
/// фазы в _PhysicsProcess, физика мира паузой, EVA шагами EvaTestStep.</summary>
public partial class DecksTest : Node
{
    private enum Phase
    {
        Intro,
        D1Catalog, D2Build, D3Hatch, D4DeckStand, D5Cycle, D6Revert,
        Outro,
    }

    private Phase _phase;
    private Node3D _world = null!;
    private Player _player = null!;
    private Ship _ship = null!;
    private readonly System.Collections.Generic.Dictionary<string, Vector3>
        _sceneDeckPos = new();

    private int _checks, _failures;

    public override void _Ready()
    {
        GD.Print("[DECKS] start");
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
            case Phase.Intro: BuildWorld(); Next(Phase.D1Catalog); break;
            case Phase.D1Catalog: D1_CatalogVolume(); Next(Phase.D2Build); break;
            case Phase.D2Build: D2_BuildHauler(); Next(Phase.D3Hatch); break;
            case Phase.D3Hatch: D3_HatchInAftWall(); Next(Phase.D4DeckStand); break;
            case Phase.D4DeckStand: D4_PlayerOnGeneratedDeck(); Next(Phase.D5Cycle); break;
            case Phase.D5Cycle: if (D5_HatchCycle()) Next(Phase.D6Revert); break;
            case Phase.D6Revert: D6_RevertToCourier(); Next(Phase.Outro); break;
        }
    }

    private void BuildWorld()
    {
        _ship = ((PackedScene)GD.Load("res://scenes/Ship.tscn")).Instantiate<Ship>();
        _ship.Name = "TestShip";
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _world.AddChild(_ship);
        // Сцена-трансформы палубы ДО любых перестроек — эталон для п.7.5в.
        foreach (Node c in _ship.GetNode<Node3D>("Decks").GetChildren())
            if (c is Node3D n3d && !c.HasMeta("GeneratedDeck"))
                _sceneDeckPos[n3d.Name] = n3d.Position;

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

    private void EvaSteps(int n)
    {
        for (int i = 0; i < n; i++)
            _player.EvaTestStep();
    }

    private Vector3 BodyPos => _player.GlobalPosition;

    // ─────────────────────────────────────────────────────────────────────
    // D1 (п.7.4): HabitableVolume в каталоге: Hauler/Freighter — есть,
    // курьер — нет (его каюта живёт сценой); каюта вписана в корпус.
    // ─────────────────────────────────────────────────────────────────────
    private void D1_CatalogVolume()
    {
        var hauler = ShipStatCatalog.Find(ShipType.Hauler);
        var freighter = ShipStatCatalog.Find(ShipType.ContainerFreighter);
        var courier = ShipStatCatalog.Find(ShipType.Courier);
        Check("п.7.4: у Hauler задан HabitableVolume, 1 палуба",
              hauler.HabitableVolume != Vector3.Zero && hauler.DecksCount == 1);
        Check("п.7.4: у Freighter задан HabitableVolume",
              freighter.HabitableVolume != Vector3.Zero);
        Check("п.7.4: у курьера каюту задаёт сцена, не каталог",
              courier.HabitableVolume == Vector3.Zero);
        Check("п.7.4: каюта Hauler вписана в габарит корпуса",
              hauler.HabitableVolume.X <= hauler.HullSize.X
              && hauler.HabitableVolume.Y <= hauler.HullSize.Y);
    }

    // ─────────────────────────────────────────────────────────────────────
    // D2 (п.7.4/п.7.5a): переключение на Hauler — генератор построил каюту,
    // сцена-палуба курьера вырезана, шлюз переехал в кормовую стенку.
    // ─────────────────────────────────────────────────────────────────────
    private void D2_BuildHauler()
    {
        _ship.ShipTestSetType(ShipType.Hauler);
        var spec = ShipStatCatalog.Find(ShipType.Hauler);

        var floor = _ship.GetNodeOrNull<StaticBody3D>("Decks/GenCabinFloor");
        var ceiling = _ship.GetNodeOrNull<StaticBody3D>("Decks/GenCabinCeiling");
        var walls = _ship.GetNodeOrNull<Node3D>("Decks/GenCabinWalls");
        Check("п.7.4: Hauler: код построил пол каюты", floor != null);
        Check("п.7.4: Hauler: код построил потолок каюты", ceiling != null);
        Check("п.7.4: Hauler: код построил стены каюты (4+ куска)",
              walls != null && walls.GetChildCount() >= 4);
        Check("п.7.4: пол каюты на слое Deck",
              floor != null && (floor.CollisionLayer & CollisionLayers.Deck) != 0);

        // Каюта в габарите корпуса (центр корпуса = центр Ship, hull y ±hy/2).
        if (floor != null)
        {
            var shape = floor.GetNode<CollisionShape3D>("CollisionShape3D");
            var box = (BoxShape3D)shape.Shape;
            float floorTopLocalY = floor.Position.Y + box.Size.Y * 0.5f;
            Check("п.7.4: верх пола каюты выше днища корпуса и под потолком",
                  floorTopLocalY > -spec.HullSize.Y * 0.5f
                  && floorTopLocalY + spec.HabitableVolume.Y < spec.HullSize.Y * 0.5f + 0.01f);
            Check("п.7.4: пол каюты по ширине = HabitableVolume.X",
                  Mathf.Abs(box.Size.X - spec.HabitableVolume.X) < 0.11f);
        }

        // Сцена-палуба курьера вырезана из дерева: ни путей, ни детей в Decks.
        Check("п.7.5: Hauler: сцена-палуба курьера вырезана (DeckHull)",
              _ship.GetNodeOrNull<Node3D>("Decks/DeckHull") == null);
        Check("п.7.5: Hauler: вырезаны стены каюты курьера",
              _ship.GetNodeOrNull<Node3D>("Decks/CabinWalls") == null);

        // Шлюз переехал: его центр в кормовой стенке каюты (local z ≈ hz−0.15+0.55).
        var hatch = _ship.GetNodeOrNull<Airlock>("Decks/Airlock");
        float cabinAft = spec.HullSize.Z * 0.5f - 0.15f;
        Check("п.7.5: шлюз перенесён в кормовую стенку каюты",
              hatch != null
              && Mathf.Abs(hatch.Position.Z - (cabinAft + 0.55f)) < 0.01f);
    }

    // ─────────────────────────────────────────────────────────────────────
    // D3 (п.7.5b): игрок из кабины попадает на шлюз и липнет к нему
    // (ботинками к стене — тот же путь, что HabDeckTest п.4).
    // ─────────────────────────────────────────────────────────────────────
    private void D3_HatchInAftWall()
    {
        var spec = ShipStatCatalog.Find(ShipType.Hauler);
        var hatch = _ship.GetNodeOrNull<Airlock>("Decks/Airlock");
        var wall = _ship.GetNodeOrNull<StaticBody3D>("Decks/GenCabinWalls/WallAftStarboard");
        if (hatch == null || wall == null)
        {
            Check("п.7.5b: кормовая стенка/шлюз найдены", false);
            return;
        }

        // Посадка на стену каюты изнутри (шов стенда, как Clip3 в HabDeckTest:
        // опора — ботинки, `IsAttached` — только про кресло).
        _player.EvaTestSetMode(EvaMode.Jetpack);
        _player.EvaTestSetSuit(SuitState.Fresh());
        Vector3 inner = wall.ToGlobal(new Vector3(0f, 1.0f, -0.06f));
        _player.EvaTestStickTo(wall, inner, -Vector3.Forward);
        Check("п.7.5b: ботинки липнут к кормовой стенке каюты",
              _player.EvaTestBootsBody() == wall);
        _player.EvaTestSetBoots(false);

        // Игрок у кормовой стенки в радиусе шлюза: поиск по расстоянию
        // (NearestAirlock), при закрытых дверях подсказка — «наружную».
        _player.Detach();
        _player.GlobalPosition = hatch.ToGlobal(new Vector3(0f, 1.2f, -1.1f));
        _player.Velocity = Vector3.Zero;
        string hint = _player.HatchHoverHint();
        Check("п.7.5b: шлюз виден из кабины (hover-подсказка)",
              hint.Contains("наружную"));
    }

    // ─────────────────────────────────────────────────────────────────────
    // D4 (п.7.5a): капсула садится ботинками на код-пол каюты Hauler и
    // ходит по нему: опора — GenDeckFloor, носитель — наш корабль,
    //clip-исключения палубы выставлены (аналог Clip3 HabDeckTest, но на
    // сгенерированной палубе).
    // ─────────────────────────────────────────────────────────────────────
    private void D4_PlayerOnGeneratedDeck()
    {
        var floor = _ship.GetNodeOrNull<StaticBody3D>("Decks/GenCabinFloor");
        if (floor == null)
        {
            Check("п.7.5a: код-пол найден", false);
            return;
        }
        _player.EvaTestSetMode(EvaMode.Jetpack);
        _player.EvaTestSetSuit(SuitState.Fresh());
        _player.EvaTestStickTo(floor,
            floor.ToGlobal(new Vector3(0f, 0f, 0.5f)), Vector3.Up);
        Node3D? body = _player.EvaTestBootsBody();
        GD.Print($"[PROBE d4] body={body?.GetName()} pos={_player.GlobalPosition}");
        Check("п.7.5a: ботинки липнут к код-полу каюты", body == floor);
        Check("п.7.5a: носитель-корабль найден, clip-исключения выставлены",
              _player.ClipTestCount() > 0);
        // Плита курьера вырезана: луч вниз от игрока встречает код-пол, а не
        // DeckHull курьера (тот в DeckStorage вне PhysWorld).
        _player.EvaTestSetBoots(false);
        _player.EvaTestSetMode(EvaMode.Jetpack);
    }

    // ─────────────────────────────────────────────────────────────────────
    // D5 (п.7.5b): полный цикл шлюзования с код-палубы Hauler: наружу и
    // обратно. Как Clip6 в HabDeckTest, но стартовая точка — каюта,
    // построенная кодом.
    // ─────────────────────────────────────────────────────────────────────
    private int _c5Step = -1, _c5Frames;

    private bool D5_HatchCycle()
    {
        var hatch = _ship.GetNodeOrNull<Airlock>("Decks/Airlock");
        if (hatch == null)
        {
            Check("п.7.5b: шлюз есть", false);
            return true;
        }
        if (++_c5Frames > 1200)
        {
            Check($"п.7.5b: цикл таймаут на шаге {_c5Step} "
                  + $"(ext={hatch.ExternalOpen} int={hatch.InternalOpen} "
                  + $"mode={_player.Mode} pos={_player.GlobalPosition})", false);
            _c5Step = 5;
        }

        if (_c5Step < 0)
        {
            _c5Step = 0;
            // Чистый старт: двери закрыты (D4 мог задеть дверь через DetachOrSeat).
            hatch.SetExternalOpen(false);
            hatch.SetInternalOpen(false);
            // Старт внутри каюты у кормовой стенки (над код-полом).
            _player.EvaTestSetBoots(false);
            _player.EvaTestSetMode(EvaMode.Jetpack);
            _player.GlobalPosition = hatch.ToGlobal(new Vector3(0f, 1.2f, -1.4f));
            _player.Velocity = Vector3.Zero;
            _player.EvaTestSyncFlightFromBody();
            return false;
        }

        switch (_c5Step)
        {
            case 0: // из кабины подсказка — открыть наружную (двери закрыты)
                Check("п.7.5b: старт из кабины — двери закрыты",
                      !hatch.ExternalOpen && !hatch.InternalOpen
                      && !_player.HatchTestInside(hatch));
                Check("п.7.5b: у шлюза из кабины подсказка «наружную»",
                      _player.HatchHoverHint().Contains("наружную"));
                _c5Step++;
                return false;
            case 1: // F: наружная открыта; игрок всё ещё снаружи коробки
                _player.EvaTestDetachOrSeat();
                if (!hatch.ExternalOpen)
                    return false;
                _c5Step++;
                return false;
            case 2: // внутрь шлюза (телепорт — физический перелёт не предмет стенда)
                _player.EvaTestSetMode(EvaMode.Jetpack);
                _player.Velocity = Vector3.Zero;
                _player.Rotation = Vector3.Zero;
                _player.GlobalPosition = hatch.ToGlobal(new Vector3(0f, 1.4f, 0f));
                _player.EvaTestSyncFlightFromBody();
                _c5Step++;
                return false;
            case 3: // в шлюзе при открытой наружной — «шлюзование: встать на палубу»
                if (!_player.HatchTestInside(hatch))
                    return false;
                Check("п.7.5b: в шлюзе подсказка — шлюзование",
                      _player.HatchHoverHint().Contains("шлюзование"));
                _player.EvaTestDetachOrSeat(); // F: сесть на палубу шлюза
                _c5Step++;
                return false;
            case 4: // после посадки: шлюзование завершено, внутренняя открыта
                Check("п.7.5b: игрок в шлюзе в ботинках",
                      _player.HatchTestInside(hatch) && _player.Mode == EvaMode.Boots);
                Check("п.7.5b: наружная закрыта, внутренняя открыта",
                      !hatch.ExternalOpen && hatch.InternalOpen);
                Check("п.7.5b: подсказка — выход в космос (обратный цикл)",
                      _player.HatchHoverHint().Contains("космос"));
                _c5Step++;
                return false;
            case 5:
                _player.EvaTestSetInput(null);
                return true;
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────
    // D6 (п.7.5в): возврат на курьера — код-узлы удалены, сцена-палуба
    // вернулась на свои места со сцена-трансформами, шлюз — на сцена-позиции.
    // ─────────────────────────────────────────────────────────────────────
    private void D6_RevertToCourier()
    {
        _ship.ShipTestSetType(ShipType.Courier);

        int genLeft = 0;
        foreach (Node c in _ship.GetNode<Node3D>("Decks").GetChildren())
            if (c.HasMeta("GeneratedDeck"))
                genLeft++;
        Check("п.7.5в: код-узлы Hauler удалены при возврате", genLeft == 0);

        var deckHull = _ship.GetNodeOrNull<StaticBody3D>("Decks/DeckHull");
        var interior = _ship.GetNodeOrNull<StaticBody3D>("Decks/DeckInterior");
        var walls = _ship.GetNodeOrNull<Node3D>("Decks/CabinWalls");
        Check("п.7.5в: сцена-палуба курьера вернулась в Decks",
              deckHull != null && interior != null && walls != null);
        Check("п.7.5в: сцена-трансформы палубы не изменились",
              deckHull != null && interior != null && walls != null
              && _sceneDeckPos.TryGetValue("DeckHull", out var hp)
              && deckHull.Position == hp
              && _sceneDeckPos.TryGetValue("DeckInterior", out var ip)
              && interior.Position == ip);

        var hatch = _ship.GetNode<Airlock>("Decks/Airlock");
        Check("п.7.5в: шлюз вернулся на сцена-позицию (y=-0.4, z=2.15)",
              Mathf.Abs(hatch.Position.Y + 0.4f) < 0.01f
              && Mathf.Abs(hatch.Position.Z - 2.15f) < 0.01f);
    }
}
