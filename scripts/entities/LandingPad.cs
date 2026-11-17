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

    /// <summary>Типоразмер площадки (П1, docs/landing-mechanic-plan.md):
    /// площадка принимает корабль, только если SiteSize >= SizeClass корабля.
    /// Площадка меньше корабля — отказ: «посадка невозможна - нет подходящей
    /// площадки». Пока в проекте одна площадка (LandingSiteS) — типоразмера S.</summary>
    [Export] public ShipSizeClass SiteSize { get; set; } = ShipSizeClass.S;

    /// <summary>Примет ли эта площадка корабль типоразмера cls.</summary>
    public bool AcceptsShip(ShipSizeClass cls) => SiteSize >= cls;

    /// <summary>Высота поверхности палубы над корнем площадки (Deck mesh:
    /// центр 0.1, полувысота 0.1 → верх 0.2). Правится в инспекторе вместе
    /// с геометрией платформы, код посадки её не хардкодит.</summary>
    [Export] public float DeckTopOffset { get; set; } = 0.2f;

    /// <summary>Точка на середине поверхности палубы (мировая) — «пол», на
    /// который площадка опускает корабль при захвате (П7).</summary>
    public Vector3 DeckSurfaceGlobal => GlobalPosition + GlobalBasis.Y * DeckTopOffset;

    [Export] public NodePath LandingPointPath { get; set; } = new("DockingPoint");
    [Export] public NodePath DetectorPath { get; set; } = new("LandingZone");
    [Export] public NodePath BeaconPath { get; set; } = new("DockingPoint/DockBeacon");
    /// <summary>Узел-группа из 4 угновых посадочных огней (П4): горят все
    /// вместе с маяком, когда стыковка одобрена (тот же DockApproved).</summary>
    [Export] public NodePath CornerLightsPath { get; set; } = new("PadLights");

    private Node3D? _landingPoint;
    private Area3D? _detector;
    private MeshInstance3D? _beacon;
    private Node3D? _cornerLights;
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
        _cornerLights = GetNodeOrNull<Node3D>(CornerLightsPath);
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
        bool on = _ownerStation?.DockApproved ?? false;
        if (_beacon != null && _beacon.Visible != on)
            _beacon.Visible = on;
        // Посадочные огни (П4): 4 зелёных огонька в углах платформы — те же
        // условия горения, что у маяка.
        if (_cornerLights != null && _cornerLights.Visible != on)
            _cornerLights.Visible = on;
    }

    /// <summary>Есть ли у станции-владельца корабль в финальной фазе стыковки
    /// Docked (П9/П10: условие статуса «пристыкован» для игроков в зоне).</summary>
    public bool HasDockedShip() => _ownerStation != null
        && GodotObject.IsInstanceValid(_ownerStation)
        && _ownerStation.DockingState == DockState.Docked;

    /// <summary>Станция-владелец площадки (null, если площадка вне станции) —
    /// у неё живёт обитаемая палуба и зона обитаемости (П8: геометрия и логика
    /// перенесены в Station.tscn / Station.cs).</summary>
    public Station? OwnerStation =>
        _ownerStation != null && GodotObject.IsInstanceValid(_ownerStation) ? _ownerStation : null;
}
