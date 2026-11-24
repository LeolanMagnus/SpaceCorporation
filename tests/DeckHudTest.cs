using Godot;

namespace SpaceCorporation.Tests;

/// <summary>Стенд docs/habitable-deck-phase2-plan.md, F2.4 (HUD-контекст
/// местоположения). Образец п.11/п.32 (DebugInfoBus-тест): проверяется
/// строка Player.DeckContextText() (ключ шины «DeckCtx»). Кейсы:
///  • курьер, ботинки на палубе внутри кабины (в т.ч. у шлюза) ⇒ CABIN;
///  • игрок на внешней корме ⇒ HULL;
///  • курьер в пузыре захвата (шов SetCaptureForTest) ⇒ CABIN + «captured»;
///  • код-каюта Hauler (F2.3, meta GenCabinCenter/Half): в каюте ⇒ CABIN
///    (без meta-габарита внешняя точка давала бы CABIN по корпусу);
///  • станция (Station.tscn HabDeck, слой Station) ⇒ STATION;
///  • источник «DeckCtx» зарегистрирован в шине (образец п.32).</summary>
public partial class DeckHudTest : Node
{
    private enum Phase
    {
        Intro, H1CourierCabin, H2Hull, H3Captured, H4Generated,
        H5Station, H6Bus, Outro,
    }

    private Phase _phase;
    private Node3D _world = null!;
    private Player _player = null!;
    private Ship _ship = null!;

    private int _checks, _failures;

    public override void _Ready()
    {
        GD.Print("[DECKHUD] start");
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
            case Phase.Intro: BuildWorld(); Next(Phase.H1CourierCabin); break;
            case Phase.H1CourierCabin: H1(); Next(Phase.H2Hull); break;
            case Phase.H2Hull: H2(); Next(Phase.H3Captured); break;
            case Phase.H3Captured: H3(); Next(Phase.H4Generated); break;
            case Phase.H4Generated: H4(); Next(Phase.H5Station); break;
            case Phase.H5Station: H5(); Next(Phase.H6Bus); break;
            case Phase.H6Bus: H6(); Next(Phase.Outro); break;
        }
    }

    private void BuildWorld()
    {
        _ship = ((PackedScene)GD.Load("res://scenes/Ship.tscn")).Instantiate<Ship>();
        _ship.Name = "TestShip";
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _world.AddChild(_ship);
        _ship.ShipTestSetType(ShipType.Courier);

        _player = ((PackedScene)GD.Load("res://scenes/Player.tscn")).Instantiate<Player>();
        _player.Name = "TestPlayer";
        _world.AddChild(_player);
    }

    /// <summary>Посадить игрока ботинками на конкретное тело палубы в мировой
    /// точке (шов EvaTestStickTo — как HabDeckTest/DecksTest).</summary>
    private void StickOn(StaticBody3D body, Vector3 worldTop)
    {
        _player.GlobalPosition = worldTop + Vector3.Up * 0.6f;
        _player.EvaTestSetBoots(true);
        _player.EvaTestStickTo(body, worldTop, Vector3.Up);
    }

    private string Ctx() => _player.DeckContextText();

    private void H1()
    {
        var deck = _ship.GetNode<StaticBody3D>("Decks/DeckHull");
        // Кабина курьера: палуба-плита 2×1×4 (верх локально y=+0.5), стены
        // кабины до y=1.7. Точка посадки = низ капсулы (центр на +0.9 выше).
        StickOn(deck, deck.ToGlobal(new Vector3(0f, 0.5f, 0f)));
        string s = Ctx();
        GD.Print($"    [DeckCtx] {s} body={_player.EvaTestBootsBody()?.GetName()}" +
                 $" lp={_ship.ToLocal(_player.GlobalPosition)}");
        Check("H1: ботинки на палубе внутри кабины ⇒ loc=CABIN",
              s.Contains("loc=CABIN"));
        Check("H1: без захвата — без пометки captured", !s.Contains("captured"));

        // У шлюза (z за кормовым срезом корпуса) — тоже CABIN: шлюз часть кабины.
        _player.GlobalPosition = _ship.ToGlobal(new Vector3(0f, -0.1f, 2.3f));
        s = Ctx();
        Check("H1: точка у шлюза (за кормовым срезом) ⇒ CABIN",
              s.Contains("loc=CABIN"));
    }

    private void H2()
    {
        // Ноги на ВНЕШНИМ днище (точка входа под корпусом): центр капсулы
        // (~+0.95 над точкой входа) остаётся ниже габарита CABIN (мин. y=-0.5)
        // ⇒ HULL. Телепорт здесь запрещён — рвёт приварку ботинка (стендовый
        // шов не переносит привязку), поэтому сажаем заново.
        // Габарит палубы (объединённый AABB палубных тел): y до 1.7 — верх
        // переборок. Сажаем ботинки на верх переборки WallFore (слой Deck):
        // центр капсулы (+0.95) оказывается над габаритом ⇒ HULL.
        var deck = _ship.GetNode<StaticBody3D>("Decks/CabinWalls/WallFore");
        StickOn(deck, _ship.ToGlobal(new Vector3(0f, 1.75f, -1.55f)));
        string s = Ctx();
        GD.Print($"    [DeckCtx] {s} body={_player.EvaTestBootsBody()?.GetName()}" +
                 $" lp={_ship.ToLocal(_player.GlobalPosition)}");
        Check("H2: ботинки на переборке выше габарита палубы ⇒ loc=HULL",
              s.Contains("loc=HULL"));
    }

    private void H3()
    {
        // Захват: курьер в пузыре «родителя» (фиктивный id — публичное поле
        // Net, тест только читает пометку). Возврат: id 0.
        var deck = _ship.GetNode<StaticBody3D>("Decks/DeckHull");
        StickOn(deck, deck.ToGlobal(new Vector3(0f, -0.5f, 0f)));
        _ship.Net!.CaptureParentNetId = 999;
        string s = Ctx();
        GD.Print($"    [DeckCtx] {s} body={_player.EvaTestBootsBody()?.GetName()}" +
                 $" lp={_ship.ToLocal(_player.GlobalPosition)}");
        Check("H3: каюта захваченного корабля ⇒ CABIN + captured",
              s.Contains("loc=CABIN") && s.Contains("captured"));
        _ship.Net.CaptureParentNetId = 0;
        Check("H3: снятие захвата ⇒ CABIN без captured",
              Ctx().Contains("loc=CABIN") && !Ctx().Contains("captured"));
    }

    private void H4()
    {
        _ship.ShipTestSetType(ShipType.Hauler);
        var deck = _ship.GetNode<StaticBody3D>("Decks/GenCabinFloor");
        var shape = (CollisionShape3D)deck.GetChild(0);
        Vector3 top = deck.ToGlobal(new Vector3(0f,
            ((BoxShape3D)shape.Shape).Size.Y * 0.5f, 0f));
        StickOn(deck, top);
        string s = Ctx();
        GD.Print($"    [DeckCtx] {s}");
        Check("H4: код-каюта Hauler внутри meta-габарита ⇒ CABIN",
              s.Contains("loc=CABIN"));

        // Снаружи каюты (в носовом отсеке корпуса, где габарита каюты нет) —
        // HULL: без meta габарита F2.3 эта точка проваливалась бы в CABIN по
        // габариту корпуса.
        _player.GlobalPosition = _ship.ToGlobal(new Vector3(0f, -0.2f, -6.2f));
        s = Ctx();
        Check("H4: вне meta-габарита каюты (корпус Hauler) ⇒ HULL",
              s.Contains("loc=HULL"));
        _ship.ShipTestSetType(ShipType.Courier);
    }

    private void H5()
    {
        var station = ((PackedScene)GD.Load("res://scenes/Station.tscn")).Instantiate<Node3D>();
        station.Name = "TestStation";
        station.Position = new Vector3(0f, 40f, 0f);
        _world.AddChild(station);
        var hab = station.GetNode<StaticBody3D>("HabDeck");
        var shape = (CollisionShape3D)hab.GetChild(0);
        float topLocal = 0.1f + ((BoxShape3D)shape.Shape).Size.Y * 0.5f; // DeckShape y=0.1
        Vector3 top = hab.ToGlobal(new Vector3(0f, topLocal, 0f));
        StickOn(hab, top);
        string s = Ctx();
        GD.Print($"    [DeckCtx] {s}");
        Check("H5: палуба станции (слой Station) ⇒ loc=STATION",
              s.Contains("loc=STATION"));
        station.QueueFree();
    }

    private void H6()
    {
        Check("H6: в шине есть источник «DeckCtx» (ключ Deck)",
              DebugInfoBus.HasInfoSource(Player.DeckContextSourceKey));
        // В свободном EVA (не в ботинках) контекста нет.
        _player.EvaTestSetBoots(false);
        string s = Ctx();
        Check("H6: свободный EVA без опоры ⇒ loc=NONE", s.Contains("loc=NONE"));
    }
}
