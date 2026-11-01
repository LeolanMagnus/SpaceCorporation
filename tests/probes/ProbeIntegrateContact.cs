using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П3 (stage4-plan §4.4): можно ли делать IntersectShape-запрос
/// НЕ из _PhysicsProcess, а прямо внутри _IntegrateForces RigidBody c
/// CustomIntegrator (как в Ship). Пространство физики на время шага
/// «запирается» — запрос может вернуть пусто или напечатать
/// "space is locked". Если нельзя — учёт контакта вынесем в _PhysicsProcess
/// авторитета (после шага) либо в кинематический тест без CustomIntegrator.
/// Запуск: godot --headless --path . res://tests/probes/ProbeIntegrateContact.tscn
/// </summary>
public partial class ProbeIntegrateContact : Node3D
{
    public override void _Ready()
    {
        GD.Print("=== ProbeIntegrateContact ===");

        var station = new StaticBody3D { Name = "Station", Position = new Vector3(5f, 0f, 0f) };
        station.CollisionLayer = CollisionLayers.Station;
        station.CollisionMask = CollisionLayers.Ship;
        station.AddChild(new CollisionShape3D { Name = "S", Shape = new SphereShape3D { Radius = 1f } });
        AddChild(station);

        // Корабль-подобное тело: влетает (4.0,0,0) со скоростью +X,CustomIntegrator.
        var body = new ContactBody { Name = "Ship" };
        body.CustomIntegrator = true;
        body.CollisionLayer = CollisionLayers.Ship;
        body.CollisionMask = CollisionLayers.Station;
        body.AddChild(new CollisionShape3D { Name = "S", Shape = new SphereShape3D { Radius = 0.5f } });
        body.Position = new Vector3(4.0f, 0f, 0f);
        body.LinearVelocity = new Vector3(3f, 0f, 0f);
        AddChild(body);
        _body = body;
    }

    private ContactBody _body = null!;

    public override void _PhysicsProcess(double delta)
    {
        if (_body.Done)
        {
            GD.Print($"=== PROBE integrate_hits={_body.HitsSeen} -> {(_body.HitsSeen > 0 ? "QUERY_WORKS" : "QUERY_EMPTY (locked?)")} ===");
            GetTree().Quit(0);
        }
    }
}

public partial class ContactBody : RigidBody3D
{
    public bool Done;
    public int HitsSeen;
    private int _steps;

    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        _steps++;
        float dt = (float)sd.GetStep();
        if (dt <= 0f) dt = 1.0f / 60.0f;

        var v = sd.LinearVelocity;
        var start = sd.Transform;
        start.Origin -= v * dt;
        var next = new Transform3D(start.Basis, start.Origin + v * dt);

        // Запрос в ЗАПРОСИМОЙ позе (как в Ship). Пытаемся из интегратора.
        try
        {
            var space = GetWorld3D().DirectSpaceState;
            var shape = GetNode<CollisionShape3D>("S").Shape;
            var pq = new PhysicsShapeQueryParameters3D
            {
                Shape = shape,
                Transform = next,
                CollisionMask = CollisionLayers.Station,
                CollideWithBodies = true,
                CollideWithAreas = false,
                Exclude = new Godot.Collections.Array<Rid> { GetRid() },
                Margin = 0.0f,
            };
            var hits = space.IntersectShape(pq, 8);
            GD.Print($"  [_IntegrateForces] frame step={_steps} next.origin={next.Origin} hits={hits.Count}");
            HitsSeen += hits.Count;
        }
        catch (Exception e)
        {
            GD.Print($"  [_IntegrateForces] исключение запроса: {e.GetType().Name}: {e.Message}");
        }

        sd.Transform = next;
        sd.LinearVelocity = v;

        if (_steps >= 5)
            Done = true;
    }
}
