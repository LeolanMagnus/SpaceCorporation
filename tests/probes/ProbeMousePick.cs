using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Проба перед шагом 2.2.2 (мышь → доворот носа). Отвечает на четыре вопроса,
/// от которых зависит конструкция пилотирования мышью:
///   1) доходит ли `InputEventMouseMotion`, посланный через viewport, до `_Input`
///      самого `RigidBody3D` (у `CollisionObject3D` ввод идёт через пикинг);
///   2) доходит ли он до дочернего `Node3D` (гипотеза — да, и тогда нужен
///      узел-«сборщик» под кораблём);
///   3) что в headless делает `Input.MouseMode = Captured/Hidden` (проба
///      ProbeInputHeadless показала: значение НЕ меняется, остаётся Visible —
///      значит гейт по MouseMode в тесте бесполезен);
///   4) работает ли в headless `Camera3D.ProjectRayOrigin/ProjectRayNormal` и
///      `Input.WarpMouse` — на них держится порог «курсор на корабле».
/// Запуск: godot --headless --path . res://tests/probes/ProbeMousePick.tscn
/// </summary>
public partial class ProbeMousePick : Node3D
{
    private PickableBody _body = null!;
    private ProbeMouseCollector2 _collector = null!;
    private Camera3D _cam = null!;
    private int _frames;

    public override void _Ready()
    {
        GD.Print("=== ProbeMousePick ===");

        _cam = new Camera3D { Name = "Cam", Position = new Vector3(0f, 3f, 12f), Far = 4000f };
        AddChild(_cam);
        _cam.LookAt(Vector3.Zero);
        _cam.MakeCurrent();

        _body = new PickableBody { Name = "Body", InputRayPickable = true };
        _body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 1f } });
        AddChild(_body);

        _collector = new ProbeMouseCollector2 { Name = "Collector" };
        _body.AddChild(_collector);

        GD.Print($"body: input_ray_pickable = {_body.InputRayPickable}, " +
                 $"process_input = {_body.IsProcessingInput()}, is_collision_object = {_body is CollisionObject3D}");
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_frames == 3)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GD.Print($"после Captured: MouseMode = {Input.MouseMode}");
            Input.MouseMode = Input.MouseModeEnum.Hidden;
            GD.Print($"после Hidden: MouseMode = {Input.MouseMode}");

            Vector2 before = GetViewport().GetMousePosition();
            Input.WarpMouse(new Vector2(640f, 360f));
            GD.Print($"WarpMouse: было {before}, стало {GetViewport().GetMousePosition()} (сразу), окно = {GetWindow().Size}");

            // Две мышиные дельты: одна «в центр» (курсор на корабле), вторая в угол.
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Relative = new Vector2(40f, -12f),
                Position = new Vector2(640f, 360f),
                GlobalPosition = new Vector2(640f, 360f),
            });
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Relative = new Vector2(-90f, 30f),
                Position = new Vector2(5f, 700f),
                GlobalPosition = new Vector2(5f, 700f),
            });
        }

        if (_frames == 4)
        {
            GD.Print($"после пуша: mouse_pos = {GetViewport().GetMousePosition()}, body _Input = {_body.InputCount}, " +
                     $"collector = {_collector.Count}, body_last = {_body.LastPos}, " +
                     $"collector_last = {_collector.LastPos}");

            Vector2 center = new Vector2(640f, 360f);
            Vector2 corner = new Vector2(5f, 700f);
            Vector3 ro = _cam.ProjectRayOrigin(center);
            Vector3 rn = _cam.ProjectRayNormal(center);
            Vector3 co = _cam.ProjectRayOrigin(corner);
            Vector3 cn = _cam.ProjectRayNormal(corner);
            GD.Print($"ray center: origin={ro} normal={rn} -> расстояние до (0,0,0) = {DistToRay(Vector3.Zero, ro, rn):F3}");
            GD.Print($"ray corner: origin={co} normal={cn} -> расстояние до (0,0,0) = {DistToRay(Vector3.Zero, co, cn):F3}");

            bool ok = _collector.Count >= 2;
            GD.Print($"=== PROBE: body_input={_body.InputCount} collector={_collector.Count} " +
                     $"mouse_mode_changed={Input.MouseMode != Input.MouseModeEnum.Visible} -> {(ok ? "OK" : "FAIL")} ===");
            GetTree().Quit(ok ? 0 : 1);
        }
    }

    private static float DistToRay(Vector3 p, Vector3 o, Vector3 d)
    {
        Vector3 w = p - o;
        float t = w.Dot(d);
        return (o + d * t - p).Length();
    }
}

/// <summary>Тело-наследник, чтобы посчитать вызовы `_Input` именно у него.</summary>
public partial class PickableBody : RigidBody3D
{
    public int InputCount;
    public Vector2 LastPos;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m)
        {
            InputCount++;
            LastPos = m.Position;
            GD.Print($"    body _Input: rel={m.Relative} pos={m.Position}");
        }
    }
}

/// <summary>Дочерний обычный Node3D: доходит ли до него мышь.</summary>
public partial class ProbeMouseCollector2 : Node3D
{
    public int Count;
    public Vector2 LastPos;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m)
        {
            Count++;
            LastPos = m.Position;
            GD.Print($"    collector _Input: rel={m.Relative} pos={m.Position}");
        }
    }
}
