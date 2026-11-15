using Godot;

namespace SpaceCorporation;

/// <summary>
/// Чистая математика «углов наводки» орудия (этап «наводка», docs/weapon-aim-plan.md).
///
/// Ствол закреплён на корпусе и может отклониться от его оси (нос = -Z) не более
/// чем на <c>maxAngle</c> (параметр оружия «углы наводки», <see cref="WeaponSpec.AimLimitRad"/>).
/// Если направление на цель выходит за конус — направление «схлопывается» на
/// ГРАНИЦУ конуса, оставаясь в плоскости «ось корабля → цель». Именно так
/// прицельная точка уходит на край возможного угла поворота, не сходя с линии
/// «центр экрана → курсор» в проекции камеры.
///
/// Класс статический и без состояния — как <see cref="PilotAim"/>: кроется
/// юнит-проверками без дерева сцены.
/// </summary>
public static class WeaponAim
{
    /// <summary>Ось ствола по умолчанию в локальных координатах турели: нос = -Z.</summary>
    public static readonly Vector3 BarrelAxis = new Vector3(0f, 0f, -1f);

    /// <summary>
    /// Ограничить вектор <paramref name="dir"/> конусом с осью <paramref name="axis"/>
    /// и полууглом <paramref name="maxAngle"/> (радианы, [0..π]).
    ///  • maxAngle ≤ 0 или dir уже внутри — dir без изменений;
    ///  • dir коллинеарен axis (±) — поворот не определён: возвращаем axis
    ///    (противоположный dir — тоже axis: цель «за спиной», стреляем вдоль оси);
    ///  • иначе — dir, повёрнутый К axis ровно на maxAngle в плоскости (axis, dir).
    /// Вход не обязан быть единичным — нормализуем; нулевой dir возвращаем как есть.
    /// </summary>
    public static Vector3 ClampToCone(Vector3 axis, Vector3 dir, float maxAngle)
    {
        if (dir.LengthSquared() < 1e-12f)
            return dir;
        Vector3 a = axis.LengthSquared() < 1e-12f ? BarrelAxis : axis.Normalized();
        Vector3 d = dir.Normalized();

        if (maxAngle <= 0f)
            return a; // ствол зафиксирован вдоль оси (нулевой сектор наводки)

        float angle = a.AngleTo(d);
        if (angle <= maxAngle)
            return d;

        // Поворот на (angle − maxAngle) К оси вокруг нормали плоскости (a, d).
        Vector3 rotAxis = a.Cross(d);
        if (rotAxis.LengthSquared() < 1e-12f)
            return a; // dir строго по оси или против — плоскость не определена
        rotAxis = rotAxis.Normalized();
        // В этой плоскости n = a×d: поворот a вокруг n на +angle приводит a к d
        // (проверка: скорость вращения a = n×a ∝ d − cosθ·a, то есть «в сторону d»).
        // Значит d нужно вращать на −(angle − maxAngle), чтобы ПРИВЕСТИ его к оси.
        float delta = maxAngle - angle; // < 0: доворот d к a до границы конуса
        return d.Rotated(rotAxis, delta);
    }

    /// <summary>Тот же clamp, но стартуем с ЛОКАЛЬНОГО направления прицела в
    /// координатах турели (ось ствола — <see cref="BarrelAxis"/>).</summary>
    public static Vector3 ClampLocalToBarrel(Vector3 localDir, float maxAngle)
        => ClampToCone(BarrelAxis, localDir, maxAngle);
}
