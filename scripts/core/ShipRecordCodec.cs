using System;

namespace SpaceCorporation.Net;

/// <summary>
/// Кодек записей батча снапшотов (§2.1/§2.2 плана net-refactor-plan.md).
///
/// Запись фиксированной длины и фиксированных смещений: распаковка не должна
/// парсить поля, чтобы идти по 400 кораблям за микросекунды и без аллокаций.
/// Позиция — int16-СМЕЩЕНИЕ от якоря тира (см. `Quant`), поворот — канонический
/// кватернион с пропуском наибольшей компоненты (2 бита индекса + 3 × int10),
/// скорости — int10/int8.
///
/// Класс не зависит от Godot: его тесты гоняются вне движка (`dotnet run`).
/// </summary>
public static class ShipRecordCodec
{
    /// <summary>Запись наблюдателю (near/mid): netId 2 + дельта тика 2 + позиция 6
    /// + поворот 4 + линейная скорость 4 + угловая 3 = 21 Б.</summary>
    public const int FullRecordSize = 21;
    /// <summary>Запись пилоту: + дельта применённого тика 2 + маска 2 = 25 Б.</summary>
    public const int PilotRecordSize = 25;
    /// <summary>Запись дальнего тира: netId 2 + дельта тика 2 + позиция 6 + грубый
    /// поворот 4 = 14 Б, без скоростей.</summary>
    public const int FarRecordSize = 14;
    /// <summary>Заголовок батча: базовый тик 4 + число записей 2 + флаги/шаг 2 = 8 Б.</summary>
    public const int HeaderSize = 8;

    public static int RecordSize(bool forPilot, bool farTier)
        => farTier ? FarRecordSize : (forPilot ? PilotRecordSize : FullRecordSize);

    // ------------------------------------------------ заголовок батча (§2.2)
    // 8 Б: базовый SimTick (4) + число записей (2) + флаги (1) + шаг позиции (1).
    // Шаг позиции передаётся множителем от 1 см (1 → 1 см, 10 → 10 см), чтобы
    // клиент не угадывал квант по типу записи, а читал его из пакета.

    /// <summary>Бит флага: в батче есть запись своего корабля (пилотская).</summary>
    public const byte FlagHasPilotRecord = 1;
    /// <summary>Бит флага: все записи в батче дальнего тира (без скоростей).</summary>
    public const byte FlagAllFarTier = 2;
    /// <summary>Бит флага: после заголовка идут 12 Б якоря этого пакета
    /// (3 × float32 LE). Без флага клиент использует якорь, полученный ранее
    /// для того же тира — сервер пересылает якорь только при его смене (§2.1).</summary>
    public const byte FlagHasAnchor = 4;
    /// <summary>Размер якоря в заголовке с флагом FlagHasAnchor.</summary>
    public const int AnchorBytes = 12;

    public static void WriteHeader(System.Span<byte> dst, int baseTick, int recordCount,
                                   byte flags, float posStep)
    {
        dst[0] = (byte)baseTick;
        dst[1] = (byte)(baseTick >> 8);
        dst[2] = (byte)(baseTick >> 16);
        dst[3] = (byte)(baseTick >> 24);
        dst[4] = (byte)recordCount;
        dst[5] = (byte)(recordCount >> 8);
        dst[6] = flags;
        dst[7] = (byte)MathF.Round(posStep / Quant.PosStepNear);   // множитель шага
    }

    /// <summary>Записать заголовок и (если флаг установлен) якорь. Возвращает
    /// суммарный размер заголовка: 8 Б или 8+12 Б с якорем.</summary>
    public static int WriteHeaderWithAnchor(System.Span<byte> dst, int baseTick, int recordCount,
                                            byte flags, float posStep, Vec3 anchor)
    {
        WriteHeader(dst, baseTick, recordCount, (byte)(flags | FlagHasAnchor), posStep);
        WriteF32(dst, HeaderSize + 0, anchor.X);
        WriteF32(dst, HeaderSize + 4, anchor.Y);
        WriteF32(dst, HeaderSize + 8, anchor.Z);
        return HeaderSize + AnchorBytes;
    }

    public static void ReadHeader(System.ReadOnlySpan<byte> src, out int baseTick,
                                  out int recordCount, out byte flags, out float posStep)
    {
        baseTick = src[0] | (src[1] << 8) | (src[2] << 16) | (src[3] << 24);
        recordCount = src[4] | (src[5] << 8);
        flags = src[6];
        int mult = src[7] == 0 ? 1 : src[7];
        posStep = mult * Quant.PosStepNear;
    }

    /// <summary>Прочитать заголовок; если стоит FlagHasAnchor — вернуть якорь
    /// пакета и смещение начала записей (8+12), иначе смещение 8 и `anchor` без изменений.</summary>
    public static int ReadHeaderWithAnchor(System.ReadOnlySpan<byte> src, out int baseTick,
                                           out int recordCount, out byte flags, out float posStep,
                                           ref Vec3 anchor)
    {
        ReadHeader(src, out baseTick, out recordCount, out flags, out posStep);
        if ((flags & FlagHasAnchor) == 0) return HeaderSize;
        anchor = new Vec3(ReadF32(src, HeaderSize + 0), ReadF32(src, HeaderSize + 4),
                          ReadF32(src, HeaderSize + 8));
        return HeaderSize + AnchorBytes;
    }

    private static void WriteF32(System.Span<byte> d, int o, float v)
    {
        System.Runtime.InteropServices.MemoryMarshal.Write(d.Slice(o, 4), ref v);
    }

    private static float ReadF32(System.ReadOnlySpan<byte> d, int o)
    {
        return System.Runtime.InteropServices.MemoryMarshal.Read<float>(d.Slice(o, 4));
    }

    /// <summary>Сколько записей влезает в один MTU-пакет при данном транспорте
    /// (ENet 4 + UDP/IP 28 + путь RPC 12 + метод 2 + заголовок батча 8).</summary>
    public static int RecordsPerMtu(int recordSize, int mtuBytes = 1400,
                                    int transportOverhead = 4 + 28 + 12 + 2)
        => (mtuBytes - transportOverhead - HeaderSize) / recordSize;

    // Смещения полей полной записи.
    private const int OffNetId = 0;
    private const int OffTick = 2;
    private const int OffPos = 4;
    private const int OffRot = 10;
    private const int OffVel = 14;
    private const int OffAng = 18;
    private const int OffAppliedTick = 21;
    private const int OffAppliedMask = 23;

    /// <summary>Записать снимок в `dst` (длина ≥ `RecordSize`). Возвращает число
    /// записанных байт или -1, если позиция не влезает в int16-смещение при
    /// выбранном шаге — вызывающий обязан перевыбрать якорь/шаг (§2.1) и
    /// повторить, а не писать обрезок.</summary>
    public static int Write(System.Span<byte> dst, in ShipSnapshotQ s, Vec3 anchor,
                            int baseTick, float posStep)
    {
        int size = RecordSize(s.ForPilot, s.FarTier);
        if (dst.Length < size) return -1;

        Vec3 off = s.Position - anchor;
        if (!Quant.FitsInt16(off, posStep)) return -1;

        dst[OffNetId] = (byte)s.NetId;
        dst[OffNetId + 1] = (byte)(s.NetId >> 8);

        int tickDelta = s.SimTick - baseTick;      // знакомая дельта (низ 16 бит)
        dst[OffTick] = (byte)tickDelta;
        dst[OffTick + 1] = (byte)(tickDelta >> 8);

        WriteI16(dst, OffPos + 0, Quant.ToCode(off.X, posStep));
        WriteI16(dst, OffPos + 2, Quant.ToCode(off.Y, posStep));
        WriteI16(dst, OffPos + 4, Quant.ToCode(off.Z, posStep));

        if (s.FarTier)
        {
            WriteRotCoarse(dst, OffRot, s.Rotation);
            return FarRecordSize;
        }

        WriteRot(dst, OffRot, s.Rotation);
        WriteVel(dst, OffVel, s.LinearVelocity, Quant.VelStep);
        WriteAng(dst, OffAng, s.AngularVelocity, Quant.AngStep);

        if (s.ForPilot)
        {
            // AppliedTick — АБСОЛЮТНЫЙ uint16, а не дельта от baseTick: тик кадра
            // ввода живёт в ДОМЕНЕ ТИКОВ ПИЛОТА (счётчик клиента), а baseTick — в
            // домене физических шагов сервера; дельта между доменами переполнила бы
            // int16. Клиент разворачивает низ 16 бит относительно последнего
            // виденного AppliedTick (правило полуинтервала, Ship.ApplyBatchRecord).
            dst[OffAppliedTick] = (byte)s.AppliedTick;
            dst[OffAppliedTick + 1] = (byte)(s.AppliedTick >> 8);
            dst[OffAppliedMask] = (byte)s.AppliedMask;
            dst[OffAppliedMask + 1] = (byte)(s.AppliedMask >> 8);
            return PilotRecordSize;
        }
        return FullRecordSize;
    }

    /// <summary>Прочитать запись из `src`. Возвращает число прочитанных байт.</summary>
    public static int Read(System.ReadOnlySpan<byte> src, out ShipSnapshotQ s,
                           Vec3 anchor, int baseTick, float posStep,
                           bool forPilot, bool farTier)
    {
        int size = RecordSize(forPilot, farTier);
        s = default;
        if (src.Length < size) return 0;

        s.NetId = (ushort)(src[OffNetId] | (src[OffNetId + 1] << 8));
        // дельта тика — знаковая (снимок мог быть старше базового тика пакета)
        s.SimTick = baseTick + (short)(src[OffTick] | (src[OffTick + 1] << 8));
        s.Position = anchor + new Vec3(
            Quant.FromCode(ReadI16(src, OffPos + 0), posStep),
            Quant.FromCode(ReadI16(src, OffPos + 2), posStep),
            Quant.FromCode(ReadI16(src, OffPos + 4), posStep));
        s.Rotation = farTier ? ReadRotCoarse(src, OffRot) : ReadRot(src, OffRot);

        if (farTier)
        {
            s.LinearVelocity = Vec3.Zero;
            s.AngularVelocity = Vec3.Zero;
            s.FarTier = true;
            return FarRecordSize;
        }

        s.LinearVelocity = ReadVel(src, OffVel, Quant.VelStep);
        s.AngularVelocity = ReadAng(src, OffAng, Quant.AngStep);
        if (forPilot)
        {
            // AppliedTick — абсолютный низ 16 бит (см. запись выше); разворот
            // относительно последнего виденного тика делает получатель.
            s.AppliedTick = src[OffAppliedTick] | (src[OffAppliedTick + 1] << 8);
            s.AppliedMask = (uint)(src[OffAppliedMask] | (src[OffAppliedMask + 1] << 8));
            s.ForPilot = true;
            return PilotRecordSize;
        }
        return FullRecordSize;
    }

    private static void WriteI16(System.Span<byte> d, int o, int v)
    {
        d[o] = (byte)v;
        d[o + 1] = (byte)(v >> 8);
    }

    private static short ReadI16(System.ReadOnlySpan<byte> d, int o)
        => (short)(d[o] | (d[o + 1] << 8));

    // ------------------------------------------------ поворот: 4 Б (§2.1)
    // Канонический кватернион: наибольшей компоненте принудительно задан знак
    // «+», её величина не передаётся (она восстанавливается из единичной длины),
    // а её индекс уходит в 2 бита. Остальные три — int10 со шагом 1/511 ≈ 0.22°.
    // q и -q задают один и тот же поворот, поэтому отбрасывание знака безопасно.

    /// <summary>Шаг квантования компоненты кватерниона (int10: ±511 → 1/511).</summary>
    private const float RotScale = 511f;

    private static void WriteRot(System.Span<byte> d, int o, Quat q)
    {
        q = Normalize(q);
        int drop = 0;
        float best = MathF.Abs(q.X);
        if (MathF.Abs(q.Y) > best) { best = MathF.Abs(q.Y); drop = 1; }
        if (MathF.Abs(q.Z) > best) { best = MathF.Abs(q.Z); drop = 2; }
        if (MathF.Abs(q.W) > best) { best = MathF.Abs(q.W); drop = 3; }
        if (BestComponent(q, drop) < 0f) { q.X = -q.X; q.Y = -q.Y; q.Z = -q.Z; q.W = -q.W; }

        // три оставшиеся компоненты в int10, старшие 2 бита — индекс пропущенной.
        // ClampCode ждёт значение УЖЕ в единицах кода, поэтому компоненту нужно
        // умножить на шкалу (обратное деление округляло бы всё в 0/±1).
        int c0 = ClampCode(Component(q, (drop + 1) & 3) * RotScale, RotScale);
        int c1 = ClampCode(Component(q, (drop + 2) & 3) * RotScale, RotScale);
        int c2 = ClampCode(Component(q, (drop + 3) & 3) * RotScale, RotScale);
        uint packed = ((uint)(c0 & 0x3FF)) | (((uint)(c1 & 0x3FF)) << 10)
                    | (((uint)(c2 & 0x3FF)) << 20) | ((uint)drop << 30);
        d[o] = (byte)packed;
        d[o + 1] = (byte)(packed >> 8);
        d[o + 2] = (byte)(packed >> 16);
        d[o + 3] = (byte)(packed >> 24);
    }

    private static Quat ReadRot(System.ReadOnlySpan<byte> d, int o)
    {
        uint packed = (uint)d[o] | ((uint)d[o + 1] << 8) | ((uint)d[o + 2] << 16) | ((uint)d[o + 3] << 24);
        int drop = (int)((packed >> 30) & 3u);
        int c0 = SignExtend((int)(packed & 0x3FFu), 10);
        int c1 = SignExtend((int)((packed >> 10) & 0x3FFu), 10);
        int c2 = SignExtend((int)((packed >> 20) & 0x3FFu), 10);
        // default (все нули), НЕ Identity: иначе пропущенная компонента сохраняет
        // W=1 из Identity, sum завышается и rest=1-sum уходит в минус → W=0.
        var q = default(Quat);
        SetComponent(ref q, (drop + 1) & 3, c0 / RotScale);
        SetComponent(ref q, (drop + 2) & 3, c1 / RotScale);
        SetComponent(ref q, (drop + 3) & 3, c2 / RotScale);
        float sum = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        float rest = 1f - sum;
        SetComponent(ref q, drop, rest > 0f ? MathF.Sqrt(rest) : 0f);
        return Normalize(q);
    }

    /// <summary>Дальний тир: 3 × int8 (шаг 1/127 ≈ 0.45°) + индекс пропущенной
    /// компоненты. Точность ниже, но этот тир не интерполируют (§2.3).</summary>
    private const float RotCoarseScale = 127f;

    private static void WriteRotCoarse(System.Span<byte> d, int o, Quat q)
    {
        q = Normalize(q);
        int drop = 0;
        float best = MathF.Abs(q.X);
        if (MathF.Abs(q.Y) > best) { best = MathF.Abs(q.Y); drop = 1; }
        if (MathF.Abs(q.Z) > best) { best = MathF.Abs(q.Z); drop = 2; }
        if (MathF.Abs(q.W) > best) { best = MathF.Abs(q.W); drop = 3; }
        if (BestComponent(q, drop) < 0f) { q.X = -q.X; q.Y = -q.Y; q.Z = -q.Z; q.W = -q.W; }
        d[o] = (byte)drop;
        // компоненту умножаем на шкалу: ClampCode ждёт значение в единицах кода
        d[o + 1] = (byte)ClampCode(Component(q, (drop + 1) & 3) * RotCoarseScale, RotCoarseScale);
        d[o + 2] = (byte)ClampCode(Component(q, (drop + 2) & 3) * RotCoarseScale, RotCoarseScale);
        d[o + 3] = (byte)ClampCode(Component(q, (drop + 3) & 3) * RotCoarseScale, RotCoarseScale);
    }

    private static Quat ReadRotCoarse(System.ReadOnlySpan<byte> d, int o)
    {
        int drop = d[o] & 3;
        var q = default(Quat);   // см. ReadRot: Identity ломает rest=1-sum
        SetComponent(ref q, (drop + 1) & 3, SignedByte(d[o + 1]) / RotCoarseScale);
        SetComponent(ref q, (drop + 2) & 3, SignedByte(d[o + 2]) / RotCoarseScale);
        SetComponent(ref q, (drop + 3) & 3, SignedByte(d[o + 3]) / RotCoarseScale);
        float sum = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        float rest = 1f - sum;
        SetComponent(ref q, drop, rest > 0f ? MathF.Sqrt(rest) : 0f);
        return Normalize(q);
    }

    // ------------------------------------------------ скорости: 4 + 3 Б (§2.1)
    // Линейная: 3 × int10 в 4 байта (шаг Quant.VelStep → ±153.6 м/с).
    // Угловая: 3 × int8 (шаг Quant.AngStep → ±6.35 рад/с).

    private static void WriteVel(System.Span<byte> d, int o, Vec3 v, float step)
    {
        int c0 = ClampCode(v.X / step, 511f), c1 = ClampCode(v.Y / step, 511f), c2 = ClampCode(v.Z / step, 511f);
        uint packed = (uint)((c0 & 0x3FF) | ((c1 & 0x3FF) << 10) | ((c2 & 0x3FF) << 20));
        d[o] = (byte)packed;
        d[o + 1] = (byte)(packed >> 8);
        d[o + 2] = (byte)(packed >> 16);
        d[o + 3] = (byte)(packed >> 24);
    }

    private static Vec3 ReadVel(System.ReadOnlySpan<byte> d, int o, float step)
    {
        uint packed = (uint)d[o] | ((uint)d[o + 1] << 8) | ((uint)d[o + 2] << 16) | ((uint)d[o + 3] << 24);
        return new Vec3(
            SignExtend((int)(packed & 0x3FFu), 10) * step,
            SignExtend((int)((packed >> 10) & 0x3FFu), 10) * step,
            SignExtend((int)((packed >> 20) & 0x3FFu), 10) * step);
    }

    private static void WriteAng(System.Span<byte> d, int o, Vec3 w, float step)
    {
        d[o] = (byte)ClampCode(w.X / step, 127f);
        d[o + 1] = (byte)ClampCode(w.Y / step, 127f);
        d[o + 2] = (byte)ClampCode(w.Z / step, 127f);
    }

    private static Vec3 ReadAng(System.ReadOnlySpan<byte> d, int o, float step)
        => new(SignedByte(d[o]) * step, SignedByte(d[o + 1]) * step, SignedByte(d[o + 2]) * step);

    // ------------------------------------------------ мелкие утилиты
    private static int SignedByte(byte b) => b < 128 ? b : b - 256;

    private static int SignExtend(int v, int bits)
    {
        int shift = 32 - bits;
        return (v << shift) >> shift;
    }

    /// <summary>Насыщающее кодирование: выход за диапазон не должен оборачиваться
    /// в знак (иначе корабль на 160 м/с «развернётся» по скорости на клиенте).</summary>
    private static int ClampCode(float scaled, float maxCode)
    {
        int c = (int)MathF.Round(scaled);
        if (c > (int)maxCode) return (int)maxCode;
        if (c < -(int)maxCode) return -(int)maxCode;
        return c;
    }

    private static Quat Normalize(Quat q)
    {
        float len = MathF.Sqrt(q.LengthSquared);
        if (len < 1e-8f) return Quat.Identity;
        float inv = 1f / len;
        return new Quat(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
    }

    private static float Component(Quat q, int i) => i switch { 0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W };
    private static float BestComponent(Quat q, int i) => Component(q, i);

    private static void SetComponent(ref Quat q, int i, float v)
    {
        switch (i) { case 0: q.X = v; break; case 1: q.Y = v; break; case 2: q.Z = v; break; default: q.W = v; break; }
    }
}
