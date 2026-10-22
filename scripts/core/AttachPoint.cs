using Godot;

namespace SpaceCorporation;

/// <summary>
/// Точка, к которой может быть пристёгнут игрок (Player.AttachTo).
///
/// Закладка под этап 6: игрок открепляется от корабля и прикрепляется к станции.
/// Для этого у корабля есть точка кабины (Seat), у станции позже появятся точки
/// пола (Floor) и шлюза (Hatch). Все они состоят в группе AttachPoint.GroupName,
/// поэтому код игрока ищет их по группе, а не по жёстким путям в сцене.
/// </summary>
public partial class AttachPoint : Node3D
{
    public enum KindOf
    {
        /// <summary>Кабина корабля / кресло.</summary>
        Seat,
        /// <summary>Пол станции или палубы (магнитные ботинки, этап 6).</summary>
        Floor,
        /// <summary>Шлюз: переход между кораблём и станцией.</summary>
        Hatch,
    }

    /// <summary>Группа всех точек крепления в сцене.</summary>
    public const string GroupName = "attach_point";

    [Export] public KindOf Kind { get; set; } = KindOf.Seat;

    /// <summary>К этой точке игрок пристёгнут при старте сцены.</summary>
    [Export] public bool IsDefaultForPlayer { get; set; }

    /// <summary>
    /// Смещение капсулы игрока относительно этой точки (в локальных осях точки).
    /// Для Kind.Seat — посадить капсулу в кресло; для Kind.Floor — поднять на рост
    /// над палубой, чтобы капсула не проваливалась в пол.
    /// </summary>
    [Export] public Vector3 PlayerOffset { get; set; } = Vector3.Zero;

    public override void _Ready() => AddToGroup(GroupName);
}
