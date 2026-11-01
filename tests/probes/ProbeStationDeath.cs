using Godot;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П5 (stage4-plan §4.4, шаг 4.8): серверная смерть корабля при Hp<=0.
/// Как в ProbeStationRam: корабль шагает ЯВНО через TestStepForTest в реальном
/// Space3D (headless-интегратор тело не двигает), тараня реальную Station.
/// MaxHp ставим НИЗКИМ, а скорость ВЫСОКОЙ, чтобы одного попадания хватило на
/// Hp<=0 → ServerDieShip. Проверяем п.21: после Hp=0 у сервера узел помечен к
/// удалению (IsQueuedForDeletion) и PilotPeerId == 0. У сервера-авторитета
/// (offline-peer, Ship — авторитет) ServerDieShip() срабатывает в том же шаге.
/// Запуск: godot --headless --path . res://tests/probes/ProbeStationDeath.tscn
/// </summary>
public partial class ProbeStationDeath : Node3D
{
    private Ship _ship = null!;
    private Station _station = null!;
    private ShipHealthState _health = null!;
    private bool _shipReady;
    private int _warmupFrames;
    private int _steps;
    private bool _finished;

    public override void _Ready()
    {
        GD.Print("=== ProbeStationDeath ===");

        _station = new Station { Name = "Station", Position = new Vector3(5f, 0f, 0f) };
        _station.CollisionLayer = CollisionLayers.Station;
        _station.CollisionMask = CollisionLayers.Ship;
        _station.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 1f } });
        _station.AddChild(new StationState { Name = "StationState", MaxHp = 5000 });
        AddChild(_station);
        GD.Print($"  station id = {_station.State?.StationNetId ?? -1}");

        var shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (shipScene == null)
        {
            GD.PrintErr("Ship.tscn не загрузился");
            GetTree().Quit(1);
            return;
        }
        _ship = shipScene.Instantiate<Ship>();
        _ship.Name = "DeathShip";
        AddChild(_ship);
        _health = _ship.GetNode<ShipHealthState>("ShipHealthState");
        // Низкий HP + высокая скорость: одно попадание → Hp<=0 → смерть.
        _health.MaxHp = 10;
        _health.Hp = 10;

        _ship.SetPhysicsProcess(false);
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _ship.GlobalPosition = new Vector3(3.0f, 0f, 0f);
        _ship.LinearVelocity = new Vector3(40f, 0f, 0f);

        _shipReady = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_shipReady || _finished)
            return;
        _warmupFrames++;
        if (_warmupFrames < 4)
            return;

        _steps++;
        float dt = FlightModel.FixedDt;
        _ship.TestStepForTest(dt);
        GD.Print($"  step {_steps}: pos={_ship.GlobalPosition} vel={_ship.LinearVelocity} " +
                 $"dmg={_health.LastDamage} hp={_health.Hp}/{_health.MaxHp} count={_health.DamageTakenCount} " +
                 $"pilot={_ship.PilotPeerId} queued={_ship.IsQueuedForDeletion()}");

        if (_health.Hp <= 0 || _steps >= 15)
        {
            // П.21: Hp=0 → узел помечен к удалению, PilotPeerId == 0.
            bool hpZero = _health.Hp <= 0;
            bool queued = _ship.IsQueuedForDeletion();
            bool pilotCleared = _ship.PilotPeerId == 0;
            bool died = hpZero && queued && pilotCleared;
            GD.Print($"=== PROBE steps={_steps} hpZero={hpZero} queued={queued} " +
                     $"pilotCleared={pilotCleared} -> {(died ? "OK" : "FAIL")} ===");
            _finished = true;
            GetTree().Quit(died ? 0 : 1);
        }
    }
}
