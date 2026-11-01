using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П2 (stage4-plan §4.4): точный C#-биндинг поиска проникновения в
/// статическую станцию из CustomIntegrator. Проверяем (Godot 4.7 C#-биндинг):
///  1) space через Node3D.GetWorld3D().DirectSpaceState;
///  2) IntersectShape → Array[Dictionary]: collider, depth, direction, normal, point;
///  3) CollideShape (замена устаревшего ComputeContacts) → что возвращает;
///  4) какой коллайдер возвращается (Station StaticBody3D) и совпадает ли слой;
///  5) Exclude через Godot.Collections.Array&lt;Rid&gt; (типа Rid[] в C# НЕТ).
/// Запуск: godot --headless --path . res://tests/probes/ProbeStationPen.tscn
/// </summary>
public partial class ProbeStationPen : Node3D
{
    public override void _Ready()
    {
        GD.Print("=== ProbeStationPen ===");
        // Станция: сфера r=1 в (5,0,0), слой Station(2), маска Ship(1).
        var station = new StaticBody3D { Name = "ProbeStation", Position = new Vector3(5f, 0f, 0f) };
        station.CollisionLayer = CollisionLayers.Station;
        station.CollisionMask = CollisionLayers.Ship;
        station.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 1f } });
        AddChild(station);

        // Тестовое тело (корабль-подобное): в (4.5,0,0) — сфера r=0.5, центр
        // на 1.0 м от центра станции (r_station + r_body = 1.5) => проникновение
        // 1.0-1.5 = -0.5? Нет: расстояние 0.5? Позиция 4.5, станция 5.0 => d=0.5.
        // Сумма радиусов = 1.5 => проникновение 1.0 м, normal из станции в корабль = (-1,0,0).
        var body = new RigidBody3D { Name = "ProbeShip", Position = new Vector3(4.5f, 0f, 0f) };
        body.CollisionLayer = CollisionLayers.Ship;
        body.CollisionMask = CollisionLayers.Station;
        body.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 0.5f } });
        AddChild(body);

        _station = station;
        _body = body;
    }

    private StaticBody3D _station = null!;
    private RigidBody3D _body = null!;
    private bool _steppedOnce;

    public override void _PhysicsProcess(double delta)
    {
        if (_steppedOnce)
            return;
        _steppedOnce = true;

        var space = GetWorld3D().DirectSpaceState;
        GD.Print($"space = {space} (GetWorld3D={GetWorld3D()})");
        if (space == null)
        {
            GD.PrintErr("space null — проба не за чем");
            GetTree().Quit(1);
            return;
        }

        var qShape = _body.GetNode<CollisionShape3D>("Shape");
        var xform = _body.GlobalTransform;

        // Exclude: Rid[] в C# нет — только Godot.Collections.Array<Rid>.
        var exclude = new Godot.Collections.Array<Rid> { _body.GetRid() };

        bool found = false;

        // --- (а) IntersectShape: ищем пересечение/проникновение в Station.
        try
        {
            var pq = new PhysicsShapeQueryParameters3D
            {
                Shape = qShape.Shape,
                Transform = xform,
                CollisionMask = CollisionLayers.Station,
                CollideWithBodies = true,
                CollideWithAreas = false,
                Exclude = exclude,
                Margin = 0.0f,
            };
            var res = space.IntersectShape(pq, 4);
            GD.Print($"IntersectShape: count={res.Count}");
            foreach (Variant v in res)
            {
                var d = v.AsGodotDictionary();
                var col = d["collider"].As<GodotObject>();
                var node = col as Node;
                GD.Print($"  keys={string.Join(",", d.Keys)} collider={(node)?.Name} " +
                         $"asStation={col is Station} asStaticBody3D={col is StaticBody3D} " +
                         $"layer={(col as CollisionObject3D)?.GetCollisionLayer()} " +
                         $"depth={Get(d, "depth")} dir={Get(d, "direction")} " +
                         $"normal={Get(d, "normal")} point={Get(d, "point")} " +
                         $"shape={Get(d, "shape")} rid={Get(d, "rid")}");
                if (col != null)
                    found = true;
            }
        }
        catch (Exception e)
        {
            GD.Print($"IntersectShape исключение: {e.GetType().Name}: {e.Message}");
        }

        // --- (б) CollideShape (замена ComputeContacts) — с Motion=0 даёт
        //     rest-контакты/проникновение.
        try
        {
            var pq2 = new PhysicsShapeQueryParameters3D
            {
                Shape = qShape.Shape,
                Transform = xform,
                CollisionMask = CollisionLayers.Station,
                CollideWithBodies = true,
                Exclude = exclude,
                Motion = Vector3.Zero,
                Margin = 0.0f,
            };
            var res2 = space.CollideShape(pq2, 4);
            GD.Print($"CollideShape: count={res2.Count}");
            foreach (Variant v in res2)
            {
                var d = v.AsGodotDictionary();
                var node = d.ContainsKey("collider") ? d["collider"].As<GodotObject>() as Node : null;
                GD.Print($"  keys={string.Join(",", d.Keys)} collider={(node)?.Name} " +
                         $"depth={Get(d, "depth")} dir={Get(d, "direction")} " +
                         $"normal={Get(d, "normal")} point={Get(d, "point")}");
            }
        }
        catch (Exception e)
        {
            GD.Print($"CollideShape исключение: {e.GetType().Name}: {e.Message}");
        }

        // --- (в) GetRestInfo — нормаль контакта для точечного кинематического
        //     запроса (может пригодиться для отскока).
        try
        {
            var pq3 = new PhysicsShapeQueryParameters3D
            {
                Shape = qShape.Shape,
                Transform = xform,
                CollisionMask = CollisionLayers.Station,
                CollideWithBodies = true,
                Exclude = exclude,
                Motion = new Vector3(-0.1f, 0f, 0f),
            };
            var rest = space.GetRestInfo(pq3);
            if (rest != null && rest.Count > 0)
            {
                var col = rest["collider"].As<GodotObject>();
                GD.Print($"GetRestInfo: collider={(col as Node)?.Name} " +
                         $"normal={Get(rest, "normal")} point={Get(rest, "point")} depth={Get(rest, "depth")}");
            }
            else
            {
                GD.Print($"GetRestInfo: пусто/нуль (count={rest?.Count})");
            }
        }
        catch (Exception e)
        {
            GD.Print($"GetRestInfo исключение: {e.GetType().Name}: {e.Message}");
        }

        GD.Print($"station pos={_station.GlobalPosition} body pos={_body.GlobalPosition}");
        GD.Print($"=== PROBE found_station={found} -> {(found ? "OK" : "FAIL")} ===");
        GetTree().Quit(found ? 0 : 1);
    }

    private static string Get(Godot.Collections.Dictionary d, string key)
        => d.ContainsKey(key) ? d[key].ToString() : "<нет>";
}
