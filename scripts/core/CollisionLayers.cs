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
    // Решение §2.8: нумерация существующих слоёв не меняется (она вшита в .tscn и
    // тесты), новые слои добавляются в конец.
    public const uint Debris      = 1u << 6; // 7 — обломки (этап 3+)
    public const uint World       = 1u << 7; // 8 — статика мира (планеты, астероиды)
    public const uint Deck        = 1u << 8; // 9 — палубы примитивов (этап 6, eva-plan Блок F)
    public const uint ControlPanel = 1u << 9; // 10 — панели управления (eva-plan Блок E)

    /// <summary>Лучевые каналы EVA (этап 6): чем интересуются лучи игрока
    /// (выбор кресла, наведение на панель в Блоке E). По eva-plan §3 (строка 179):
    /// панели + палубы + станция + корпуса кораблей. Слой Character не добавляем:
    /// лучи не цепляются за других игроков. При том что маска КОЛЛИЗИЙ игрока слоя
    /// `Ship` не содержит («летать можно сквозь корпуса, но лучи корпуса видят»,
    /// В24 + план 6.2) — разница масок коллизий и лучей намеренная.</summary>
    public const uint EvaRayMask = ControlPanel | Deck | Station | Ship;

    /// <summary>По чему ходят магнитные ботинки (eva-plan строка 178): палубы
    /// примитивов (в т.ч. палубы кораблей) и корпус станции. Корпус корабля сюда
    /// НЕ входит: по нему ходят по его палубе-примитиву (Блок F), а не по обшивке.</summary>
    public const uint BootsMask = Deck | Station;

    /// <summary>Случайное тело корабля: сталкивается со станцией и мишенями.</summary>
    public const uint ShipMask = Station | Target;

    /// <summary>Хитскан-луч (этап 3 + фикс урона по кораблям): станция + мишени +
    /// корпуса кораблей (стрельба по станции и чужому кораблю должна наносить
    /// урон; своё тело снимается через exclude в IntersectRay). Персонаж — нет.</summary>
    public const uint LaserMask = Station | Target | Ship;
}
