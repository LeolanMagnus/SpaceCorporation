using Godot;

namespace SpaceCorporation;

/// <summary>
/// Посадочная площадка (модуль «стыковка»): плоская квадратная платформа с
/// точкой зависания, детектором посадки и маяком разрешения.
///
/// Идея взята из X4 / Elite Dangerous: посадка привязана не к станции целиком,
/// а к ПЛОЩАДКЕ — модулю, который можно поставить на любой объект. Поэтому вся
/// геометрия и логика посадки живёт здесь, а владелец (станция, база, грузовая
/// платформа) лишь инстансит сцену <c>LandingPad.tscn</c> и даёт ей сетевой
/// адрес (ближайший предок со <see cref="StationState"/>).
///
/// Состав сцены:
///   - Pad (StaticBody3D) — сам квадрат площадки;
///   - DockingPoint (Node3D + <see cref="AttachPoint"/>) — точка зависания,
///     к которой ведёт серверная пружина (<c>Ship.StepDockSpring</c>);
///   - LandingZone (Area3D) — детектор НАД площадкой: вход корабля выставляет
///     ему флаг «посадка» (<see cref="Ship.NotifyLandingZoneEntered"/>);
///   - DockBeacon — «огни»: горят, когда владелец-станция одобрила стыковку
///     (наследует поведение маяка Station 5-й стадии).
///
/// Все ссылки — через <see cref="NodePath"/> рядом с корнем, чтобы сцену
/// можно было править без кода. Группа <see cref="GroupName"/> — способ найти
/// площадки без жёстких путей.
/// </summary>
public partial class LandingPad : Node3D
{
    /// <summary>Группа всех посадочных площадок в сцене.</summary>
    public const string GroupName = "landing_pad";

    [Export] public NodePath LandingPointPath { get; set; } = new("DockingPoint");
    [Export] public NodePath DetectorPath { get; set; } = new("LandingZone");
    [Export] public NodePath BeaconPath { get; set; } = new("DockingPoint/DockBeacon");

    private Node3D? _landingPoint;
    private Area3D? _detector;
    private MeshInstance3D? _beacon;
    private Station? _ownerStation;

    /// <summary>Точка зависания в мировых координатах: к ней ведёт пружина
    /// захвата и вокруг неё гейт «в зоне» (замена Station.DockPointGlobal).</summary>
    public Vector3 LandingPointGlobal =>
        (_landingPoint ?? (Node3D)this).GlobalPosition;

    public override void _Ready()
    {
        AddToGroup(GroupName);

        _landingPoint = GetNodeOrNull<Node3D>(LandingPointPath);
        _beacon = GetNodeOrNull<MeshInstance3D>(BeaconPath);
        _detector = GetNodeOrNull<Area3D>(DetectorPath);
        if (_detector != null)
        {
            _detector.BodyEntered += OnBodyEntered;
            _detector.BodyExited += OnBodyExited;
        }
        else
        {
            GD.PushError($"LandingPad: нет детектора {DetectorPath} — флаг «посадка» " +
                         "никому не придёт (сцена неполная).");
        }

        // Владелец площадки — ближайший предок-станция (для «огней» по
        // DockApproved). Площадка без станции-владельца (например, на земле/
        // груде) — легальна: детектор работает, маяк просто не загорается.
        for (Node n = GetParent(); n != null; n = n.GetParent())
        {
            if (n is Station st)
            {
                _ownerStation = st;
                break;
            }
        }
    }

    public override void _ExitTree()
    {
        if (_detector != null)
        {
            _detector.BodyEntered -= OnBodyEntered;
            _detector.BodyExited -= OnBodyExited;
        }
    }

    private void OnBodyEntered(Node3D body)
    {
        // Флаг «посадка» — только кораблям (Area ловит и character, и debris).
        if (body is Ship ship)
            ship.NotifyLandingZoneEntered(this);
    }

    private void OnBodyExited(Node3D body)
    {
        if (body is Ship ship)
            ship.NotifyLandingZoneExited(this);
    }

    /// <summary>«Огни» — производное от реплицируемого DockApproved владельца
    /// (тот же принцип, что маяк Station 5-й стадии: второй транспорт не нужен,
    /// у клиентов маяк загорается из реплики). Без станции-владельца погашен.</summary>
    public override void _Process(double delta)
    {
        if (_beacon != null)
        {
            bool on = _ownerStation?.DockApproved ?? false;
            if (_beacon.Visible != on)
                _beacon.Visible = on;
        }
    }
}
