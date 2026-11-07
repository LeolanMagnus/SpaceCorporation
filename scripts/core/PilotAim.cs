using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Чистая математика доворота носа «на курсор мыши» (режим пилотирования 2.0).
///
/// Модель: центр экрана — это точка, куда сейчас смотрит нос. Прицелом служит
/// свободный курсор мыши. Корабль доворачивает НОС в сторону курсора со скоростью,
/// пропорциональной удалению курсора от центра:
///
///   • внутри «слепой зоны» радиусом <see cref="DeadzoneRadiusPx"/> пикселей — нос
///     НЕ крутится вовсе (фактор 0): микродрожание руки не дёргает корабль;
///   • на окружности радиусом <see cref="RadiusFraction"/> × высоты экрана — скорость
///     доворота максимальна (фактор 1);
///   • между ними фактор линейно растёт от 0 до 1.
///
/// Класс статический и без состояния — так же, как <see cref="FlightModel"/>: его
/// можно крыть юнит-тестом без дерева сцены и без мыши. Визуал (пунктирная линия
/// и галочки) рисует <c>AimReticle</c>, пользуясь теми же константами.
/// </summary>
public static class PilotAim
{
    /// <summary>Радиус «слепой зоны» в пикселях: внутри нос не крутится.</summary>
    public const float DeadzoneRadiusPx = 40f;

    /// <summary>Радиус полной скорости как доля ВЕРТИКАЛЬНОГО размера экрана.
    /// При 0.25 курсор на дистанции ¼ высоты окна даёт 100% скорости доворота.</summary>
    public const float RadiusFraction = 0.25f;

    /// <summary>Радиус окружности полной скорости по высоте окна.</summary>
    public static float FullSpeedRadiusPx(float viewportHeight)
        => viewportHeight * RadiusFraction;

    /// <summary>Экранная точка, куда смотрит нос (центр окна).</summary>
    public static Vector2 ScreenCenter(Vector2 viewportSize)
        => viewportSize * 0.5f;

    /// <summary>
    /// Направление-фактор доворота: единичный вектор «центр → курсор», умноженный
    /// на фактор 0..1 от удаления. Вне слепой зоны фактор =
    /// (<c>r</c> − deadzone) / (полный радиус − deadzone), обрезанный [0, 1].
    ///
    /// Граничные случаи (возвращают <see cref="Vector2.Zero"/>):
    ///   • курсор точно в центре (r = 0) — нечем взять направление;
    ///   • курсор в слепой зоне (r ≤ dead) — не крутим;
    ///   • вырожденная геометрия (полный радиус ≤ dead, напр. крошечное headless-
    ///     окно 64×64: полный радиус 16 &lt; deadzone 100) — зона не достижима,
    ///     крутим только строго за её границей на полную скорость (см. ниже),
    ///     иначе в микроскопическом окне любое движение было бы нелинейным шумом.
    /// </summary>
    public static Vector2 TurnFactor(Vector2 cursor, Vector2 center, float fullSpeedRadius)
        => TurnFactor(cursor, center, DeadzoneRadiusPx, fullSpeedRadius);

    /// <summary>То же, но с явным радиусом слепой зоны — для тестов, где зону и
    /// полный радиус подгоняют под размер окна (headless-окно 64×64 не вмещает
    /// боевые 100 пикселей).</summary>
    public static Vector2 TurnFactor(Vector2 cursor, Vector2 center,
        float deadzone, float fullSpeedRadius)
    {
        Vector2 off = cursor - center;
        float r = off.Length();
        if (r <= 0f)
            return Vector2.Zero;          // направление неопределено
        if (r <= deadzone)
            return Vector2.Zero;          // слепая зона: нос не крутится

        Vector2 dir = off / r;            // экранная ось Y смотрит ВНИЗ — как и
                                          // old look-канал (mouse down = pitch +).

        // Вырожденная геометрия: полный радиус не больше слепой зоны — линейной
        // ramp нет места. За границей слепой зоны сразу полная скорость.
        if (fullSpeedRadius <= deadzone)
            return dir;

        float t = (r - deadzone) / (fullSpeedRadius - deadzone);
        return dir * Mathf.Clamp(t, 0f, 1f);
    }

    /// <summary>
    /// Команда доворота в радианах за ОДИН физический шаг: направление-фактор
    /// (0..1), умноженный на полную угловую скорость и длительность шага. Это
    /// прямой доворот носа (как old mouse-look), а не момент, поэтому ложится в
    /// тот же look-канал <see cref="InputFrame.LookYaw"/>/<see cref="InputFrame.LookPitch"/>.
    ///
    /// <paramref name="fullSpeedRadPerSec"/> — потолок (<see cref="FlightParams.MaxAngularSpeed"/>),
    /// 0 = без ограничения — тогда фактора-потолка нет, и величина определяется
    /// только удалением курсора (фактором 0..1 × <paramref name="fullSpeedRadPerSec"/>).
    /// </summary>
    public static Vector2 LookRadiansPerStep(Vector2 cursor, Vector2 center,
        float fullSpeedRadius, float fullSpeedRadPerSec, float dt)
    {
        Vector2 factor = TurnFactor(cursor, center, fullSpeedRadius);
        // factor уже в [0,1]; полная скорость = fullSpeedRadPerSec.
        return factor * fullSpeedRadPerSec * dt;
    }
}
