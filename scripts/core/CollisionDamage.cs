using Godot;

namespace SpaceCorporation;

/// <summary>
/// Чистая функция урона от столкновения с станцией (Этап 4, шаг 4.6, В19 п.5).
/// Никаких узлов и сцен — только числа, поэтому все критерии этапа («на
/// одинаковой скорости одинаковый урон», «медленное касание — 0») проверяются
/// без физики, а серверный интегратор (шаг 4.7) вызывает ровно эти функции.
///
/// Формула (stage4-plan §1):
///   vn — нормальная составляющая скорости вдоль нормали станции
///        (сближение < 0 по конвенции нормалей contact-проб, но функция
///        симметрична по знаку — см. п.16);
///   |vn| <= Threshold → урон 0 (мягкое касание);
///   иначе damage = clamp((|vn| - Threshold) * DamagePerMps, 0, maxHp).
/// Кламп именно в maxHp, а не «976»: смерть корабля — отдельная ветка (шаг 4.8),
/// отрицательного HP формула не производит.
///
/// Конвенция для ApplyBounce: vn считается СКОРОСТЬЮ СБЛИЖЕНИЯ, т.е. если
/// корабль уже расходится (vn по знаку «от станции»), скорость НЕ меняется —
/// иначе станция «тянет» корабль на себя (п.16).
/// </summary>
public static class CollisionDamage
{
    /// <summary>Ниже этой нормальной скорости урона нет (мягкое касание), м/с.</summary>
    public const float Threshold = 12f;

    /// <summary>Единиц урона на 1 м/с нормальной скорости сверх порога
    /// (→ 62 м/с = 100 урона при maxHp = 100, дальше кламп).</summary>
    public const float DamagePerMps = 2f;

    /// <summary>Доля нормальной скорости, возвращаемая кораблю при отскоке
    /// (0 = гаснет полностью, 1 = абсолютно упругий).</summary>
    public const float BounceRestitution = 0.3f;

    /// <summary>Урон от нормального сближения со станцией. god == true → 0 при
    /// любой скорости (шаг 4.9). maxHp <= 0 → 0 (нечего наносить мёртвому).</summary>
    public static int ComputeDamage(int maxHp, bool god, float deltaNormal)
        => ComputeDamage(maxHp, god, deltaNormal, Threshold, DamagePerMps);

    /// <summary>То же с явными константами — чтобы тест мог проверять граничные
    /// значения без правки констант класса.</summary>
    public static int ComputeDamage(int maxHp, bool god, float deltaNormal,
                                    float threshold, float damagePerMps)
    {
        if (god || maxHp <= 0)
            return 0;
        float v = Mathf.Abs(deltaNormal);
        if (v <= threshold)
            return 0;
        float raw = (v - threshold) * damagePerMps;
        // Кламп в maxHp (п.14): 500 м/с дают 100, а не 976.
        return (int)Mathf.Min(raw, maxHp);
    }

    /// <summary>Отскок от станции: гасит нормальную составляющую и возвращает
    /// долю restitution. `n` — ВНЕШНЯЯ нормаль станции (из станции в корабль),
    /// `vn` — скорость сближения вдоль неё (т.е. <see cref="ApproachSpeed"/>,
    /// > 0 когда корабль летит в станцию). Если `vn <= 0` (расходимся) — скорость
    /// не меняется: отскок не должен тянуть корабль обратно к станции (п.16).
    ///
    /// Формула: v' = v + (1 + restitution) * vn * n.
    /// Проверка знаком: корабль v = (0,0,40) летит на станцию при z = +200,
    /// внешняя нормаль в сторону корабля n = (0,0,-1), vn = 40 →
    /// v' = (0,0,40) + 1.3*40*(0,0,-1) = (0,0,-12) — разворот наружу. </summary>
    public static Vector3 ApplyBounce(Vector3 v, Vector3 n, float vn, float restitution)
    {
        if (vn <= 0f)
            return v;
        return v + (1f + restitution) * vn * n;
    }

    /// <summary>Компонента скорости вдоль нормали сближения: положительно, когда
    /// корабль летит В станцию (скорость направлена против внешней нормали n).
    /// Разделяем явно, чтобы интегратор (шаг 4.7) и тест считали знак одинаково.</summary>
    public static float ApproachSpeed(Vector3 v, Vector3 outwardNormal)
        => -v.Dot(outwardNormal);
}
