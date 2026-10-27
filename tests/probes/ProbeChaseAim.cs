using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Проба перед шагом 2.2.3 (камера-преследователь + гейт прицела мыши).
/// Отвечает на три вопроса, от которых зависит тест п.6.2:
///   1) какая ориентация у дочерней `Camera3D` без явного `LookAt` (гипотеза:
///      identity, то есть смотрит вдоль мировой -Z, как и корма корабля);
///   2) проходит ли луч камеры, восстановленный из ЦЕНТРА окна, через центр
///      корпуса (гейт прицела обязан быть активен) и из УГЛА 64×64-окна в
///      headless (гейт обязан быть выключен) — при пороге AimRadiusM = 6 м;
///   3) куда реально поворачивает нос `ApplyLook` (ожидание: dx=+30 при
///      чувствительности 0.0025 даёт Euler.Y = -0.075 рад).
/// Запуск: godot --headless --path . res://tests/probes/ProbeChaseAim.tscn
/// </summary>
public partial class ProbeChaseAim : Node3D
{
    private Ship _ship = null!;
    private int _frames;

    public override void _Ready()
    {
        GD.Print("=== ProbeChaseAim ===");

        var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (scene == null)
        {
            GD.PrintErr("Ship.tscn не загрузился");
            GetTree().Quit(1);
            return;
        }

        _ship = (Ship)scene.Instantiate();
        _ship.Name = "Ship";
        AddChild(_ship);
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames < 3)
            return;

        Camera3D? cam = GetViewport()?.GetCamera3D();
        bool ok = true;

        if (cam == null)
        {
            GD.Print("[FAIL] в вьюпорте нет current-камеры: гейт прицела нечем вычислять");
            GetTree().Quit(1);
            return;
        }

        Vector2 size = GetViewport().GetVisibleRect().Size;
        GD.Print($"окно = {size}, камера = {cam.Name}, global = {cam.GlobalPosition}, " +
                 $"basis = {cam.GlobalTransform.Basis.GetEuler()}");

        // 1) Ориентация камеры без LookAt: дочерняя, identity → смотрит вдоль -Z.
        // (В Godot 4 `Vector3.Forward` = -Z, а `Vector3.Back` = +Z.)
        Vector3 fwd = -cam.GlobalTransform.Basis.Z;
        GD.Print($"взгляд камеры = {fwd} (ожидание ~(0, 0, -1))");
        ok &= fwd.Dot(Vector3.Forward) > 0.99f;

        // 2) Луч из центра окна обязан проходить через центр корпуса.
        Vector2 center = new Vector2(size.X * 0.5f, size.Y * 0.5f);
        float dCenter = DistToRay(_ship.GlobalPosition,
            cam.ProjectRayOrigin(center), cam.ProjectRayNormal(center));
        GD.Print($"луч из центра {center}: расстояние до корпуса = {dCenter:F3} м (порог 6 м)");
        ok &= dCenter <= 6f;

        // Угол окна: луч обязан проходить дальше порога.
        Vector2 corner = new Vector2(1f, 1f);
        float dCorner = DistToRay(_ship.GlobalPosition,
            cam.ProjectRayOrigin(corner), cam.ProjectRayNormal(corner));
        GD.Print($"луч из угла {corner}: расстояние до корпуса = {dCorner:F3} м (порог 6 м)");
        ok &= dCorner > 6f;

        // 3) ApplyLook: dx=+30 → Euler.Y = -30*0.0025 = -0.075 рад.
        _ship.TestAddLookDelta(new Vector2(30f, 0f));
        _ship.TestStepForTest(1.0f / 60.0f);
        Vector3 euler = _ship.GlobalTransform.Basis.GetEuler();
        GD.Print($"после дельты (30, 0): euler = {euler} (ожидание Y ≈ -0.075)");
        ok &= Mathf.Abs(euler.Y + 30f * _ship.MouseSensitivity) < 1e-3f;

        GD.Print($"=== PROBE: aim_center={dCenter:F2} aim_corner={dCorner:F2} " +
                 $"yaw={euler.Y:F4} -> {(ok ? "OK" : "FAIL")} ===");
        GetTree().Quit(ok ? 0 : 1);
    }

    private static float DistToRay(Vector3 p, Vector3 o, Vector3 d)
    {
        Vector3 w = p - o;
        float t = w.Dot(d);
        return (o + d * t - p).Length();
    }
}
