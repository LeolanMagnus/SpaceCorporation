using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Битовая маска действий пилота (stage2-plan §1.3). Один кадр ввода = снимок
/// состояния клавиш на один физический шаг, а не событие «нажали/отпустили»:
/// сервер обязан знать и об отсутствии ввода, иначе отпущенная клавиша залипает.
///
/// Раскладка берётся из действий `project.godot [input]` (ship_forward и т.д.),
/// а не из захардкоженных `Key` — см. шаг 2.2 плана.
/// </summary>
public static class InputMask
{
    public const uint ThrustF = 1u << 0;   // W    — вперёд (локальная +Z? нет: -Z, см. Ship)
    public const uint ThrustB = 1u << 1;   // S    — назад
    public const uint ThrustL = 1u << 2;   // A    — влево
    public const uint ThrustR = 1u << 3;   // D    — вправо
    public const uint ThrustU = 1u << 4;   // Space— вверх (локальная +Y)
    public const uint ThrustD = 1u << 5;   // Ctrl — вниз  (локальная -Y)
    public const uint TorquePitchUp = 1u << 6;   // Z   — тангаж +
    public const uint TorquePitchDown = 1u << 7; // X   — тангаж -
    public const uint TorqueYawLeft = 1u << 8;   // Q   — рыскание -
    public const uint TorqueYawRight = 1u << 9;  // E   — рыскание +
    public const uint Boost = 1u << 10;          // Shift — форсаж (усиливает тягу)

    /// <summary>Все биты линейной тяги — для проверки «есть ли линейный ввод».</summary>
    public const uint LinearBits = ThrustF | ThrustB | ThrustL | ThrustR | ThrustU | ThrustD;

    /// <summary>Все биты момента — для проверки «есть ли вращательный ввод».</summary>
    public const uint AngularBits = TorquePitchUp | TorquePitchDown | TorqueYawLeft | TorqueYawRight;
}

/// <summary>
/// Один кадр пилотского ввода (stage2-plan §1.3). Сериализуется в фиксированный
/// байтовый блок, чтобы влезать в один unreliable-пакет канала ввода.
///
/// `Tick` — счётчик физических шагов КЛИЕНТА (не сервера): сервер сопоставляет
/// снимки состояния с вводом по этому номеру, а не по своему.
///
/// Оси (`Axis*`) — величина нажатия 0..1 (для клавиатуры = 1 при удержании).
/// `LookYaw`/`LookPitch` — смещение мыши за кадр в радианах: мышь даёт дельту,
/// а не положение, поэтому она не входит в маску.
///
/// Структура обязана оставаться детерминированной: никаких узлов и времени.
/// </summary>
public struct InputFrame
{
    public uint Tick;
    public uint Mask;

    // Оси по действиям: значение 0..1 (клавиатура) или -1..1 (геймпад later).
    public float AxisF, AxisB, AxisL, AxisR, AxisU, AxisD;
    public float AxisPitchUp, AxisPitchDown, AxisYawLeft, AxisYawRight;

    // Дельта мыши за кадр, радианы.
    public float LookYaw, LookPitch;

    /// <summary>
    /// Размер сериализованного кадра в байтах (см. тест п.3). В провод уходит
    /// ТОЛЬКО tick + mask + дельта мыши: оси восстанавливаются из маски
    /// (`FromMask`), потому что раскладка клавиатурная и аналоговых значений нет.
    /// Это 16 байт — меньше лимита 32 байт на один unreliable-пакет канала ввода.
    /// Если появится геймпад (аналог 0..1), формат расширяется отдельным тегом,
    /// а не молча: старые клиенты должны уметь читать новые кадры.
    /// </summary>
    public const int Size = 4 + 4 + 4 + 4; // tick + mask + lookYaw + lookPitch

    /// <summary>
    /// Собрать кадр из маски и её же значений: удержанное действие даёт ось 1.
    /// Отдельная функция нужна потому, что сервер получает только маску+оси и
    /// обязан восстановить ровно тот же кадр, что отправил клиент.
    /// </summary>
    public static InputFrame FromMask(uint tick, uint mask, float yaw = 0f, float pitch = 0f)
    {
        InputFrame f = new() { Tick = tick, Mask = mask, LookYaw = yaw, LookPitch = pitch };
        if ((mask & InputMask.ThrustF) != 0) f.AxisF = 1f;
        if ((mask & InputMask.ThrustB) != 0) f.AxisB = 1f;
        if ((mask & InputMask.ThrustL) != 0) f.AxisL = 1f;
        if ((mask & InputMask.ThrustR) != 0) f.AxisR = 1f;
        if ((mask & InputMask.ThrustU) != 0) f.AxisU = 1f;
        if ((mask & InputMask.ThrustD) != 0) f.AxisD = 1f;
        if ((mask & InputMask.TorquePitchUp) != 0) f.AxisPitchUp = 1f;
        if ((mask & InputMask.TorquePitchDown) != 0) f.AxisPitchDown = 1f;
        if ((mask & InputMask.TorqueYawLeft) != 0) f.AxisYawLeft = 1f;
        if ((mask & InputMask.TorqueYawRight) != 0) f.AxisYawRight = 1f;
        return f;
    }

    /// <summary>
    /// Склеить два кадра в один (для теста п.3 и для «пропущенного» кадра сервера).
    /// Оси складываются и ограничиваются единицей, маска — объединение битов.
    /// </summary>
    public static InputFrame Merge(InputFrame a, InputFrame b)
    {
        InputFrame m = new()
        {
            Tick = (a.Tick > b.Tick) ? a.Tick : b.Tick,
            Mask = a.Mask | b.Mask,
            AxisF = Clamp1(a.AxisF + b.AxisF),
            AxisB = Clamp1(a.AxisB + b.AxisB),
            AxisL = Clamp1(a.AxisL + b.AxisL),
            AxisR = Clamp1(a.AxisR + b.AxisR),
            AxisU = Clamp1(a.AxisU + b.AxisU),
            AxisD = Clamp1(a.AxisD + b.AxisD),
            AxisPitchUp = Clamp1(a.AxisPitchUp + b.AxisPitchUp),
            AxisPitchDown = Clamp1(a.AxisPitchDown + b.AxisPitchDown),
            AxisYawLeft = Clamp1(a.AxisYawLeft + b.AxisYawLeft),
            AxisYawRight = Clamp1(a.AxisYawRight + b.AxisYawRight),
            LookYaw = a.LookYaw + b.LookYaw,
            LookPitch = a.LookPitch + b.LookPitch,
        };
        return m;
    }

    private static float Clamp1(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

    /// <summary>
    /// Сериализация в фиксированный блок (тест п.3): tick + mask + дельта мыши.
    /// Оси сюда не входят — их восстанавливает `FromMask`, см. комментарий к `Size`.
    /// Порядок байт не важен: и отправитель, и получатель — одна и та же сборка,
    /// а побитовая идентичность нужна только для round-trip-assert'а.
    /// </summary>
    public byte[] ToBytes()
    {
        byte[] b = new byte[Size];
        int i = 0;
        WriteU32(b, ref i, Tick);
        WriteU32(b, ref i, Mask);
        WriteF(b, ref i, LookYaw);
        WriteF(b, ref i, LookPitch);
        return b;
    }

    /// <summary>
    /// Обратная сериализация: кадр восстанавливается полностью, включая оси
    /// (через `FromMask`) — иначе сервер проинтегрирует маску без значений тяги.
    /// </summary>
    public static InputFrame FromBytes(byte[] b)
    {
        int i = 0;
        uint tick = ReadU32(b, ref i);
        uint mask = ReadU32(b, ref i);
        float yaw = ReadF(b, ref i);
        float pitch = ReadF(b, ref i);
        return FromMask(tick, mask, yaw, pitch);
    }

    private static void WriteU32(byte[] b, ref int i, uint v)
    {
        b[i++] = (byte)(v & 0xFF);
        b[i++] = (byte)((v >> 8) & 0xFF);
        b[i++] = (byte)((v >> 16) & 0xFF);
        b[i++] = (byte)((v >> 24) & 0xFF);
    }

    private static uint ReadU32(byte[] b, ref int i)
    {
        uint v = (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
        i += 4;
        return v;
    }

    private static void WriteF(byte[] b, ref int i, float v)
    {
        byte[] raw = BitConverter.GetBytes(v);
        for (int k = 0; k < 4; k++) b[i + k] = raw[k];
        i += 4;
    }

    private static float ReadF(byte[] b, ref int i)
    {
        float v = BitConverter.ToSingle(b, i);
        i += 4;
        return v;
    }
}
