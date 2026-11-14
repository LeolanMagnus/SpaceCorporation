using Godot;

namespace SpaceCorporation;

/// <summary>
/// Типы оружия прототипа (этап 6, фикс стрельбы). Порядок фиксирован:
/// byte-значение enum в будущем уйдёт в реплику (как у ShipType),
/// поэтому перечислять существующие элементы нельзя — только дописывать в конец.
/// 0 = LaserTurret — значение по умолчанию (единственный тип на сегодня).
/// </summary>
public enum WeaponType : byte
{
    /// <summary>Лазерная турель: хитскан, дальность 400 м, урон щиту 10,
    /// урон корпусу 20, энергия 10 на выстрел, 2 выстрела в секунду.</summary>
    LaserTurret = 0,
}

/// <summary>
/// Характеристики ОДНОГО типа оружия. Чистые данные, узлов не держат —
/// по образцу <see cref="ShipTypeSpec"/>: дизайнер приходит за числами сюда.
///
/// СМЫСЛ пары «урон щиту / урон корпусу» (фикс стрельбы, этап 6):
///  • пока у цели щит &gt; 0, турель бьёт ПО ЩИТУ по ставке ShieldDamage —
///    корпус в этом выстреле не страдает (турель «перестраивается» со выстрела
///    на выстрел, пробития насквозь нет);
///  • если щита уже нет (0), выстрел уходит по корпусу по ставке HullDamage.
/// Мишени-болванки (TargetState, щита нет) получают HullDamage.
/// </summary>
public sealed class WeaponSpec
{
    public readonly WeaponType Type;
    /// <summary>Человекочитаемое имя (HUD, дебаг-строка).</summary>
    public readonly string DisplayName;

    /// <summary>Настолько ли оружие мгновенное (хитскан). Иное (проективы)
    /// появится, когда такие типы заведутся.</summary>
    public readonly bool IsHitscan;

    /// <summary>Дальность огня (м): и хитскан-луч сервера, и дальность точки
    /// прицеливания на HUD считаются отсюда.</summary>
    public readonly float RangeM;

    /// <summary>Урон по щиту за выстрел (щит &gt; 0 — страдает только щит).</summary>
    public readonly int ShieldDamage;

    /// <summary>Урон по корпусу (HP) за выстрел, когда щита нет; он же урон
    /// по мишеням-болванкам без щита.</summary>
    public readonly int HullDamage;

    /// <summary>Сколько энергии стоит один выстрел — снимается с энергобуфера
    /// корабля (<see cref="ShipHealthState.Energy"/>) и пополняется
    /// реактором. Выстрел с недобором энергии не производится.</summary>
    public readonly int EnergyCost;

    /// <summary>Скорострельность, выстрелов в секунду (режим автоматической
    /// стрельбы: зажатая ЛКМ стреляет ровно с этим темпом).</summary>
    public readonly float FireRatePerSec;

    /// <summary>Пауза между выстрелами (сек) = 1 / FireRatePerSec — именно она
    /// уходит в <c>Ship.FireCooldownSec</c> (гейт В17 п.2 и автоскорость).</summary>
    public float FireIntervalSec => 1f / FireRatePerSec;

    public WeaponSpec(WeaponType type, string displayName, bool isHitscan,
                      float rangeM, int shieldDamage, int hullDamage,
                      int energyCost, float fireRatePerSec)
    {
        Type = type;
        DisplayName = displayName;
        IsHitscan = isHitscan;
        RangeM = rangeM;
        ShieldDamage = shieldDamage;
        HullDamage = hullDamage;
        EnergyCost = energyCost;
        FireRatePerSec = fireRatePerSec;
    }
}

/// <summary>
/// Общий каталог оружия (статический, по образцу <see cref="ShipStatCatalog"/>).
/// Все расчёты боя (урон, кулдаун, дальность, энергопотребление) ведутся по
/// записям отсюда — константы в <c>Ship.Combat.cs</c> только применяются из
/// каталога при спавне корабля.
/// </summary>
public static class WeaponCatalog
{
    public const WeaponType DefaultWeapon = WeaponType.LaserTurret;

    /// <summary>Запись оружия по умолчанию.</summary>
    public static WeaponSpec DefaultWeaponSpec => Find(DefaultWeapon);

    /// <summary>Все записи каталога в порядке enum. Порядок = будущий байт
    /// реплики, переставлять элементы нельзя.</summary>
    public static WeaponSpec[] Types => Specs;

    private static readonly WeaponSpec[] Specs =
    {
        // Лазерная турель по ТЗ этапа 6: хитскан, 400 м, щит 10 / корпус 20,
        // энергия 10 на выстрел, 2 выстрела в секунду (кулдаун 0.5 с).
        new WeaponSpec(
            WeaponType.LaserTurret, "Laser Turret (лазерная турель)",
            isHitscan: true,
            rangeM: 400f,
            shieldDamage: 10,
            hullDamage: 20,
            energyCost: 10,
            fireRatePerSec: 2f),
    };

    /// <summary>Запись по типу; неизвестный байт (чужая версия каталога) —
    /// молча возвращает дефолт, чтобы кривая реплика не роняла бои.</summary>
    public static WeaponSpec Find(WeaponType type)
    {
        foreach (var s in Specs)
            if (s.Type == type)
                return s;
        GD.PushWarning($"WeaponCatalog: тип {type} не найден — применяется дефолт " +
                       $"{DefaultWeapon} (версии каталога разошлись?)");
        return Find(DefaultWeapon);
    }

    /// <summary>Безопасный разбор байта из реплики.</summary>
    public static WeaponSpec FindByByte(byte typeValue)
        => Find((WeaponType)typeValue);
}
