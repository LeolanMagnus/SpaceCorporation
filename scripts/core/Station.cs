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

    public override void _Ready()
    {
        // Станция спавнится сервером в начале мира (шаг 4.3); никакой логики в
        // этой стадии у неё нет — только существование, коллизия и адрес.
    }
}
