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

    /// <summary>Точка стыковки из сцены (шаг 5.6: гейты «в зоне»/пружины).
    /// Если узла нет (неполная сцена) — центр станции: безопасный fallback.</summary>
    public Vector3 DockPointGlobal =>
        (GetNodeOrNull<Node3D>("DockingPoint") ?? (Node3D)this).GlobalPosition;

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
        // Шаг 5.7: «огни» — маркер видимости разрешения.
        _beacon = GetNodeOrNull<MeshInstance3D>("DockingPoint/DockBeacon");

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

    private MeshInstance3D? _beacon;

    /// <summary>Шаг 5.7: «огни» — производное от реплицируемого DockApproved.
    /// НЕ второй транспорт и не RPC: у клиентов маркер загорается сам, из
    /// реплики свойства (п.6 блока A, тесты п.18-п.20).</summary>
    public override void _Process(double delta)
    {
        if (_beacon != null)
        {
            bool on = DockApproved;
            if (_beacon.Visible != on)
                _beacon.Visible = on;
        }
    }
}
