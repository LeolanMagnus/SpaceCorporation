using SpaceCorporation.Net;

namespace SpaceCorporation.UI;

/// <summary>
/// Тип маркера на радаре — форма по классу объекта (radar-plan.md §2.1).
/// Байт, чтобы контакт оставался «чистым» (без зависимостей от Godot).
/// </summary>
public static class RadarShape
{
    /// <summary>Точка — обычный корабль (истребитель/фрегат).</summary>
    public const byte Ship = 0;
    /// <summary>Ромб — мишень/цель-тренажёр (Target.tscn).</summary>
    public const byte Target = 1;
    /// <summary>Квадрат — станция.</summary>
    public const byte Station = 2;
}

/// <summary>
/// Контакт радара (radar-plan.md §2.1): состояние контакта целиком из
/// реплицированных полей (NetState/StationState), собранных RadarSource.
/// Чистая структура без Godot — RadarModel тестируется движком не требуется.
/// </summary>
public struct RadarContact
{
    /// <summary>Сетевой адрес объекта (ShipNetId/StationNetId из единой нумерации).</summary>
    public int NetId;
    /// <summary>Позиция относительно своего корабля (мир, до поворота на курс).</summary>
    public Vec3 RelPos;
    /// <summary>Относительная скорость (для векторов скорости на панели).</summary>
    public Vec3 RelVel;
    /// <summary>Абсолютная мировая скорость контакта на момент сборки
    /// (Vel at sync). Целевой контракт для экстраполяции оверлея:
    /// world = SyncCenter + RelPos + AbsVel·age. Статичные сущности
    /// (станция, мишень, астероид, контейнер в покое) → Zero; движущиеся
    /// (курьер, обломок по инерции) → их реальная скорость. Оверлей НЕ
    /// использует скорость своего корабля, поэтому выделение не дёргается
    /// при манёврах наблюдающего — верно для любых будущих типов.</summary>
    public Vec3 AbsVel;
    /// <summary>Фракция ((Faction)FactionValue): 0 Neutral / 1 Player / 2 Hostile.</summary>
    public byte Faction;
    /// <summary>Форма маркера (<see cref="RadarShape"/>).</summary>
    public byte Shape;
    /// <summary>Контакт выбран пилотом (TargetingService) — рамка на панели.</summary>
    public bool IsTarget;
    /// <summary>Подбит (Hp &lt; MaxHp) — блип мигает (R2, источник ShipHealthState).</summary>
    public bool Damaged;
    /// <summary>Корабль в фазе захвата (Net.CaptureParentNetId ≠ 0) — рамка-«⊂»
    /// у блипа (frame-plan §1 шаг 9: НЕ новый тип метки, существующий блип).</summary>
    public bool Captured;
    /// <summary>Дистанция до контакта, м (заполняет RadarModel).</summary>
    public float Distance;

    // ── lock-оверлей (targeting-plan): метка типа и угловой размер цели ──
    // Заполняет RadarSource; RadarModel их не трогает — это данные для
    // экранного оверлея TargetOverlay, живущего на тех же контактах.

    /// <summary>Байт типа корабля ((ShipType)значение, реплика ShipTypeValue).
    /// У мишеней/станций не используется — форму метки задаёт <see cref="Shape"/>.</summary>
    public byte ShipTypeValue;
    /// <summary>Габаритный радиус объекта, м (половина диагонали корпуса; для
    /// мишени/станции — условный «экранный» размер).TargetOverlay считает из него
    /// полуразмер рамки-выделения: r = focal·SizeRadius/дистанция.</summary>
    public float SizeRadius;
}
