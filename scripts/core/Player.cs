using Godot;

namespace SpaceCorporation;

/// <summary>
/// Игрок-персонаж: капсула + привязанная к ней камера (scenes/Player.tscn).
///
/// Ключевая механика задела на этап 6 — крепление к точкам (AttachPoint):
///   AttachTo(point) — игрок становится узлом точки крепления (кресло кабины,
///                     позже — пол станции/палубы) и летает/ходит вместе с ней;
///   Detach()        — возвращается в мировой корень сцены, сохраняя позицию.
///
/// Поэтому в Main.tscn игрок просто стоит рядом с кораблём, а сам факт «игрок
/// в кабине» задаётся кодом (точка Seat с IsDefaultForPlayer = true), а не
/// вложенностью узлов в сцене: иерархию можно менять в рантайме.
///
/// Горизонтальное перемещение по палубе (этап 6) — MoveInAttachedPlane: движение
/// в локальной плоскости XZ точки крепления, то есть «вперёд» для игрока =
/// «вперёд» корабля или станции, к которой он пристёгнут.
/// </summary>
public partial class Player : CharacterBody3D
{
    /// <summary>Высота капсулы (совпадает с CapsuleShape3D в scenes/Player.tscn).</summary>
    public const float CapsuleHeight = 1.8f;

    [Export] public NodePath CameraRigPath { get; set; } = "CameraRig";

    /// <summary>Точка крепления, к которой пристёгнут игрок (null — свободен).</summary>
    public AttachPoint? AttachedTo { get; private set; }

    public bool IsAttached => AttachedTo != null;

    /// <summary>Куда игрок возвращается при Detach (мировой корень сцены).</summary>
    private Node3D _worldRoot = null!;

    private Node3D _cameraRig = null!;
    private bool _awaitingDefaultPoint = true;
    private int _defaultPointAttempts;

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.Character;
        // Пока игрок сидит в корабле, он не должен сталкиваться с его корпусом:
        // маска намеренно без слоя Ship. Столкновения со станцией и мишенями — есть.
        CollisionMask = CollisionLayers.Station | CollisionLayers.Target;
        MotionMode = MotionModeEnum.Grounded;
        UpDirection = Vector3.Up;
        FloorMaxAngle = Mathf.DegToRad(50f);

        _cameraRig = GetNode<Node3D>(CameraRigPath);
        _worldRoot = GetParent() as Node3D ?? throw new System.InvalidOperationException(
            "Player должен лежать в сцене внутри 3D-узла (Main), чтобы было куда открепляться");

        // Автопристёгивание откладывается на первый физический шаг: Reparent во время
        // _Ready() запрещён (дерево сцены ещё достраивается), а на первом шаге все
        // точки уже в группе attach_point — порядок узлов в Main.tscn не важен.
        _awaitingDefaultPoint = true;

        // Дебаг-кнопки: цикл «открепиться → свободно → пристегнуться обратно» — ровно
        // то, что понадобится на этапе 6 для перехода корабль ↔ станция.
        DebugInfoBus.RegisterDebugButton("Detach Player", () =>
        {
            Detach();
            GD.Print($"player detached, global = {Fmt(GlobalPosition)}");
        });
        DebugInfoBus.RegisterDebugButton("Attach Player to Seat", () =>
        {
            AttachToDefaultPoint();
            GD.Print($"player attached to {(AttachedTo?.Name ?? "<nothing>")}, " +
                     $"parent = {GetParent()?.GetName()}, global = {Fmt(GlobalPosition)}");
        });

        DebugInfoBus.RegisterInfoSource("Player", () =>
            $"player: parent = {GetParent()?.GetName()}, " +
            $"attached = {AttachedTo?.Name ?? "-"}, " +
            $"pos = {Fmt(GlobalPosition)}");
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";

    public override void _PhysicsProcess(double delta)
    {
        if (!_awaitingDefaultPoint)
            return;

        if (AttachToDefaultPoint())
        {
            _awaitingDefaultPoint = false;
            return;
        }

        // Точки ещё нет в группе (её сцена достраивается) — пробуем в следующем шаге,
        // но бесконечно молчать не будем: через секунду предупреждаем один раз.
        if (++_defaultPointAttempts == 60)
        {
            GD.PushWarning("Player: точка крепления по умолчанию " +
                           "(AttachPoint.IsDefaultForPlayer) так и не найдена");
        }
    }

    /// <summary>Пристегнуться к точке с [Export] IsDefaultForPlayer = true.</summary>
    public bool AttachToDefaultPoint()
    {
        foreach (var node in GetTree().GetNodesInGroup(AttachPoint.GroupName))
        {
            if (node is AttachPoint point && point.IsDefaultForPlayer &&
                GodotObject.IsInstanceValid(point))
            {
                AttachTo(point);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Пристегнуться к точке. Игрок переносится в иерархию точки (Reparent), поэтому
    /// все её перемещения/повороты (корабль, станция) передаются игроку и его камере.
    /// snapToAttachPoint = true — встать в позу, заданную точкой (PlayerOffset),
    /// лицом вдоль её -Z; false — сохранить мировое положение.
    /// </summary>
    public void AttachTo(AttachPoint point, bool snapToAttachPoint = true)
    {
        if (point == null || !GodotObject.IsInstanceValid(point))
        {
            GD.PrintErr("Player.AttachTo: точка крепления недействительна");
            return;
        }
        if (AttachedTo == point)
            return;
        if (IsAttached)
            Detach();

        AttachedTo = point;
        Reparent(point, keepGlobalTransform: !snapToAttachPoint);
        if (snapToAttachPoint)
            Transform = new Transform3D(Basis.Identity, point.PlayerOffset);

        Velocity = Vector3.Zero;
        ResetPhysicsInterpolation();
    }

    /// <summary>
    /// Открепиться: игрок возвращается в мировой корень сцены, мировая позиция
    /// сохраняется. Дальше (этап 6) его можно пристегнуть к другой сцене.
    /// </summary>
    public void Detach()
    {
        if (!IsAttached)
            return;

        AttachedTo = null;
        Reparent(_worldRoot, keepGlobalTransform: true);
        Velocity = Vector3.Zero;
        ResetPhysicsInterpolation();
    }

    /// <summary>
    /// Перемещение в горизонтальной (для точки крепления) плоскости — задел на этап 6:
    /// WASD-ходьба по палубе корабля или станции. Ось X = право точки, Z = её назад,
    /// «вверх» точки — её локальный +Y, поэтому игрок не «всплывает» относительно палубы.
    /// Ввод в этой версии ещё не подключается (клавиши игрока — этап 2/6).
    /// </summary>
    public void MoveInAttachedPlane(Vector2 input, float speed = 3f)
    {
        if (!IsAttached || AttachedTo == null)
            return;

        var basis = AttachedTo.GlobalTransform.Basis;
        // Вертикаль относительно палубы (магнитные ботинки) добавится на этапе 6.
        var wish = basis.X * input.X + basis.Z * input.Y;
        Velocity = wish * speed;
        MoveAndSlide();
    }

    /// <summary>Камера игрока (для дебаг-инфо и будущего прицела).</summary>
    public Camera3D? GetActiveCamera() =>
        _cameraRig?.GetNodeOrNull<Camera3D>("Camera3D");
}
