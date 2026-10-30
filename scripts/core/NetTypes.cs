using System;
using System.Runtime.CompilerServices;

namespace SpaceCorporation.Net;

/// <summary>
/// Позиция вне Godot-типов: кодек снапшотов (§2.1 плана net-refactor-plan.md)
/// обязан компилироваться и тестироваться БЕЗ движка, поэтому использует свои
/// векторы. Конвертация в `Godot.Vector3` — на стороне вызывающего кода (R3/R4).
/// Порядок компонент совпадает с Godot (`X, Y, Z`), поэтому конверсия тривиальна.
/// </summary>
public struct Vec3
{
    public float X, Y, Z;

    public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    /// <summary>Нулевой вектор (свойство, а не поле: struct не инициализируется статически).</summary>
    public static Vec3 Zero => default;

    public readonly float LengthSquared => X * X + Y * Y + Z * Z;

    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, float k) => new(a.X * k, a.Y * k, a.Z * k);
    public static Vec3 operator *(float k, Vec3 a) => new(a.X * k, a.Y * k, a.Z * k);

    public override readonly string ToString() => $"({X:F3}, {Y:F3}, {Z:F3})";
}

/// <summary>Кватернион в том же «чистом» виде (порядок полей как у Godot).</summary>
public struct Quat
{
    public float X, Y, Z, W;

    public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

    public static readonly Quat Identity = new(0, 0, 0, 1);

    public readonly float LengthSquared => X * X + Y * Y + Z * Z + W * W;

    public static Quat operator *(Quat a, float k) => new(a.X * k, a.Y * k, a.Z * k, a.W * k);
}

/// <summary>
/// Позиционное квантование (§2.1): позиция корабля уходит как int16-СМЕЩЕНИЕ от
/// якоря тира, а не как абсолютные координаты. Абсолютный int16 на поле 13 км дал
/// бы шаг 20 см — для интерполяции корпуса это уже видно.
/// </summary>
public static class Quant
{
    /// <summary>Шаг позиции в ближнем/среднем тире, м (1 см).</summary>
    public const float PosStepNear = 0.01f;
    /// <summary>Шаг позиции в дальнем тире, м (10 см — его не интерполируют).</summary>
    public const float PosStepFar = 0.10f;
    /// <summary>Шаг линейной скорости, м/с. Диапазон int10: ±153.6 м/с.</summary>
    public const float VelStep = 0.30f;
    /// <summary>Шаг угловой скорости, рад/с. Диапазон int8: ±6.35 рад/с.</summary>
    public const float AngStep = 0.05f;

    /// <summary>Максимальная дистанция от якоря, при которой int16 ещё не переполняется.</summary>
    public const float MaxOffsetNear = 327.67f;   // 32767 × 1 см
    public const float MaxOffsetFar = 3276.7f;    // 32767 × 10 см

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToCode(float value, float step) => (int)MathF.Round(value / step);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float FromCode(int code, float step) => code * step;

    /// <summary>Влезает ли смещение в int16 по всем трём осям.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FitsInt16(Vec3 offset, float step)
    {
        // Сравниваем НЕ расстояние с плавающим лимитом (32767*step из-за float
        // чуть меньше граничного 327.67 и отсекал бы ровно допустимый код), а
        // фактический округлённый код, который попадёт в запись. Так граница
        // MaxOffsetNear/MaxOffsetFar совпадает с реальным диапазоном int16.
        return MathF.Abs(MathF.Round(offset.X / step)) <= 32767f
            && MathF.Abs(MathF.Round(offset.Y / step)) <= 32767f
            && MathF.Abs(MathF.Round(offset.Z / step)) <= 32767f;
    }
}

/// <summary>
/// Снимок корабля ДО квантования (то, что сервер берёт из `NetState`/`FlightState`)
/// и ПОСЛЕ распаковки (то, что клиент кладёт в интерполяционный буфер). Один и тот
/// же тип с обеих сторон: кодек не различает «отправку» и «приём» по типу.
/// </summary>
public struct ShipSnapshotQ
{
    public ushort NetId;
    /// <summary>Физический шаг авторитета, на котором сделан снимок.</summary>
    public int SimTick;
    public Vec3 Position;
    public Quat Rotation;
    public Vec3 LinearVelocity;
    public Vec3 AngularVelocity;

    /// <summary>Тик кадра ввода, применённого авторитетом (только для пилота).</summary>
    public int AppliedTick;
    /// <summary>Маска того же кадра (только для пилота).</summary>
    public uint AppliedMask;
    /// <summary>Признак «это запись своего корабля»: добавляет поля реконсиляции.</summary>
    public bool ForPilot;
    /// <summary>Запись дальнего тира: без скоростей, грубее поворот, квант 10 см.</summary>
    public bool FarTier;
}
