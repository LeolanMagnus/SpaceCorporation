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
    /// <summary>Фракция ((Faction)FactionValue): 0 Neutral / 1 Player / 2 Hostile.</summary>
    public byte Faction;
    /// <summary>Форма маркера (<see cref="RadarShape"/>).</summary>
    public byte Shape;
    /// <summary>Контакт выбран пилотом (TargetingService) — рамка на панели.</summary>
    public bool IsTarget;
    /// <summary>Подбит (Hp &lt; MaxHp) — блип мигает (R2, источник ShipHealthState).</summary>
    public bool Damaged;
    /// <summary>Дистанция до контакта, м (заполняет RadarModel).</summary>
    public float Distance;
}
