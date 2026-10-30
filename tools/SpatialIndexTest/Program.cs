using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SpaceCorporation.Net;

// Тесты шага R2 (план net-refactor-plan.md): серверный SpatialIndex — грид 400 м,
// тиры интереса, гистерезис ±10 %. Гоняются БЕЗ Godot:
//   dotnet run --project tools/SpatialIndexTest
// Код возврата 0 = все проверки пройдены; иначе — число провалов.

internal static class SpatialIndexTest
{
    private static int _checks, _fails;

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _fails++; Console.WriteLine($"  FAIL: {what}"); }
    }

    private static float Rand(Random rng, float lo, float hi)
        => lo + (float)(rng.NextDouble() * (hi - lo));

    /// <summary>Случайная точка в сферическом слое [rLo, rHi] вокруг origin.</summary>
    private static Vec3 RandInShell(Random rng, Vec3 origin, float rLo, float rHi)
    {
        // равномерное направление на сфере
        double u = rng.NextDouble() * 2 - 1;
        double th = rng.NextDouble() * Math.PI * 2;
        double s = Math.Sqrt(1 - u * u);
        var dir = new Vec3((float)(s * Math.Cos(th)), (float)(s * Math.Sin(th)), (float)u);
        // равномерный радиус в слое (по объёму не важно — нам нужен слой)
        float r = rLo + (float)(rng.NextDouble() * (rHi - rLo));
        return origin + dir * r;
    }

    private static int Main()
    {
        var rng = new Random(20260729);
        const float NearR = 400f, MidR = 1200f, FarR = 4000f;

        // ------------------------------------------------ 1. плотная посадка: 100/200/100
        // Точки раскладываем по слоям так, чтобы при ПЕРВОМ запросе (нет истории,
        // вход по 0.9R) тиры вышли ровно 100/200/100:
        //   near: r <= 0.9*400 = 360
        //   mid : 360 < r <= 0.9*1200 = 1080
        //   far : 1080 < r <= 0.9*4000 = 3600
        Console.WriteLine("=== R2.1: плотная посадка 400 тел → тиры ~100/200/100 ===");
        var origin = new Vec3(6500f, 0f, 6500f);   // центр поля 13 км
        var pos = new Vec3[400];
        int idx = 0;
        for (int i = 0; i < 100; i++) pos[idx++] = RandInShell(rng, origin, 1f, 350f);
        for (int i = 0; i < 200; i++) pos[idx++] = RandInShell(rng, origin, 380f, 1050f);
        for (int i = 0; i < 100; i++) pos[idx++] = RandInShell(rng, origin, 1120f, 3550f);
        // 100 тел вне интереса (за 1.1*farR = 4400) — но у нас ровно 400, поэтому
        // «вне интереса» в этом наборе нет; отдельная проверка ниже (R2.4).

        var index = new SpatialIndex();
        var st = new InterestState();
        var near = new System.Collections.Generic.List<int>(512);
        var mid = new System.Collections.Generic.List<int>(512);
        var far = new System.Collections.Generic.List<int>(512);

        index.Rebuild(pos);
        Check(index.Count == 400, $"Count == 400 (факт {index.Count})");
        index.QueryTiers(origin, NearR, MidR, FarR, st, near, mid, far);
        Console.WriteLine($"  near={near.Count} mid={mid.Count} far={far.Count} none={400 - near.Count - mid.Count - far.Count}");
        Check(near.Count == 100, $"near == 100 (факт {near.Count})");
        Check(mid.Count == 200, $"mid == 200 (факт {mid.Count})");
        Check(far.Count == 100, $"far == 100 (факт {far.Count})");
        Check(near.Count + mid.Count + far.Count == 400, "все 400 тел попали в тиры");

        // ------------------------------------------------ 2. грид не теряет тела
        Console.WriteLine("=== R2.2: грид корректен — каждый слот ровно в одном тире ===");
        var seen = new int[400];
        foreach (int s in near) seen[s]++;
        foreach (int s in mid) seen[s]++;
        foreach (int s in far) seen[s]++;
        bool eachOnce = true;
        for (int s = 0; s < 400; s++) if (seen[s] != 1) { eachOnce = false; break; }
        Check(eachOnce, "каждый слот отнесён ровно в один тир");

        // ------------------------------------------------ 3. гистерезис на границе
        // Тело ровно на границе near (400 м). Без гистерезиса дрожание вокруг 400
        // м переключало бы near↔mid каждый пересчёт. С гистерезисом: вход по 360,
        // удержание до 440.
        Console.WriteLine("=== R2.3: гистерезис ±10 % на границе near (400 м) ===");
        var gp = new Vec3[1];
        var gst = new InterestState();
        var gindex = new SpatialIndex();
        var gn = new System.Collections.Generic.List<int>(8);
        var gm = new System.Collections.Generic.List<int>(8);
        var gf = new System.Collections.Generic.List<int>(8);

        // (а) тело на 400 м, истории нет → не проходит вход near (360), уходит в mid.
        gp[0] = origin + new Vec3(400f, 0, 0);
        gindex.Rebuild(gp);
        gst.Reset();
        gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf);
        Check(gm.Count == 1 && gn.Count == 0, "400 м без истории → mid (вход near по 360)");

        // (б) то же тело, но в прошлом оно было near → удерживается (400 <= 440).
        gst.LastTier[0] = (byte)SpatialIndex.TierNear;
        gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf);
        Check(gn.Count == 1 && gm.Count == 0, "400 м с историей near → остаётся near (удержание до 440)");

        // (в) тело на 441 м с историей near → за порогом удержания → падает в mid.
        gp[0] = origin + new Vec3(441f, 0, 0);
        gindex.Rebuild(gp);
        gst.LastTier[0] = (byte)SpatialIndex.TierNear;
        gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf);
        Check(gm.Count == 1 && gn.Count == 0, "441 м с историей near → падает в mid (за 440)");

        // (г) тело на 359 м без истории → проходит вход near.
        gp[0] = origin + new Vec3(359f, 0, 0);
        gindex.Rebuild(gp);
        gst.Reset();
        gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf);
        Check(gn.Count == 1, "359 м без истории → near (вход по 360)");

        // (д) дрожание на границе: тело ходит 395↔405 м, стартуя near → всегда near.
        gp[0] = origin + new Vec3(400f, 0, 0);
        gindex.Rebuild(gp);
        gst.Reset();
        gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf); // → mid
        gst.LastTier[0] = (byte)SpatialIndex.TierNear;                 // считаем его near
        int nearFlips = 0;
        for (int t = 0; t < 200; t++)
        {
            float r = (t % 2 == 0) ? 395f : 405f;
            gp[0] = origin + new Vec3(r, 0, 0);
            gindex.Rebuild(gp);
            gindex.QueryTiers(origin, NearR, MidR, FarR, gst, gn, gm, gf);
            if (gn.Count == 0) nearFlips++;
        }
        Check(nearFlips == 0, $"дрожание 395↔405 не роняет near (флипов {nearFlips})");

        // ------------------------------------------------ 4. тела вне интереса
        Console.WriteLine("=== R2.4: тела за 1.1*farR не попадают ни в один тир ===");
        var op = new Vec3[50];
        for (int i = 0; i < 50; i++) op[i] = RandInShell(rng, origin, 4500f, 6400f);
        var oindex = new SpatialIndex();
        var ost = new InterestState();
        oindex.Rebuild(op);
        oindex.QueryTiers(origin, NearR, MidR, FarR, ost, gn, gm, gf);
        Check(gn.Count == 0 && gm.Count == 0 && gf.Count == 0,
              $"50 тел за 4400 м → ни в одном тире (near {gn.Count} mid {gm.Count} far {gf.Count})");

        // ------------------------------------------------ 5. отсутствие аллокаций на запрос
        Console.WriteLine("=== R2.5: нет аллокаций на QueryTiers (переиспользование буферов) ===");
        // Прогрев: буферы уже выросли, LastTier уже нужного размера.
        index.Rebuild(pos);
        for (int w = 0; w < 50; w++)
            index.QueryTiers(origin, NearR, MidR, FarR, st, near, mid, far);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int q = 0; q < 1000; q++)
            index.QueryTiers(origin, NearR, MidR, FarR, st, near, mid, far);
        long after = GC.GetAllocatedBytesForCurrentThread();
        long perQuery = (after - before) / 1000;
        Console.WriteLine($"  аллокаций на запрос: {perQuery} Б");
        Check(perQuery == 0, $"0 аллоцированных байт на запрос (факт {perQuery})");

        // ------------------------------------------------ R7: гейтинг событий
        Console.WriteLine("=== R7: гейтинг событий попаданий по интересу ===");
        {
            var r7idx = new SpatialIndex();
            var r7st = new InterestState();
            var r7near = new System.Collections.Generic.List<int>(); var r7mid = new System.Collections.Generic.List<int>(); var r7far = new System.Collections.Generic.List<int>();

            // Тело A рядом с началом, тело B далеко (за farR).
            var r7pos = new Vec3[2] { new Vec3(10, 0, 0), new Vec3(1000, 0, 0) };
            r7idx.Rebuild(r7pos);
            // Запрос из начала: A в near, B вне farR (farR=500).
            r7idx.QueryTiers(new Vec3(0, 0, 0), 100, 300, 500, r7st, r7near, r7mid, r7far);
            Check(r7st.TierOf(0) == SpatialIndex.TierNear, "R7: свежий тир ближнего тела = Near");
            Check(r7st.TierOf(1) == SpatialIndex.TierNone, "R7: свежий тир тела за farR = None");

            // Гейтинг: событие A-B интересно пиру (сторона A в поле зрения).
            Check(SpatialIndex.PeerWantsEvent(r7st, 0, 1),
                  "R7: событие A-B шлём (сторона A в поле зрения)");
            // Событие B-B (обе стороны вне поля) — не шлём.
            Check(!SpatialIndex.PeerWantsEvent(r7st, 1, 1),
                  "R7: событие B-B НЕ шлём (обе стороны вне поля зрения)");
            // Слот вне индекса (-1) интереса не даёт.
            Check(!SpatialIndex.PeerWantsEvent(r7st, -1, 1),
                  "R7: отсутствующее тело (slot=-1) интереса не даёт");

            // Ключевой кейс: тело УШЛО из farR. Меняем местами: A далеко, B рядом.
            // CurTier A обязан стать None (BeginQuery обнулил прошлый publish), хотя
            // LastTier A остался Near (память гистерезиса). Гейтинг читает CurTier.
            r7pos[0] = new Vec3(1000, 0, 0);
            r7pos[1] = new Vec3(10, 0, 0);
            r7idx.Rebuild(r7pos);
            r7idx.QueryTiers(new Vec3(0, 0, 0), 100, 300, 500, r7st, r7near, r7mid, r7far);
            Check(r7st.TierOf(0) == SpatialIndex.TierNone,
                  "R7: ушедшее из farR тело — свежий тир None (гейтинг не пропустит)");
            Check(r7st.LastTier[0] != SpatialIndex.TierNone,
                  "R7: LastTier ушедшего тела НЕ None (память гистерезиса цела)");
            Check(!SpatialIndex.PeerWantsEvent(r7st, 0, 0),
                  "R7: событие про ушедшее тело НЕ шлём (CurTier свежий, не LastTier)");
            Check(r7st.TierOf(1) == SpatialIndex.TierNear,
                  "R7: пришедшее в near тело — свежий тир Near");
            Check(SpatialIndex.PeerWantsEvent(r7st, 1, 1),
                  "R7: событие про пришедшее тело шлём");
        }

        // ------------------------------------------------ 6. бюджет перестройки/запроса
        Console.WriteLine("=== R2.6: бюджет Rebuild + QueryTiers (мс) ===");
        var sw = new Stopwatch();
        const int iters = 2000;
        sw.Restart();
        for (int i = 0; i < iters; i++) index.Rebuild(pos);
        sw.Stop();
        double rebuildMs = sw.Elapsed.TotalMilliseconds / iters;

        sw.Restart();
        for (int i = 0; i < iters; i++)
            index.QueryTiers(origin, NearR, MidR, FarR, st, near, mid, far);
        sw.Stop();
        double queryMs = sw.Elapsed.TotalMilliseconds / iters;

        Console.WriteLine($"  Rebuild: {rebuildMs:F4} мс/вызов (400 тел, грид 400 м)");
        Console.WriteLine($"  QueryTiers: {queryMs:F4} мс/вызов");
        // Rebuild раз в 0.25 с → бюджет на вызов огромен (250 мс). QueryTiers на
        // репликационном тике 20 Гц → 50 мс на кадр; индекс тратит доли миллисекунды.
        Check(rebuildMs < 1.0, $"Rebuild < 1 мс (факт {rebuildMs:F4})");
        Check(queryMs < 1.0, $"QueryTiers < 1 мс (факт {queryMs:F4})");

        // ------------------------------------------------ итог
        Console.WriteLine();
        Console.WriteLine($"проверок: {_checks}, провалов: {_fails}");
        Console.WriteLine(_fails == 0 ? "R2 PASS" : "R2 FAIL");
        return _fails;
    }
}
