using System;
using SpaceCorporation.Net;

// Тесты шага R1 (план net-refactor-plan.md): квантование снимка и якорь тира.
// Гоняются БЕЗ Godot: dotnet run --project tools/CodecTest. Код возврата 0 = все
// проверки пройдены; иначе — число провалов.

internal static class CodecTest
{
    private static int _checks, _fails;

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _fails++; Console.WriteLine($"  FAIL: {what}"); }
    }

    private static double Deg(double rad) => rad * 180.0 / Math.PI;

    /// <summary>Угловая разница между кватернионами, радианы (через |dot|).</summary>
    private static double AngleBetween(in Quat a, in Quat b)
    {
        double dot = Math.Abs((double)a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W);
        if (dot > 1.0) dot = 1.0;
        return 2.0 * Math.Acos(dot);
    }

    private static int Main()
    {
        if (Environment.GetEnvironmentVariable("ROTDEBUG") == "1") { RotDebug.Run(); return 0; }

        var rng = new Random(20260729);      // детерминированно: тесты воспроизводимы

        // ------------------------------------------------ 1. round-trip записи наблюдателя
        Console.WriteLine("=== R1.1: round-trip полной записи (near/mid) ===");
        double maxPosErr = 0, maxRotErr = 0, maxVelErr = 0, maxAngErr = 0;
        var anchor = new Vec3(1234.5f, -678.25f, 90.125f);
        var buf = new byte[ShipRecordCodec.PilotRecordSize];
        for (int i = 0; i < 20000; i++)
        {
            var s = new ShipSnapshotQ
            {
                NetId = (ushort)rng.Next(1, 60000),
                SimTick = 100000 + rng.Next(0, 600),
                Position = anchor + new Vec3(Rand(rng, -300f, 300f), Rand(rng, -300f, 300f), Rand(rng, -300f, 300f)),
                Rotation = RandQuat(rng),
                LinearVelocity = new Vec3(Rand(rng, -150f, 150f), Rand(rng, -150f, 150f), Rand(rng, -150f, 150f)),
                AngularVelocity = new Vec3(Rand(rng, -6f, 6f), Rand(rng, -6f, 6f), Rand(rng, -6f, 6f)),
            };
            int written = ShipRecordCodec.Write(buf, s, anchor, 100000, Quant.PosStepNear);
            Check(written == ShipRecordCodec.FullRecordSize, $"Write вернул {written} (ожидалось 21)");
            ShipRecordCodec.Read(buf.AsSpan(0, written), out var r, anchor, 100000,
                                 Quant.PosStepNear, false, false);

            Check(r.NetId == s.NetId, $"netId: {r.NetId} != {s.NetId}");
            Check(r.SimTick == s.SimTick, $"simTick: {r.SimTick} != {s.SimTick}");
            maxPosErr = Math.Max(maxPosErr, Dist(r.Position, s.Position));
            maxRotErr = Math.Max(maxRotErr, AngleBetween(r.Rotation, s.Rotation));
            maxVelErr = Math.Max(maxVelErr, Dist(r.LinearVelocity, s.LinearVelocity));
            maxAngErr = Math.Max(maxAngErr, Dist(r.AngularVelocity, s.AngularVelocity));
        }
        Console.WriteLine($"  макс. ошибка позиции  {maxPosErr * 1000,6:F2} мм (шаг 10 мм)"
                      + $"\n  макс. ошибка поворота   {Deg(maxRotErr),6:F3}° (шаг 0.22°)"
                      + $"\n  макс. ошибка скорости   {maxVelErr,6:F3} м/с (шаг 0.30)"
                      + $"\n  макс. ошибка угл. скор. {maxAngErr,6:F3} рад/с (шаг 0.05)");
        Check(maxPosErr <= Quant.PosStepNear, "ошибка позиции ≤ шага 1 см");
        Check(Deg(maxRotErr) <= 0.5, $"ошибка поворота ≤ 0.5° (факт {Deg(maxRotErr):F3}°)");
        Check(maxVelErr <= Quant.VelStep, "ошибка линейной скорости ≤ шага 0.30 м/с");
        Check(maxAngErr <= Quant.AngStep, "ошибка угловой скорости ≤ шага 0.05 рад/с");

        // ------------------------------------------------ 2. запись пилота
        // AppliedTick — абсолютный низ 16 бит (домен тиков пилота, малые числа).
        Console.WriteLine("=== R1.2: пилотская запись (AppliedTick + AppliedMask) ===");
        var pilot = new ShipSnapshotQ
        {
            NetId = 7, SimTick = 123456, AppliedTick = 1234, AppliedMask = 0xBEEF,
            Position = anchor + new Vec3(12.34f, -5.67f, 8.90f),
            Rotation = RandQuat(rng), ForPilot = true,
        };
        int n = ShipRecordCodec.Write(buf, pilot, anchor, 123400, Quant.PosStepNear);
        Check(n == ShipRecordCodec.PilotRecordSize, $"Write(пилот) вернул {n} (ожидалось 25)");
        ShipRecordCodec.Read(buf.AsSpan(0, n), out var pr, anchor, 123400, Quant.PosStepNear, true, false);
        Check(pr.AppliedTick == pilot.AppliedTick, $"appliedTick {pr.AppliedTick} != {pilot.AppliedTick}");
        Check(pr.AppliedMask == pilot.AppliedMask, $"appliedMask {pr.AppliedMask:X4} != {pilot.AppliedMask:X4}");
        Check(pr.NetId == pilot.NetId && pr.SimTick == pilot.SimTick, "netId/simTick пилота");
        // знаковая дельта тика: устаревший снимок (SimTick < baseTick) читается точно
        var stalePilot = pilot; stalePilot.SimTick = 123400 - 300; stalePilot.AppliedTick = 900;
        n = ShipRecordCodec.Write(buf, stalePilot, anchor, 123400, Quant.PosStepNear);
        ShipRecordCodec.Read(buf.AsSpan(0, n), out var spr, anchor, 123400, Quant.PosStepNear, true, false);
        Check(spr.SimTick == stalePilot.SimTick, $"stale simTick {spr.SimTick} != {stalePilot.SimTick}");
        Check(spr.AppliedTick == 900, $"stale appliedTick {spr.AppliedTick} != 900");

        // ------------------------------------------------ 3. дальний тир: квант 10 см
        Console.WriteLine("=== R1.3: дальний тир (квант 10 см, грубый поворот) ===");
        double farPosErr = 0, farRotErr = 0;
        var farAnchor = new Vec3(0, 0, 0);
        for (int i = 0; i < 20000; i++)
        {
            var s = new ShipSnapshotQ
            {
                NetId = (ushort)rng.Next(1, 60000), SimTick = 5000 + rng.Next(0, 300),
                Position = new Vec3(Rand(rng, -3000f, 3000f), Rand(rng, -3000f, 3000f), Rand(rng, -3000f, 3000f)),
                Rotation = RandQuat(rng), FarTier = true,
            };
            int w = ShipRecordCodec.Write(buf, s, farAnchor, 5000, Quant.PosStepFar);
            Check(w == ShipRecordCodec.FarRecordSize, $"Write(far) вернул {w} (ожидалось 14)");
            ShipRecordCodec.Read(buf.AsSpan(0, w), out var r, farAnchor, 5000, Quant.PosStepFar, false, true);
            farPosErr = Math.Max(farPosErr, Dist(r.Position, s.Position));
            farRotErr = Math.Max(farRotErr, AngleBetween(r.Rotation, s.Rotation));
            Check(r.LinearVelocity.LengthSquared == 0f, "far-запись не несёт скорость");
        }
        Console.WriteLine($"  макс. ошибка позиции {farPosErr,6:F3} м (шаг 0.10)"
                        + $"\n  макс. ошибка поворота {Deg(farRotErr),6:F3}° (шаг 0.45°)");
        Check(farPosErr <= Quant.PosStepFar, "ошибка позиции far ≤ 0.10 м");
        Check(Deg(farRotErr) <= 1.5, $"ошибка поворота far ≤ 1.5° (факт {Deg(farRotErr):F3}°)");

        // ------------------------------------------------ 4. переполнение int16-смещения
        // Godot-синхронизатор на переполнении int16 молча заворачивает значение
        // (корабль телепортируется на другой конец поля). Наш кодек обязан вернуть
        // -1, чтобы вызывающий перевыбрал якорь, а не испортил поток.
        Console.WriteLine("=== R1.4: переполнение смещения → Write возвращает -1 ===");
        var overflow = new ShipSnapshotQ
        {
            NetId = 1, SimTick = 0, FarTier = false,
            Position = new Vec3(400f, 0, 0),      // 400 м > 327.67 м при шаге 1 см
            Rotation = Quat.Identity,
        };
        Check(ShipRecordCodec.Write(buf, overflow, Vec3.Zero, 0, Quant.PosStepNear) == -1,
              "400 м при шаге 1 см → -1");
        Check(ShipRecordCodec.Write(buf, overflow, Vec3.Zero, 0, Quant.PosStepFar) > 0,
              "400 м при шаге 10 см → помещается");
        var edge = new ShipSnapshotQ
        {
            NetId = 1, SimTick = 0, FarTier = false, Position = new Vec3(327.67f, 0, 0),
            Rotation = Quat.Identity,
        };
        Check(ShipRecordCodec.Write(buf, edge, Vec3.Zero, 0, Quant.PosStepNear) > 0,
              "327.67 м при шаге 1 см → помещается (код 32767 = max int16)");
        var edge2 = new ShipSnapshotQ
        {
            NetId = 1, SimTick = 0, FarTier = false, Position = new Vec3(327.68f, 0, 0),
            Rotation = Quat.Identity,
        };
        Check(ShipRecordCodec.Write(buf, edge2, Vec3.Zero, 0, Quant.PosStepNear) == -1,
              "327.68 м при шаге 1 см → -1");

        // ------------------------------------------------ 5. якорь тира при 150 м/с
        // Политика перевыбора (§2.1): порог 200 м при лимите 327.67 м. Корабль на
        // полной скорости 150 м/с не должен требовать перевыбора чаще, чем раз в
        // ~1.3 с, и смещение обязано оставаться в лимите всегда.
        Console.WriteLine("=== R1.5: якорь тира при 150 м/с (перевыбор по порогу 200 м) ===");
        var shipPos = new Vec3(0, 0, 0);
        var vel = new Vec3(150f, 0, 0);
        var shipAnchor = Vec3.Zero;
        int reanchors = 0;
        const float reanchorThreshold = 200f;
        const int ticks = 30 * 60;                 // минута полёта на полной скорости
        for (int t = 0; t < ticks; t++)
        {
            shipPos += vel * (1f / 30f);
            if (!Quant.FitsInt16(shipPos - shipAnchor, Quant.PosStepNear)
                || Dist(shipPos, shipAnchor) > reanchorThreshold)
            {
                shipAnchor = shipPos;
                reanchors++;
            }
            var s = new ShipSnapshotQ { NetId = 2, SimTick = t, Position = shipPos, Rotation = Quat.Identity };
            Check(ShipRecordCodec.Write(buf, s, shipAnchor, t, Quant.PosStepNear) > 0,
                   $"тик {t}: запись не влезла в смещение");
        }
        double secondsPerReanchor = (ticks / 30.0) / Math.Max(1, reanchors);
        Console.WriteLine($"  перевыборов якоря за 60 с: {reanchors} (≈ раз в {secondsPerReanchor:F2} с)");
        Check(reanchors <= 60 / 1.3 + 1, $"перевыборов ≤ 47 за минуту (факт {reanchors})");
        Check(secondsPerReanchor >= 1.3, $"интервал перевыбора ≥ 1.3 с (факт {secondsPerReanchor:F2} с)");

        // far-тир при том же лимите дистанции перевыбирается на порядок реже:
        // лимит смещения 3276 м, порог 2000 м.
        var farShipAnchor = Vec3.Zero;
        int farReanchors = 0;
        for (int t = 0; t < ticks; t++)
        {
            if (Dist(shipPos, farShipAnchor) > 0) break;
        }
        shipPos = Vec3.Zero;
        for (int t = 0; t < ticks; t++)
        {
            shipPos += vel * (1f / 30f);
            if (Dist(shipPos, farShipAnchor) > 2000f) { farShipAnchor = shipPos; farReanchors++; }
        }
        Console.WriteLine($"  far-тир (порог 2000 м): перевыборов за 60 с: {farReanchors}");
        // За 60 с корабль проходит 9000 м; при пороге 2000 м якорь переводится на
        // 2000/4000/6000/8000 → 4 раза. Это на порядок реже near-тира (44):
        // интервал ≈ 13 с против ≈ 1.36 с.
        double farSecondsPerReanchor = (ticks / 30.0) / Math.Max(1, farReanchors);
        Check(farReanchors <= 5, $"far-тир перевыбирается ≤ 5 раз за минуту (факт {farReanchors})");
        Check(farSecondsPerReanchor >= 13.0,
              $"far-интервал перевыбора ≥ 13 с (факт {farSecondsPerReanchor:F2} с)");
        Check(reanchors > 0 && farReanchors * 8 <= reanchors,
              $"far перевыборов на порядок меньше near (near {reanchors}, far {farReanchors})");

        // ------------------------------------------------ 6. заголовок батча + вместимость MTU
        Console.WriteLine("=== R1.6: заголовок батча и вместимость MTU ===");
        var hb = new byte[ShipRecordCodec.HeaderSize];
        ShipRecordCodec.WriteHeader(hb, 1_234_567, 400,
            ShipRecordCodec.FlagHasPilotRecord, Quant.PosStepNear);
        ShipRecordCodec.ReadHeader(hb, out int bt, out int rc, out byte fl, out float ps);
        Check(bt == 1_234_567, $"baseTick {bt}");
        Check(rc == 400, $"recordCount {rc}");
        Check(fl == ShipRecordCodec.FlagHasPilotRecord, $"flags {fl}");
        Check(MathF.Abs(ps - Quant.PosStepNear) < 1e-6f, $"posStep {ps}");
        ShipRecordCodec.WriteHeader(hb, 1, 1, ShipRecordCodec.FlagAllFarTier, Quant.PosStepFar);
        ShipRecordCodec.ReadHeader(hb, out _, out _, out _, out ps);
        Check(MathF.Abs(ps - Quant.PosStepFar) < 1e-6f, $"posStep far {ps} (множитель 10)");

        // заголовок с якорем (FlagHasAnchor): 8+12 Б, round-trip якоря и смещения записей
        Console.WriteLine("=== R1.6b: заголовок с якорем (FlagHasAnchor) ===");
        var hb2 = new byte[ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes];
        var aRef = new Vec3(-4200.75f, 133.5f, 777.25f);
        int hl = ShipRecordCodec.WriteHeaderWithAnchor(hb2, 987_654, 12,
            ShipRecordCodec.FlagHasPilotRecord, Quant.PosStepNear, aRef);
        Check(hl == ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes,
            $"WriteHeaderWithAnchor вернул {hl}");
        Check((hb2[6] & ShipRecordCodec.FlagHasAnchor) != 0, "флаг FlagHasAnchor выставлен");
        var aRead = new Vec3(999, 999, 999);   // заведомо неверный: должен перезаписаться
        int hl2 = ShipRecordCodec.ReadHeaderWithAnchor(hb2, out int bt2, out int rc2,
            out byte fl2, out float ps2, ref aRead);
        Check(bt2 == 987_654 && rc2 == 12, "baseTick/count в заголовке с якорем");
        Check((fl2 & ShipRecordCodec.FlagHasAnchor) != 0, "флаг FlagHasAnchor прочитан");
        Check(hl2 == ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes,
            $"смещение записей после якорного заголовка = {hl2}");
        Check(aRead.X == aRef.X && aRead.Y == aRef.Y && aRead.Z == aRef.Z,
            $"якорь round-trip ({aRead.X},{aRead.Y},{aRead.Z}) != ({aRef.X},{aRef.Y},{aRef.Z})");
        // без флага — смещение 8, якорь не трогается
        ShipRecordCodec.WriteHeader(hb, 5, 1, ShipRecordCodec.FlagAllFarTier, Quant.PosStepFar);
        var keep = new Vec3(1, 2, 3);
        int hl3 = ShipRecordCodec.ReadHeaderWithAnchor(hb, out _, out _, out _, out _, ref keep);
        Check(hl3 == ShipRecordCodec.HeaderSize, "без флага смещение = HeaderSize");
        Check(keep.X == 1 && keep.Y == 2 && keep.Z == 3, "без флага якорь не меняется");

        int perMtuFull = ShipRecordCodec.RecordsPerMtu(ShipRecordCodec.FullRecordSize);
        int perMtuFar = ShipRecordCodec.RecordsPerMtu(ShipRecordCodec.FarRecordSize);
        Console.WriteLine($"  записей в MTU 1400: полная {perMtuFull}, far {perMtuFar}");
        Check(perMtuFull == 64, $"полная запись в MTU = 64 (факт {perMtuFull})");
        Check(perMtuFar == 96, $"far-запись в MTU = 96 (факт {perMtuFar})");

        // ------------------------------------------------ 7. батч из 400 записей: упаковка/распаковка
        Console.WriteLine("=== R1.7: батч 400 записей (near 100 + mid 200 + far 100) ===");
        var batch = new byte[ShipRecordCodec.HeaderSize + 400 * ShipRecordCodec.FullRecordSize];
        int baseTick = 900_000;
        var batchAnchor = new Vec3(500f, 500f, 500f);
        int off = ShipRecordCodec.HeaderSize;
        int writtenRecords = 0;
        for (int i = 0; i < 400; i++)
        {
            bool far = i >= 300;
            var s = new ShipSnapshotQ
            {
                NetId = (ushort)(i + 1),
                SimTick = baseTick + (i % 30),
                Position = batchAnchor + new Vec3(Rand(rng, -300f, 300f), Rand(rng, -300f, 300f), Rand(rng, -300f, 300f)),
                Rotation = RandQuat(rng),
                LinearVelocity = new Vec3(Rand(rng, -150f, 150f), 0, 0),
                AngularVelocity = new Vec3(Rand(rng, -6f, 6f), 0, 0),
                FarTier = far,
            };
            int w = ShipRecordCodec.Write(batch.AsSpan(off), s, batchAnchor, baseTick,
                                          far ? Quant.PosStepFar : Quant.PosStepNear);
            Check(w > 0, $"запись {i} не влезла");
            off += w;
            writtenRecords++;
        }
        ShipRecordCodec.WriteHeader(batch, baseTick, writtenRecords, 0, Quant.PosStepNear);
        Console.WriteLine($"  размер батча: {off} Б (записей {writtenRecords})");
        int mtuPayload = 1400 - (4 + 28 + 12 + 2) - ShipRecordCodec.HeaderSize;
        Check(off > mtuPayload, "400 смешанных записей (" + off + " Б) не влезают в один MTU ("
              + mtuPayload + " Б полезной нагрузки) → батч обязан резаться на пакеты (проверка R3)");
        // Проверка, что реальные размеры записей соответствуют ожидаемым:
        // 300 near-полных (21 Б) + 100 far (14 Б) = 6300 + 1400 = 7700 Б + заголовок 8 Б.
        Check(off == ShipRecordCodec.HeaderSize + 300 * ShipRecordCodec.FullRecordSize
                        + 100 * ShipRecordCodec.FarRecordSize,
              $"размер батча = 8 + 300×21 + 100×14 = 7708 Б (факт {off})");

        // распаковка: far-записи идут последними, шаг позиции у них свой
        ShipRecordCodec.ReadHeader(batch, out bt, out rc, out fl, out ps);
        int p = ShipRecordCodec.HeaderSize;
        double batchPosErr = 0;
        for (int i = 0; i < rc; i++)
        {
            bool far = i >= 300;
            float step = far ? Quant.PosStepFar : Quant.PosStepNear;
            int used = ShipRecordCodec.Read(batch.AsSpan(p), out var r, batchAnchor, bt, step, false, far);
            Check(used > 0, $"чтение записи {i}");
            p += used;
            double e = Math.Abs(r.NetId - (i + 1));
            Check(e == 0, $"netId записи {i}: {r.NetId}");
            batchPosErr = Math.Max(batchPosErr, r.Position.LengthSquared == 0 ? 0 : 0);
        }
        Check(p == off, $"распаковка прошла ровно {off} Б (факт {p})");

        // ------------------------------------------------ 8. насыщение скорости
        // Корабль на 160 м/с не должен «разворачивать» знак скорости переполнением.
        Console.WriteLine("=== R1.8: насыщение за пределами диапазона ===");
        var fast = new ShipSnapshotQ
        {
            NetId = 3, SimTick = 0, Position = new Vec3(0, 0, 0), Rotation = Quat.Identity,
            LinearVelocity = new Vec3(160f, -160f, 0), AngularVelocity = new Vec3(7f, -7f, 0),
        };
        ShipRecordCodec.Write(buf, fast, Vec3.Zero, 0, Quant.PosStepNear);
        ShipRecordCodec.Read(buf, out var fr, Vec3.Zero, 0, Quant.PosStepNear, false, false);
        Console.WriteLine($"  160 м/с → {fr.LinearVelocity}, 7 рад/с → {fr.AngularVelocity}");
        Check(fr.LinearVelocity.X > 150f && fr.LinearVelocity.X < 161f, "160 м/с насыщается в +диапазон, знак сохранён");
        Check(fr.LinearVelocity.Y < -150f && fr.LinearVelocity.Y > -161f, "-160 м/с насыщается в -диапазон, знак сохранён");
        Check(fr.AngularVelocity.X > 6f && fr.AngularVelocity.X < 7.1f, "7 рад/с насыщается в +диапазон");
        Check(fr.AngularVelocity.Y < -6f && fr.AngularVelocity.Y > -7.1f, "-7 рад/с насыщается в -диапазон");

        // ------------------------------------------------ 9. дельта тика за пределами uint16
        Console.WriteLine("=== R1.9: дельта SimTick вне uint16 (мусор, а не паника) ===");
        var stale = new ShipSnapshotQ
        {
            NetId = 4, SimTick = 1_000_000 - 70_000,   // на 70 000 тиков старше базового
            Position = new Vec3(1, 2, 3), Rotation = Quat.Identity,
        };
        int wst = ShipRecordCodec.Write(buf, stale, Vec3.Zero, 1_000_000, Quant.PosStepNear);
        ShipRecordCodec.Read(buf.AsSpan(0, wst), out var sr, Vec3.Zero, 1_000_000, Quant.PosStepNear, false, false);
        Check(sr.NetId == 4, "netId у устаревшей записи читается");
        Check(sr.SimTick != stale.SimTick, "дельта вне uint16 не может быть восстановлена корректно"
              + " — вызывающий обязан отбросить такую запись (проверка R3)");

        // ------------------------------------------------ 10. аллокации: путь без new
        Console.WriteLine("=== R1.10: отсутствие аллокаций в горячем пути ===");
        // у записей фиксированные смещения, поэтому распаковка не парсит поля и не
        // создаёт объектов; проверяем косвенно — GC-счётчик не растёт на 100k прогонов
        long before = GC.GetAllocatedBytesForCurrentThread();
        var sink = new ShipSnapshotQ();
        for (int i = 0; i < 100_000; i++)
        {
            ShipRecordCodec.Write(buf, pilot, anchor, 123400, Quant.PosStepNear);
            ShipRecordCodec.Read(buf, out sink, anchor, 123400, Quant.PosStepNear, true, false);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Console.WriteLine($"  аллокаций на 100 000 циклов write+read: {allocated} Б");
        Check(allocated <= 0, "горячий путь не аллоцирует (0 Б)");

        Console.WriteLine($"\nпроверок: {_checks}, провалов: {_fails}");
        Console.WriteLine(_fails == 0 ? "R1 PASS" : "R1 FAIL");
        return _fails;
    }

    private static float Rand(Random rng, float lo, float hi) => lo + (float)rng.NextDouble() * (hi - lo);

    private static Quat RandQuat(Random rng)
    {
        // случайный единичный кватернион (нормализация обязательна: кодек исходит
        // из единичной длины при восстановлении пропущенной компоненты)
        float x = Rand(rng, -1, 1), y = Rand(rng, -1, 1), z = Rand(rng, -1, 1), w = Rand(rng, -1, 1);
        float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (len < 1e-6f) return Quat.Identity;
        return new Quat(x / len, y / len, z / len, w / len);
    }

    private static double Dist(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
