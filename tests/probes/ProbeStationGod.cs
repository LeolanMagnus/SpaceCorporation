using Godot;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П6 (stage4-plan §4.4, шаг 4.9 п.25): god mode блокирует урон.
/// Как ProbeStationRam, но сервер включает GodMode до тарана. Высокая скорость
/// (60 м/с): без god mode Hp упал бы; с god mode → Hp == MaxHp и DamageTakenCount == 0,
/// НО упругий отскок всё равно применяется (иначе корабль ползёт внутри тела).
/// God mode включаем серверным методом SetGodModeServerOnly (offline = авторитет).
/// Запуск: godot --headless --path . res://tests/probes/ProbeStationGod.tscn
/// </summary>
public partial class ProbeStationGod : Node3D
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
        GD.Print("=== ProbeStationGod ===");

        _station = new Station { Name = "Station", Position = new Vector3(5f, 0f, 0f) };
        _station.CollisionLayer = CollisionLayers.Station;
        _station.CollisionMask = CollisionLayers.Ship;
        _station.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 1f } });
        _station.AddChild(new StationState { Name = "StationState", MaxHp = 5000 });
        AddChild(_station);

        var shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (shipScene == null)
        {
            GD.PrintErr("Ship.tscn не загрузился");
            GetTree().Quit(1);
            return;
        }
        _ship = shipScene.Instantiate<Ship>();
        _ship.Name = "GodShip";
        AddChild(_ship);
        _health = _ship.GetNode<ShipHealthState>("ShipHealthState");
        _health.MaxHp = 100;
        _health.Hp = 100;
        // Шаг 4.9: включаем god mode серверным путём (offline-роль = авторитет).
        _health.SetGodModeServerOnly(true);
        GD.Print($"  god mode on = {_health.GodMode}");

        _ship.SetPhysicsProcess(false);
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _ship.GlobalPosition = new Vector3(3.0f, 0f, 0f);
        _ship.LinearVelocity = new Vector3(60f, 0f, 0f);

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
        Vector3 after = _ship.LinearVelocity;
        GD.Print($"  step {_steps}: pos={_ship.GlobalPosition} vel={after} " +
                 $"dmg={_health.LastDamage} hp={_health.Hp}/{_health.MaxHp} count={_health.DamageTakenCount}");

        if (_steps >= 8)
        {
            // П.25: god mode → Hp не изменился, урона нет; но отскок есть (vel.X<0).
            bool hpFull = _health.Hp == _health.MaxHp;
            bool noDamage = _health.DamageTakenCount == 0 && _health.LastDamage == 0;
            bool bounced = after.X < 0f;
            bool ok = hpFull && noDamage && bounced;
            GD.Print($"=== PROBE steps={_steps} hpFull={hpFull} noDamage={noDamage} " +
                     $"bounced={bounced} -> {(ok ? "OK" : "FAIL")} ===");
            _finished = true;
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
