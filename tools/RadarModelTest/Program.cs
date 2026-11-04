using System;
using System.Collections.Generic;
using SpaceCorporation.Net;
using SpaceCorporation.UI;

// Тесты этапа R1 (план radar-plan.md): RadarModel — фильтр радиуса с
// гистерезисом ±10 %, поворот в систему координат радара, fade исчезнувших,
// сортировка по дистанции, экстраполяция между сборками. Гоняются БЕЗ Godot:
//   dotnet run --project tools/RadarModelTest
// Код возврата 0 = все проверки пройдены; иначе — число провалов.

internal static class RadarModelTest
{
    private static int _checks, _fails;

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok) { _fails++; Console.WriteLine($"  FAIL: {what}"); }
    }

    private static RadarContact C(int id, float x, float y, float z,
                                  float vx = 0, float vy = 0, float vz = 0,
                                  byte faction = 0, byte shape = RadarShape.Ship)
        => new() { NetId = id, RelPos = new Vec3(x, y, z),
                   RelVel = new Vec3(vx, vy, vz), Faction = faction, Shape = shape };

    private static int Main()
    {
        const float R = 2000f;

        // ------------------------------------------------ 1. фильтр радиуса
        Console.WriteLine("=== R1.1: фильтр радиуса (вход 0.9R) ===");
        var m = new RadarModel { Range = R };
        var contacts = new List<RadarContact>
        {
            C(1, 300, 0, 0),                       // внутри
            C(2, 0, 0, 2500),                      // снаружи (>1.1R)
            C(3, 1850, 0, 0),                      // 1850 < 1800? нет: 1850 > 1800 → новый не входит
            C(4, 2200, 0, 0),                      // ровно в гистерезисной полосе (0.9R..1.1R)
        };
        m.Sync(contacts);
        m.Advance(0f, 0f);
        Check(m.FindBlip(1) != null, "контакт 1 (300 м) виден");
        Check(m.FindBlip(2) == null, "контакт 2 (2500 м) не виден");
        Check(m.FindBlip(3) == null, "новый контакт 3 (1850 м > 0.9R=1800) не входит");
        Check(m.FindBlip(4) == null, "новый контакт 4 (2200 м) не входит");
        Check(m.LiveCount == 1, $"LiveCount == 1 (факт {m.LiveCount})");

        // ------------------------------------------------ 2. гистерезис ±10 %
        Console.WriteLine("=== R1.2: гистерезис — видимый держится до 1.1R ===");
        m = new RadarModel { Range = R };
        m.Sync(new List<RadarContact> { C(10, 1000, 0, 0) });
        m.Advance(0f, 0f);
        Check(m.FindBlip(10) != null, "контакт 10 вошёл (1000 м)");
        // улетел на 2050 м: >1.0R, но <1.1R — должен держаться
        m.Sync(new List<RadarContact> { C(10, 2050, 0, 0) });
        m.Advance(0f, 0f);
        Check(m.FindBlip(10) != null, "контакт 10 удержан на 2050 м (<1.1R)");
        // затем за 1.1R — выходит
        m.Sync(new List<RadarContact> { C(10, 2300, 0, 0) });
        m.Advance(0f, 0f);
        var b10 = m.FindBlip(10);
        Check(b10 != null && b10.Stale, "контакт 10 (>1.1R) помечен stale");
        // и гаснет за FadeTime
        m.Sync(new List<RadarContact>());
        m.Advance(RadarModel.FadeTime * 0.5f, 0f);
        b10 = m.FindBlip(10);
        Check(b10 != null && b10.Fade > 0f && b10.Fade < 1f,
              $"fade на середине (факт {b10?.Fade:F2})");
        m.Advance(RadarModel.FadeTime, 0f);
        Check(m.FindBlip(10) == null, "контакт 10 удалён после fade");

        // ------------------------------------------------ 3. поворот на курс
        Console.WriteLine("=== R1.3: поворот: контакт «впереди» при yaw=90° уходит вбок ===");
        m = new RadarModel { Range = R };
        m.Sync(new List<RadarContact> { C(20, 0, 0, -1000) }); // на -Z
        m.Advance(0f, 0f);   // forward=(sin0,cos0)=(0,1)→+Z? нет: при yaw=0 forward=+Z
        var bp = m.FindBlip(20);
        Check(bp != null && MathF.Abs(bp.ScreenX) < 1f && bp.ScreenY < -999f,
              $"yaw=0: -Z контакт в ScreenY={bp?.ScreenY:F1} (ожидание -1000)");
        m.Advance(0f, MathF.PI / 2f); // yaw=90°: forward=(1,0)→+X; -Z уходит вбок
        bp = m.FindBlip(20);
        Check(bp != null && MathF.Abs(bp.ScreenY) < 1f && bp.ScreenX > 999f,
              $"yaw=90°: ScreenX={bp?.ScreenX:F1} (ожидание +1000), ScreenY={bp?.ScreenY:F1}");

        // Контакт «впереди по курсу» при yaw=0 (forward +Z): rel (0,0,1000) → ScreenY=+1000
        m.Sync(new List<RadarContact> { C(21, 0, 50, 1000) });
        m.Advance(0f, 0f);
        bp = m.FindBlip(21);
        Check(bp != null && bp.ScreenY > 999f && MathF.Abs(bp.HeightOffset - 50f) < 1f,
              $"высота ΔY=50 в HeightOffset (факт {bp?.HeightOffset:F1})");

        // ------------------------------------------------ 4. сортировка
        Console.WriteLine("=== R1.4: сортировка — дальние первыми, ближние поверх ===");
        m = new RadarModel { Range = R };
        m.Sync(new List<RadarContact>
        {
            C(30, 1800, 0, 0),
            C(31, 200, 0, 0),
            C(32, 900, 0, 0),
        });
        m.Advance(0f, 0f);
        Check(m.Blips.Count == 3, $"3 блипа (факт {m.Blips.Count})");
        Check(m.Blips[0].Contact.NetId == 30 && m.Blips[1].Contact.NetId == 32
              && m.Blips[2].Contact.NetId == 31, "порядок 30(1800) → 32(900) → 31(200)");

        // ------------------------------------------------ 5. экстраполяция
        Console.WriteLine("=== R1.5: экстраполяция RelVel·Δt между сборками ===");
        m = new RadarModel { Range = R };
        m.Sync(new List<RadarContact> { C(40, 1000, 0, 0, 0, 0, 50) });
        m.Advance(0.1f, 0f);
        bp = m.FindBlip(40);
        Check(bp != null && MathF.Abs(bp.Contact.RelPos.X - 1000f) < 0.01f
              && MathF.Abs(bp.Contact.RelPos.Z - 5f) < 0.01f,
              $"после 0.1 с при vz=50: rel={bp?.Contact.RelPos} (ожидание Z=5)");
        // ещё кадр без новой сборки — экстраполяция накапливается
        m.Advance(0.1f, 0f);
        bp = m.FindBlip(40);
        Check(bp != null && MathF.Abs(bp.Contact.RelPos.Z - 10f) < 0.01f,
              $"накопление: после 0.2 с Z={bp?.Contact.RelPos.Z:F1} (ожидание 10)");

        // ------------------------------------------------ 6. возврат контакта
        Console.WriteLine("=== R1.6: вернувшийся контакт оживает (fade=1) ===");
        m = new RadarModel { Range = R };
        m.Sync(new List<RadarContact> { C(50, 1000, 0, 0) });
        m.Advance(0f, 0f);
        m.Sync(new List<RadarContact>());
        m.Advance(0.1f, 0f);
        bp = m.FindBlip(50);
        Check(bp != null && bp.Stale && bp.Fade < 1f, "блип гаснет");
        m.Sync(new List<RadarContact> { C(50, 1000, 0, 0) });
        m.Advance(0f, 0f);
        bp = m.FindBlip(50);
        Check(bp != null && !bp.Stale && bp.Fade == 1f, "вернулся — живой, fade=1");
        Check(m.LiveCount == 1, $"LiveCount==1 после возврата (факт {m.LiveCount})");

        // ------------------------------------------------ 7. R2-критерий плана
        Console.WriteLine("=== R1.7: критерий R2 — 300 и 2500 м → 1 блип в R=2000 ===");
        m = new RadarModel { Range = 2000f };
        m.Sync(new List<RadarContact> { C(60, 300, 0, 0), C(61, 0, 0, 2500) });
        m.Advance(0f, 0f);
        int inRange = 0;
        foreach (var bl in m.Blips) if (!bl.Stale) inRange++;
        Check(inRange == 1, $"ровно 1 блип в радиусе 2000 (факт {inRange})");

        Console.WriteLine($"=== RadarModelTest: {_checks} проверок, {_fails} провалов ===");
        return _fails;
    }
}
