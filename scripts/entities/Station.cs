using Godot;

namespace SpaceCorporation;

/// <summary>
/// Станция (Этап 4, шаг 4.1): статическое тело слоя Station. Корабль с маской
/// <see cref="CollisionLayers.ShipMask"/> сталкивается с ней, хитскан-луч
/// (LaserMask) тоже её видит.
///
/// В отличие от корабля/мишени станция НЕ симулируется: у неё нет интегратора,
/// нет батча и нет SpatialIndex-участия (шаг 4.4) — статика приходит пирам только
/// спавн-снимком <see cref="MultiplayerSpawner"/> + спавн-флагами свойств
/// <see cref="StationState"/> (StationReplication.tres, replication_mode = 0).
///
/// Идентификатор — StationNetId из общего счётчика (реестр NetworkManager,
/// шаг 4.2); путь узла идентификатором не является (§3.4).
/// </summary>
public partial class Station : StaticBody3D
{
    public StationState? State => GetNodeOrNull<StationState>("StationState");

    // ─────────────────── П8: обитаемая палуба (docs/landing-mechanic-plan.md) ───────────────────
    // Геометрия в Station.tscn: HabDeck — плита 9.6×0.2×9.6 (2× платформы
    // LandingPad 4.8) с бортиками 1 м по периметру; HabitatZone — куб зоны
    // обитаемости 9.6×8×9.6 над палубой + HabZoneViz (полупрозрачный
    // светло-голубый куб 80% прозрачности, скрыт по умолчанию). Палуба —
    // часть станции, а не посадочной площадки.

    /// <summary>Высота зоны обитаемости над палубой (по ТЗ — 8 м).</summary>
    [Export] public float HabitatHeight { get; set; } = 8f;

    /// <summary>Сторона обитаемой палубы = 2× стороны платформы (4.8 → 9.6).
    /// Справочное значение для расчёта зоны (геометрия — в .tscn).</summary>
    [Export] public float HabitatDeckSize { get; set; } = 9.6f;

    /// <summary>Включить дебаг-визуализацию зоны обитаемости (ТЗ П8).</summary>
    [Export] public bool ShowHabitatViz { get; set; } = false;

    private Area3D? _habitatZone;
    private Node3D? _habDeck;
    private MeshInstance3D? _habitatViz;

    /// <summary>Зона обитаемости над палубой (Area3D, П8). Может быть null,
    /// если станция собрана без палубы (старые стенды).</summary>
    public Area3D? HabitatZone => _habitatZone;

    /// <summary>Верхняя поверхность обитаемой палубы (мировая координата).</summary>
    public Vector3 HabDeckSurfaceGlobal
    {
        get
        {
            var deck = _habDeck ?? GetNodeOrNull<Node3D>("HabDeck");
            return (deck ?? (Node3D)this).GlobalPosition + GlobalTransform.Basis.Y * 0.2f;
        }
    }

    /// <summary>Точка внутри куба обитаемой палубы (от поверхности палубы
    /// до <see cref="HabitatHeight"/> над ней)? Границы — по локальным осям
    /// станции вокруг центра палубы.</summary>
    public bool IsPointInHabitatZone(Vector3 worldPoint)
    {
        var deck = _habDeck ?? GetNodeOrNull<Node3D>("HabDeck");
        if (deck == null)
            return false;
        Vector3 local = ToLocal(worldPoint);
        Vector3 d = deck.Position;
        float h = HabitatDeckSize * 0.5f;
        return local.X >= d.X - h && local.X <= d.X + h
            && local.Z >= d.Z - h && local.Z <= d.Z + h
            && local.Y >= d.Y - 0.01f && local.Y <= d.Y + 0.2f + HabitatHeight;
    }

    // ─────────────────── Этап 5 (шаг 5.2): стыковка ───────────────────
    // Обёртки поверх byte-полей StationState (не [Export]: Godot не реплицирует
    // enum, в буфере лежит DockStateValue).

    /// <summary>Фаза стыковки типизированно (читает byte из StationState).</summary>
    public DockState DockingState
        => (DockState)(State?.DockStateValue ?? (byte)DockState.None);

    /// <summary>Адрес корабля в сделке (0 — никого).</summary>
    public int DockerNetId => State?.DockShipNetId ?? 0;

    /// <summary>«Огни горят» для корабля DockerNetId.</summary>
    public bool DockApproved => State?.DockApproved ?? false;

    /// <summary>Посадочная площадка станции (модуль стыковки). Станция принимает
    /// посадку только там, где стоит <see cref="LandingPad"/>; если площадок
    /// несколько — ближайшая к кораблю выбирается в <see cref="DockPointGlobal"/>.</summary>
    public LandingPad? LandingPad => GetNodeOrNull<LandingPad>("LandingPad");

    /// <summary>Точка стыковки (шаг 5.6: гейты «в зоне»/пружины). Читается из
    /// посадочной площадки (модуль «стыковка»); fallback на старую точку
    /// DockingPoint и центр станции — для неполных сцен/стендов.</summary>
    public Vector3 DockPointGlobal
    {
        get
        {
            var pad = GetNodeOrNull<LandingPad>("LandingPad");
            if (pad != null)
                return pad.LandingPointGlobal;
            return (GetNodeOrNull<Node3D>("DockingPoint") ?? (Node3D)this).GlobalPosition;
        }
    }

    /// <summary>
    /// Единственный путь записи dock-полей станции (шаг 5.2): пишет только
    /// авторитетная сторона (сервер; в offline — локальный процесс, как у
    /// коллизийного урона Этапа 4). Гейт по образцу SetGodModeServerOnly /
    /// гейта Hp: вызов с роли клиента -> PushError и отказ (тест п.4).
    /// </summary>
    public void SetDockStateServerOnly(DockState state, int shipNetId)
    {
        var st = State;
        if (st == null)
        {
            GD.PushError("Station: SetDockStateServerOnly — нет дочернего StationState");
            return;
        }
        var nm = NetworkManager.Instance;
        if (nm != null && !nm.IsAuthoritySide)
        {
            GD.PushError($"Station#{st.StationNetId}: SetDockStateServerOnly({state}, " +
                         $"{shipNetId}) вызван на не-авторитете — док-состояние пишет " +
                         "только сервер (Этап 5, шаг 5.2).");
            return;
        }
        st.DockStateValue = (byte)state;
        st.DockShipNetId = state == DockState.None ? 0 : shipNetId;
        st.DockApproved = state >= DockState.Approved && state < DockState.Docked;
    }

    public override void _Ready()
    {
        // Станция спавнится сервером в начале мира (шаг 4.3); своей физики/симуляции
        // у неё нет — только существование, коллизия и адрес.
        // Шаг 5.7 + рефакторинг «посадочная площадка»: маяк «огней» переехал на
        // модуль LandingPad (его _Process красит маяк по DockApproved-реплике).

        // П8 (docs/landing-mechanic-plan.md): обитаемая палуба станции —
        // ссылки на HabDeck/HabitatZone (+ дебаг-визуализация зоны).
        _habitatZone = GetNodeOrNull<Area3D>("HabitatZone");
        _habDeck = GetNodeOrNull<Node3D>("HabDeck");
        _habitatViz = GetNodeOrNull<MeshInstance3D>("HabitatZone/HabZoneViz");
        if (_habitatViz != null) _habitatViz.Visible = ShowHabitatViz;

        // Шаг 5.13 (п.35): dock-состояние станции в панели дебаг-инфы. Ключ —
        // Station#{StationNetId}, как Ship#{ShipNetId} (§3.1 п.6).
        DebugInfoBus.RegisterInfoSource($"Station#{State?.StationNetId ?? 0}", () =>
            $"station#{State?.StationNetId ?? 0}: " +
            $"dock = {((DockState)(State?.DockStateValue ?? 0))}, " +
            $"docker = {(State != null && State.DockShipNetId != 0 ? State.DockShipNetId.ToString() : "—")}, " +
            $"lights = {(DockApproved ? "on" : "off")}");
    }

    public override void _ExitTree()
    {
        // Снятие по образцу RemoveInfoSource у корабля: ключ — часть контракта.
        DebugInfoBus.RemoveInfoSource($"Station#{State?.StationNetId ?? 0}");
    }

    /// <summary>Шаг 5.7 + модуль «посадочная площадка»: «огни горят» — маяк
    /// переехал на LandingPad, но seam остался на станции (тесты п.18–п.20
    /// дёргают `_station._Process`); станция делегирует кадр площадке.</summary>
    public override void _Process(double delta)
    {
        GetNodeOrNull<LandingPad>("LandingPad")?._Process(delta);
    }
}
