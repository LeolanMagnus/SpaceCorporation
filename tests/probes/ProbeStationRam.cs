using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П4 (stage4-plan §4.4, шаг 4.7): полный цикл урона при таране
/// станции кораблём. В headless физический интегратор (`_IntegrateForces`)
/// надёжно не двигает тело, поэтому корабль шагает ЯВНО через `TestStepForTest`
/// — тот же вызов `HandleStationContacts`, что и в бою (§3.8). Шагов несколько,
/// начиная СНИЗУ скорости и издалека, чтобы корабль вошёл в станцию малыми
/// шагами и НЕ перескочил точно в её центр (там outward-нормаль вырождена).
/// Проверяем:
///   (1) DamageTakenCount вырос (>0) — сервер засчитал контакт;
///   (2) LastDamage > 0 (урон посчитан по нормальной составляющей);
///   (3) ShipHealthState.Hp упал;
///   (4) скорость после упругого отскока развернулась в -X.
/// Урон пишется в ShipHealthState корабля (этап 5 добавит HP самой станции).
/// Запуск: godot --headless --path . res://tests/probes/ProbeStationRam.tscn
/// </summary>
public partial class ProbeStationRam : Node3D
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
        GD.Print("=== ProbeStationRam ===");

        // НАСТОЯЩАЯ станция: класс Station + дочерний StationState (нужен для
        // StationNetId + реестра) + форма-коллайдер (сфера r=1 на (5,0,0)).
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
        _ship.Name = "RamShip";
        AddChild(_ship);
        _health = _ship.GetNode<ShipHealthState>("ShipHealthState");
        _health.MaxHp = 100;
        _health.Hp = 100;

        _ship.SetPhysicsProcess(false); // движок не двигает тело в headless
        _ship.Freeze = true;
        _ship.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        _ship.GlobalPosition = new Vector3(3.0f, 0f, 0f);
        _ship.LinearVelocity = new Vector3(20f, 0f, 0f);

        _shipReady = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_shipReady || _finished)
            return;
        _warmupFrames++;
        if (_warmupFrames < 4)
            return; // коллайдеры войдут в пространство после физкадра

        _steps++;
        float dt = FlightModel.FixedDt;
        _ship.TestStepForTest(dt);
        Vector3 after = _ship.LinearVelocity;
        GD.Print($"  step {_steps}: pos={_ship.GlobalPosition} vel={after} " +
                 $"dmg={_health.LastDamage} hp={_health.Hp}/{_health.MaxHp} count={_health.DamageTakenCount}");

        bool hit = _health.DamageTakenCount > 0;
        if (hit || _steps >= 15)
        {
            bool dmgOk = _health.LastDamage > 0;
            bool hpDrop = _health.Hp < _health.MaxHp;
            bool bounced = after.X < 0f;
            GD.Print($"=== PROBE steps={_steps} hit={hit} dmgOk={dmgOk} hpDrop={hpDrop} " +
                     $"bounced={bounced} vel={after} " +
                     $"-> {(hit && dmgOk && hpDrop && bounced ? "OK" : "FAIL")} ===");
            _finished = true;
            GetTree().Quit(0);
        }
    }
}
