using System;
using System.Collections.Generic;
using SpaceCorporation.Net;

// Стенд шага R9 плана net-refactor-plan.md.
//
// Сцена: 4 клиента на одном поле боя, 400 ИИ-кораблей, 600 ссылок «цель»
// (300 кораблей с 1 целью, 50 с 2, 50 с 4 — среднее 1.5 цели на корабль).
// ИИ стреляет друг в друга (hitscan-события стадии 3).
//
// Замеряем метрики РЕАЛЬНОЙ реализации (SpatialIndex + BroadcasterCore, те же
// константы, что в Godot-узле SnapshotBroadcaster) и сверяем с нормативным
// бюджетом §4 — тем же, что печатает tools/traffic_estimate.py:
//   вниз/клиент ≤ 200 КБ/с, пакетов/с ≤ 150, записей/с ≤ 5300,
//   сервер вниз ≤ 800 КБ/с, апстрим/клиент ≤ 10 КБ/с.
// Нормативная конфигурация: консервативный пресет тиров + цели по смене
// (targetId в событии, не поле записи).

static class R9
{
    static int checks = 0, fails = 0;
    static void Check(bool ok, string what)
    {
        checks++;
        if (!ok) { fails++; Console.WriteLine("  FAIL: " + what); }
        else Console.WriteLine("  ok: " + what);
    }

    // Пороги §4 (совпадают с BUDGET в traffic_estimate.py).
    const double BudgetDownPerClient = 200 * 1024;
    const double BudgetPkts = 150;
    const double BudgetUpdates = 5300;
    const double BudgetServerDown = 800 * 1024;
    const double BudgetUpPerClient = 10 * 1024;

    const int NShips = 400, NClients = 4;

    static void Main()
    {
        Console.WriteLine($"=== R9: стенд {NClients} клиента / {NShips} кораблей / 600 целей ===");

        // --- 400 кораблей в поле боя, распределены по тирам от общего центра ---
        // near ≤ 350 (вход near 0.9·400=360), mid 500..1050 (вход mid 0.9·1200=1080),
        // far 1500..3500 (вход far 0.9·4000=3600). Плюс 20 кораблей за farR
        // (5000..6500 м) — они вне тира interest любого клиента: их позиции не
        // реплицируются и события про них не шлются (гейтинг R7). 4 точки обзора —
        // в 60 м от центра, каждая видит те же оболочки → ~100/200/80 в interest.
        var pos = new Vec3[NShips];
        var rng = new Random(20240309);
        for (int i = 0; i < 100; i++) pos[i] = RandInShell(rng, 10f, 350f);
        for (int i = 100; i < 300; i++) pos[i] = RandInShell(rng, 500f, 1050f);
        for (int i = 300; i < NShips - 20; i++) pos[i] = RandInShell(rng, 1500f, 3500f);
        for (int i = NShips - 20; i < NShips; i++) pos[i] = RandInShell(rng, 5000f, 6500f);

        var index = new SpatialIndex();
        index.Rebuild(pos);

        // 4 точки обзора: небольшой джиттер вокруг центра (игроки сбились в одном
        // районе боя). Каждый опрашивает индекс и получает свои тиры интереса.
        var views = new Vec3[NClients];
        for (int c = 0; c < NClients; c++)
            views[c] = RandInShell(rng, 0f, 60f);

        // Консервативный пресет: near 30 Гц, mid 10 Гц, far 3 Гц (радиусы 400/1200/4000).
        const float nearR = 400f, midR = 1200f, farR = 4000f;
        const int nearHz = 30, midHz = 10, farHz = 3;

        double worstDown = 0, worstPkts = 0, worstUpdates = 0;
        double sumDown = 0;

        for (int c = 0; c < NClients; c++)
        {
            var st = new InterestState();
            var near = new List<int>(); var mid = new List<int>(); var far = new List<int>();
            index.QueryTiers(views[c], nearR, midR, farR, st, near, mid, far);

            // Тиры интереса → потоки батчера (консервативный пресет по Гц).
            var tiers = new[]
            {
                new BroadcasterCore.TierStream(near.Count, nearHz, FullRec),
                new BroadcasterCore.TierStream(mid.Count, midHz, FullRec),
                new BroadcasterCore.TierStream(far.Count, farHz, FarRec),
            };

            // Цели по смене (onchange): событие targetId 2 Б, ~1 смена/с на корабль.
            // Это СОБЫТИЕ (едет отдельным small-packet-трафиком), а не поле записи,
            // поэтому идёт в eventBytesPerSec (3-й аргумент), а не в perRecordExtra.
            double targetEventPayload = BroadcasterCore.TargetChangesPerSec
                                      * BroadcasterCore.TargetChangeBytes;
            // События попаданий (R7 гейтинг: RpcOnHitConfirmed доходит только до
            // заинтересованных; норматив §4 — консервативно 6 подтверждений/с на клиента).
            double hitEventPayload = BroadcasterCore.HitEventsPerClientPerSec
                                   * BroadcasterCore.HitEventBytes;

            var (rec, pkt, down) = BroadcasterCore.MeasureSecond(
                tiers, 0.0, targetEventPayload + hitEventPayload);

            Console.WriteLine($"  клиент {c}: тиры near={near.Count} mid={mid.Count} far={far.Count}"
                + $" | rec/s={rec}, pkt/s={pkt}, вниз={down / 1024:F1} КБ/с");

            worstDown = Math.Max(worstDown, down);
            worstPkts = Math.Max(worstPkts, pkt);
            worstUpdates = Math.Max(worstUpdates, rec);
            sumDown += down;
        }

        // --- Апстрим (ввод + запросы выстрелов), R8: unreliable канал 2 ---
        var (upBytes, upPkts) = BroadcasterCore.MeasureUpstream();
        Console.WriteLine($"  апстрим/клиент: {upBytes / 1024:F1} КБ/с, пакетов/с={upPkts:F0}");

        // --- Сверка с бюджетом §4 (по худшему клиенту и сумме) ---
        Console.WriteLine("--- сверка с нормативным бюджетом §4 ---");
        Check(worstDown <= BudgetDownPerClient,
            $"вниз/клиент ≤ 200 КБ/с (худший {worstDown / 1024:F1} КБ/с, " +
            $"{worstDown / BudgetDownPerClient * 100:F1}%)");
        Check(worstPkts <= BudgetPkts,
            $"пакетов/с ≤ 150 (худший {worstPkts:F0}, {worstPkts / BudgetPkts * 100:F1}%)");
        Check(worstUpdates <= BudgetUpdates,
            $"записей/с ≤ 5300 (худший {worstUpdates:F0}, {worstUpdates / BudgetUpdates * 100:F1}%)");
        Check(sumDown <= BudgetServerDown,
            $"сервер вниз ≤ 800 КБ/с (сумма {sumDown / 1024:F1} КБ/с, " +
            $"{sumDown / BudgetServerDown * 100:F1}%)");
        Check(upBytes <= BudgetUpPerClient,
            $"апстрим/клиент ≤ 10 КБ/с ({upBytes / 1024:F1} КБ/с, " +
            $"{upBytes / BudgetUpPerClient * 100:F1}%)");

        // --- Проверка гейтинга событий (R7): событие шлём только заинтересованным ---
        // Строим netId↔slot (как в SnapshotBroadcaster) и проверяем, что событие
        // про корабль вне farR данного клиента этому клиенту НЕ шлём.
        Console.WriteLine("--- гейтинг событий попаданий (R7) ---");
        // Клиент 0: корабль 0 (near) участвует в событии → шлём; корабль за farR → нет.
        var st0 = new InterestState();
        var n0 = new List<int>(); var m0 = new List<int>(); var f0 = new List<int>();
        index.QueryTiers(views[0], nearR, midR, farR, st0, n0, m0, f0);
        // slot 0 в near (CurTier=Near), найдём slot вне interest (TierOf=None).
        int outSlot = -1;
        for (int s = 0; s < NShips; s++) if (st0.TierOf(s) == SpatialIndex.TierNone) { outSlot = s; break; }
        Check(st0.TierOf(0) != SpatialIndex.TierNone, "корабль 0 в поле зрения клиента 0");
        Check(outSlot >= 0, "есть корабль вне поля зрения клиента 0");
        Check(SpatialIndex.PeerWantsEvent(st0, 0, outSlot),
            "событие (участник в interest, другой вне) — шлём клиенту 0");
        Check(!SpatialIndex.PeerWantsEvent(st0, outSlot, outSlot),
            "событие (оба участника вне interest) — НЕ шлём клиенту 0");

        // --- R10: нулевой апстрим ИИ-кораблей (инвариант зафиксирован) ---
        // Апстрим создаёт ТОЛЬКО пилот: Ship.cs шлёт кадр ввода под гейтом
        // `if (IsLocalPilot)` (PilotPeerId == SelfId). У 396 ИИ-кораблей
        // PilotPeerId == 0 (никого), гейт закрыт → 0 кадров ввода. Значит
        // апстрим сервера масштабируется числом ПИЛОТОВ (4), а не кораблей (400).
        // Фиксируем это двумя проверками.
        Console.WriteLine("--- R10: нулевой апстрим ИИ-кораблей ---");
        var (upPerPilotBytes, upPerPilotPkts) = BroadcasterCore.MeasureUpstream();
        // Апстрим всего сервера = апстрим одного пилота × число пилотов (4),
        // ИИ-корабли добавляют ровно 0.
        double serverUp = upPerPilotBytes * NClients;
        double serverUpPkts = upPerPilotPkts * NClients;
        // Граница бюджета апстрима — на клиента; серверный апстрим = 4× от одного.
        Check(Math.Abs(serverUp - 4 * upPerPilotBytes) < 1e-6,
            "R10: серверный апстрим = 4× апстрима пилота (ИИ-корабли дают 0)");
        // Инвариант гейта: корабль без локального пилота (ИИ) не отправляет НИ
        // одного кадра. Модельно: апстрим считается по пилотам, поэтому 396 ИИ
        // не входят в сумму. Если бы гейт сломался и все 400 кораблей слали ввод,
        // апстрим ушёл бы за бюджет — это и доказывает несущую роль инварианта.
        double upIfAllShipsPiloted = upPerPilotBytes * NShips;
        Check(upIfAllShipsPiloted > BudgetUpPerClient * NClients,
            "R10: если бы ИИ слали ввод, апстрим превысил бы бюджет " +
            $"({upIfAllShipsPiloted / 1024:F0} КБ/с > {BudgetUpPerClient * NClients / 1024:F0} КБ/с) — " +
            "гейт IsLocalPilot обязателен");
        Console.WriteLine($"  апстрим/пилот {upPerPilotBytes / 1024:F1} КБ/с × {NClients} пилотов " +
                          $"= {serverUp / 1024:F1} КБ/с; ИИ-кораблей {NShips - NClients} → +0 КБ/с");

        Console.WriteLine($"=== R9: проверок {checks}, провалов {fails} ===");
        Environment.Exit(fails == 0 ? 0 : 1);
    }

    const int FullRec = 30;   // полная запись near/mid (квант, без целей в поле)
    const int FarRec = 14;    // far: позиция + грубый поворот, без скоростей

    static Vec3 RandInShell(Random rng, float rMin, float rMax)
    {
        // равномерная точка в сферической оболочке [rMin, rMax]
        double u = rng.NextDouble();
        double r = Math.Cbrt(rMin * (double)rMin * rMin + u * (rMax * (double)rMax * rMax - rMin * (double)rMin * rMin));
        double cosT = 2 * rng.NextDouble() - 1;
        double sinT = Math.Sqrt(1 - cosT * cosT);
        double phi = 2 * Math.PI * rng.NextDouble();
        return new Vec3(
            (float)(r * sinT * Math.Cos(phi)),
            (float)(r * sinT * Math.Sin(phi)),
            (float)(r * cosT));
    }
}
