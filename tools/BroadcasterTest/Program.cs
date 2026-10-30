using System;
using System.Diagnostics;
using SpaceCorporation.Net;

// Тесты серверного SnapshotBroadcaster (шаг R3 плана net-refactor-plan.md).
//
// Стенд: 400 фиктивных тел в поле 13 км (без физики), тиры интереса от точки
// обзора. Пресет тиров — «базово» (near 100@20 Гц, mid 200@10, far 100@3),
// цели не шлём. Ожидаем ровно норматив из tools/traffic_estimate.py:
//   rec/s = 4300, pkt/s ≈ 104, KB/s ≈ 126 (±15 %).
// Плюс проверка CPU-бюджета нарезки батчей (≤ 3 мс/тик).

static class R3
{
    static int checks = 0, fails = 0;
    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("  FAIL: " + what); }
    }

    const int FullRec = 30;   // консервативная полная запись (near/mid)
    const int FarRec = 14;    // far: позиция + грубый поворот, без скоростей

    static void Main()
    {
        Console.WriteLine("=== R3: SnapshotBroadcaster (базовый пресет тиров, 400 тел) ===");

        // --- 400 фиктивных тел: 100 near, 200 mid, 100 far от точки обзора ---
        var pos = new Vec3[400];
        var rng = new Random(20240307);
        // near: радиус ≤ 350 (внутри входа near 0.9·400=360, чтобы не цепляло mid)
        for (int i = 0; i < 100; i++)
            pos[i] = RandInShell(rng, 10f, 350f);
        // mid: внутри входа mid 0.9·1200=1080 и вне near (500..1050)
        for (int i = 100; i < 300; i++)
            pos[i] = RandInShell(rng, 500f, 1050f);
        // far: внутри входа far 0.9·4000=3600 и вне mid (1500..3500)
        for (int i = 300; i < 400; i++)
            pos[i] = RandInShell(rng, 1500f, 3500f);

        var index = new SpatialIndex();
        index.Rebuild(pos);
        var st = new InterestState();
        var near = new System.Collections.Generic.List<int>();
        var mid = new System.Collections.Generic.List<int>();
        var far = new System.Collections.Generic.List<int>();
        index.QueryTiers(Vec3.Zero, 400f, 1200f, 4000f, st, near, mid, far);

        Console.WriteLine($"  тиры от обзора: near={near.Count}, mid={mid.Count}, far={far.Count}");
        Check(near.Count == 100 && mid.Count == 200 && far.Count == 100,
              $"тиры 100/200/100 (факт {near.Count}/{mid.Count}/{far.Count})");

        // --- Кормим ядро базовым пресетом: тиры из замера интереса ---
        var tiers = new[]
        {
            new BroadcasterCore.TierStream(near.Count, 20, FullRec),
            new BroadcasterCore.TierStream(mid.Count, 10, FullRec),
            new BroadcasterCore.TierStream(far.Count, 3,  FarRec),
        };

        var (rec, pkt, down) = BroadcasterCore.MeasureSecond(tiers, 0.0, 0.0);
        double kb = down / 1024.0;
        Console.WriteLine($"  за секунду: rec/s={rec}, pkt/s={pkt}, вниз={kb:F1} КБ/с");

        // rec/s: календарь тиков (мастер 20 Гц) округляет far-тир 3 Гц до
        // периода 6 тиков = 4 срабатывания/20 тиков = 400 записей/с вместо
        // номинальных 300. Итого 4400 против расчётных 4300 — в пределах ±15 %.
        WithinPct(rec, 4300, 0.15, "rec/s ≈ 4300");
        WithinPct(pkt, 104, 0.15, "pkt/s ≈ 104");
        WithinPct(kb, 126.0, 0.15, "KB/s ≈ 126");

        // --- Сверка с нормативом traffic_estimate.py (тот же базово-пресет) ---
        // 100*20 + 200*10 + 100*3 = 4300 записей; пакеты по календарю top=20.
        Check(BroadcasterCore.TopHz(tiers) == 20, "верх календаря = 20 Гц");
        Check(BroadcasterCore.TierFires(tiers[0], 20, 7), "near горит на каждом тике");
        Check(BroadcasterCore.TierFires(tiers[1], 20, 6) && !BroadcasterCore.TierFires(tiers[1], 20, 7),
              "mid горит на чётных тиках");

        // --- Нарезка near-тика на пакеты по MTU ---
        int nearTickBytes = near.Count * FullRec;   // 3000 Б
        int nearPkts = BroadcasterCore.SplitIntoBatches(nearTickBytes);
        Console.WriteLine($"  near-тик: {nearTickBytes} Б → {nearPkts} пакетов (ёмкость батча {BroadcasterCore.BatchPayloadCap} Б)");
        Check(nearPkts == 3, $"near-тик 3000 Б → 3 пакета (факт {nearPkts})");

        // --- CPU-бюджет: собрать и нарезать все тики секунды, замерить время ---
        // Худший тик (near+mid+far) = 100*30 + 200*30 + 100*14 = 10400 Б.
        var packBuf = new byte[16384];
        var sw = Stopwatch.StartNew();
        const int iters = 2000;   // ~100 секунд симуляции по 20 тиков
        long worstTickUs = 0;
        for (int it = 0; it < iters; it++)
        {
            for (int t = 0; t < 20; t++)
            {
                int vol = 0;
                foreach (var tier in tiers)
                    if (BroadcasterCore.TierFires(tier, 20, t))
                        vol += tier.Count * tier.RecordSize;
                // имитируем запись записей в буфер (заполнение байтов) + нарезку
                for (int b = 0; b < vol && b < packBuf.Length; b++) packBuf[b] = (byte)b;
                long tickStart = sw.ElapsedTicks;
                int _ = BroadcasterCore.SplitIntoBatches(vol);
                long us = (sw.ElapsedTicks - tickStart) * 1_000_000 / Stopwatch.Frequency;
                if (us > worstTickUs) worstTickUs = us;
            }
        }
        sw.Stop();
        double avgTickMs = sw.Elapsed.TotalMilliseconds / (iters * 20.0);
        Console.WriteLine($"  CPU нарезки: avg {avgTickMs:F4} мс/тик, худший тик {worstTickUs} мкс");
        Check(avgTickMs <= 3.0, $"CPU нарезки ≤ 3 мс/тик (факт avg {avgTickMs:F4} мс)");

        // ==================== R5: аварийный репорт позы пилота ====================
        Console.WriteLine("--- R5: PilotGapWatch (сторожевой счётчик пропадания эталона) ---");

        // (а) эталон приходит регулярно (20 Гц) — запроса нет никогда.
        var w = new BroadcasterCore.PilotGapWatch(
            BroadcasterCore.PilotGapWatch.DefaultTimeout,
            BroadcasterCore.PilotGapWatch.DefaultRequestEvery);
        int requests = 0;
        for (int i = 0; i < 200; i++)   // 10 с по 20 Гц
        {
            w.OnFresh();
            if (w.Tick(1f / 20f)) requests++;
        }
        Check(requests == 0, $"при регулярном эталоне запросов нет (факт {requests})");

        // (б) эталон пропал — через 0.5 с обязан быть ровно один запрос,
        //     дальше кулдаун 0.5 с режет шторм: за 3 с простоя ~6 запросов.
        w = new BroadcasterCore.PilotGapWatch();
        w.OnFresh();
        w.Tick(1f / 20f);              // ещё свежо
        requests = 0;
        // 3 с без свежих записей, шаг 1/20 с
        for (int i = 0; i < 60; i++)
            if (w.Tick(1f / 20f)) requests++;
        // первое срабатывание на ~0.5 с, далее каждые 0.5 с → ~6 за 3 с
        Check(requests >= 5 && requests <= 7,
              $"за 3 с простоя 5..7 запросов (кулдаун 0.5 с), факт {requests}");
        Check(w.FreshAge >= 3f - 1e-3f, $"FreshAge растёт без эталона (факт {w.FreshAge:F2})");

        // (в) эталон вернулся — счётчик обнулился, просрочки нет.
        w.OnFresh();
        Check(w.FreshAge == 0f, "OnFresh обнуляет возраст эталона");
        Check(!w.Tick(1f / 20f), "после возврата эталона запрос не срабатывает");

        // (г) полный снимок: пилотская запись (якорь=позиция) проходит round-trip
        //     через кодек с ошибкой кванта near-шага.
        var src = new ShipSnapshotQ
        {
            NetId = 7, SimTick = 123456,
            Position = new Vec3(1234.5f, -567.25f, 89.125f),
            Rotation = new Quat(0.1f, 0.2f, 0.3f, 0.92f),
            LinearVelocity = new Vec3(150f, -20f, 3.5f),
            AngularVelocity = new Vec3(0.5f, -0.25f, 0.1f),
            AppliedTick = 1234, AppliedMask = 0b1011,
            ForPilot = true, FarTier = false,
        };
        var anchor = src.Position;
        var buf = new byte[ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes
                           + ShipRecordCodec.PilotRecordSize];
        int hp = ShipRecordCodec.WriteHeaderWithAnchor(buf.AsSpan(),
            src.SimTick, 1, ShipRecordCodec.FlagHasPilotRecord, Quant.PosStepNear, anchor);
        int wp = ShipRecordCodec.Write(buf.AsSpan(hp), in src, anchor, src.SimTick, Quant.PosStepNear);
        Check(hp == ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes,
              $"заголовок с якорем = {ShipRecordCodec.HeaderSize + ShipRecordCodec.AnchorBytes} Б (факт {hp})");
        Check(wp == ShipRecordCodec.PilotRecordSize,
              $"пилотская запись = {ShipRecordCodec.PilotRecordSize} Б (факт {wp})");

        int rd = ShipRecordCodec.Read(buf.AsSpan(hp), out ShipSnapshotQ got,
            anchor, src.SimTick, Quant.PosStepNear, true, false);
        Check(rd == ShipRecordCodec.PilotRecordSize, $"прочитано {ShipRecordCodec.PilotRecordSize} Б (факт {rd})");
        Check(got.NetId == src.NetId, "NetId round-trip");
        Check(got.SimTick == src.SimTick, "SimTick round-trip");
        Check(got.ForPilot && !got.FarTier, "флаг ForPilot восстановлен");
        Check(got.AppliedTick == src.AppliedTick, "AppliedTick round-trip");
        Check(got.AppliedMask == src.AppliedMask, "AppliedMask round-trip");
        // позиция: якорь = сама позиция → смещение 0, ошибка кванта ~0 (≤ шаг near)
        float posErr = (got.Position - src.Position).LengthSquared;
        Check(posErr <= Quant.PosStepNear * Quant.PosStepNear,
              $"позиция: ошибка кванта² {posErr:F6} ≤ шаг² {(Quant.PosStepNear * Quant.PosStepNear):F6}");
        // скорость: ошибка по оси ≤ полшага → |Δ| ≤ (VelStep/2)·√3
        float velErr2 = (got.LinearVelocity - src.LinearVelocity).LengthSquared;
        float velBound = Quant.VelStep * 0.5f * 1.733f;
        Check(velErr2 <= velBound * velBound,
              $"скорость: ошибка² {velErr2:F4} ≤ {(velBound * velBound):F4}");

        // (д) петля восстановления под потерей. Два сценария в одном прогоне:
        //     (д1) независимая потеря 30 % без всплесков — на 20 Гц НЕ создаёт
        //          разрыва в 0.5 с (макс. разрыв 2 тика), значит watchdog НЕ
        //          должен срабатывать ложно (нет шторма запросов на ровном месте);
        //     (д2) периодический обрыв связи (всплеск потерь ~1 с каждые 5 с) —
        //          watchdog обязан заметить пропажу эталона и восстановить его
        //          полным снимком; худший простой эталона остаётся ограниченным.
        //     Это R5-специфичная логика под потерей; сквозной predErr под потерей
        //     (с latency, реконсиляцией и interpBuf) — стенд R9.
        var wl = new BroadcasterCore.PilotGapWatch(
            BroadcasterCore.PilotGapWatch.DefaultTimeout,
            BroadcasterCore.PilotGapWatch.DefaultRequestEvery);
        float dt = 1f / 20f;
        float maxStale = 0f;     // худший возраст эталона за всё время
        int fullSnapshots = 0;   // сколько раз сработала петля восстановления
        int nearDelivered = 0, nearLost = 0;
        int outageTicks = 0;     // тиков внутри обрыва
        for (int i = 0; i < 1000; i++)   // 50 с симуляции
        {
            // Обрыв связи: каждые 100 тиков (5 с) — 20 тиков (1 с) всё теряется.
            bool inOutage = (i % 100) >= 80;
            // Вне обрыва — независимая потеря 30 % (падает i%10==2,6,9).
            bool lost = inOutage || (i % 10 == 2) || (i % 10 == 6) || (i % 10 == 9);
            if (inOutage) outageTicks++;
            if (lost) nearLost++;
            else { nearDelivered++; wl.OnFresh(); }

            bool needReq = wl.Tick(dt);
            // Пик простоя эталона фиксируем СРАЗУ после Tick (до сброса полным снимком).
            if (wl.FreshAge > maxStale) maxStale = wl.FreshAge;
            if (needReq)
            {
                fullSnapshots++;
                // Полный снимок дошёл — эталон снова свежий (в модели всегда:
                // запрос по надёжному каналу 1, ответ — маленький отдельный пакет).
                wl.OnFresh();
            }
        }
        // Обрывов было 10 (каждые 100 тиков по 20 тиков потерь подряд).
        Check(outageTicks == 200, $"тиков обрыва: {outageTicks} (ожидалось 200)");
        Check(fullSnapshots >= 10 && fullSnapshots <= 30,
              $"под обрывами петля сработала разумное число раз: {fullSnapshots} " +
              "(≥10 обрывов, кулдаун режет повторные запросы внутри одного обрыва)");
        // Худший простой эталона не должен превышать порог + кулдаун + запас на тик:
        // даже сквозь 1-секундный обрыв пилот не зависает без эталона надолго.
        float bound = BroadcasterCore.PilotGapWatch.DefaultTimeout
                    + BroadcasterCore.PilotGapWatch.DefaultRequestEvery + dt;
        Check(maxStale <= bound,
              $"худший простой эталона {maxStale:F3} с ≤ bound {bound:F3} " +
              "(пилот не зависает без эталона дольше порога+кулдауна)");

        Console.WriteLine($"проверок: {checks}, провалов: {fails}");
        Console.WriteLine(fails == 0 ? "R3 PASS" : "R3 FAIL");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    static void WithinPct(double actual, double target, double tol, string what)
    {
        double dev = Math.Abs(actual - target) / target;
        Check(dev <= tol, $"{what}: факт {actual:F1}, цель {target}, откл {dev * 100:F1}% (допуск {tol * 100:F0}%)");
    }

    static Vec3 RandInShell(Random rng, float rMin, float rMax)
    {
        // равномерная точка в сферической оболочке [rMin, rMax]
        double u = rng.NextDouble() * 2 - 1;
        double phi = rng.NextDouble() * Math.PI * 2;
        double s = Math.Sqrt(1 - u * u);
        double r3 = rMin * rMin * rMin + rng.NextDouble() * (rMax * rMax * rMax - rMin * rMin * rMin);
        float r = (float)Math.Cbrt(r3);
        return new Vec3((float)(r * s * Math.Cos(phi)), (float)(r * s * Math.Sin(phi)), (float)(r * u));
    }
}
