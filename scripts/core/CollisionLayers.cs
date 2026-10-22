namespace SpaceCorporation;

/// <summary>
/// Именованные слои физики 3D. Номера совпадают с project.godot -> [layer_names] 3d_physics/*.
/// Использовать константы вместо magic-чисел в collision_layer / collision_mask.
/// </summary>
public static class CollisionLayers
{
    public const uint Ship        = 1u << 0; // 1
    public const uint Station     = 1u << 1; // 2
    public const uint Character   = 1u << 2; // 3
    public const uint Target      = 1u << 3; // 4
    public const uint DockingZone = 1u << 4; // 5
    public const uint DebugHelper = 1u << 5; // 6

    /// <summary>Случайное тело корабля: сталкивается со станцией и мишенями.</summary>
    public const uint ShipMask = Station | Target;

    /// <summary>Луч магнитных ботинок (этап 6): ищет только станцию.</summary>
    public const uint BootsMask = Station;

    /// <summary>Хитскан-луч (этап 3): станция + мишени, без корабля и персонажа.</summary>
    public const uint LaserMask = Station | Target;
}
