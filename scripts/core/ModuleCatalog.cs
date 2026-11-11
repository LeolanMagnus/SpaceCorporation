using Godot;

namespace SpaceCorporation;

/// <summary>Вид модуля = слот в <see cref="ShipLoadout"/> (Этап 6).</summary>
public enum ModuleKind { Rcs, Engine, Radar, Capacitor, Reactor }

/// <summary>
/// Спецификация одного модуля из каталога (Этап 6). Чистые данные, без узлов:
/// все скоростные характеристики — ЭФФЕКТИВНЫЕ ускорения в м/с² и рад/с²
/// (уже «для корпуса 1200 кг»), поэтому loadout их просто складывает/берёт
/// максимумом и не умножает на массу (Р2 плана: единицы — ускорения).
///
/// PowerDraw/PowerOutput — условные единицы мощности (задел под питание:
/// Powered = Output / Σ Draw, клампится в [0,1] и множит ускорения).
/// RadarRange — метры; в FlightParams не участвует, но живёт в том же
/// loadout'е (один источник характеристик корабля).
/// </summary>
public readonly struct ModuleSpec
{
    public readonly string Id;
    public readonly string Name;
    public readonly ModuleKind Kind;
    public readonly int Tier;             // 1..3 — «улучшение путём замены модуля»

    // Маршевый двигатель (Engine):
    public readonly float ThrustAccel;    // м/с² — тяговое ускорение
    public readonly float MaxLinearSpeed; // м/с  — потолок стрейфа/линейной скорости
    public readonly float BoostGain;      // +к форсажу (BoostMultiplier = 1 + Σ Gain… нет: см. ShipLoadout)

    // РСУ (Rcs):
    public readonly float AngularAccel;    // рад/с²
    public readonly float MaxAngularSpeed; // рад/с

    // Реактор / батарея конденсаторов:
    public readonly float PowerOutput;     // усл. ед.
    public readonly float PowerDraw;       // усл. ед.

    // Радар:
    public readonly float RadarRange;      // м

    public ModuleSpec(string id, string name, ModuleKind kind, int tier,
                      float thrustAccel = 0f, float maxLinearSpeed = 0f, float boostGain = 0f,
                      float angularAccel = 0f, float maxAngularSpeed = 0f,
                      float powerOutput = 0f, float powerDraw = 0f, float radarRange = 0f)
    {
        Id = id; Name = name; Kind = kind; Tier = tier;
        ThrustAccel = thrustAccel; MaxLinearSpeed = maxLinearSpeed; BoostGain = boostGain;
        AngularAccel = angularAccel; MaxAngularSpeed = maxAngularSpeed;
        PowerOutput = powerOutput; PowerDraw = powerDraw; RadarRange = radarRange;
    }
}

/// <summary>
/// Каталог модулей Этапа 6: 5 слотов × 2–3 уровня. Старые Mk I подобраны так,
/// чтобы в сборе дать ровно те числа, что сегодня хардкодит Ship._Ready
/// (75 м/с², 33.33 рад/с², лимит вращения 6 рад/с) — тест п.11 сверяет это
/// побитово. Уровни выше — умножение характеристик: «задел под улучшение».
/// </summary>
public static class ModuleCatalog
{
    private static readonly System.Collections.Generic.Dictionary<string, ModuleSpec> Specs = new()
    {
        // ── Маршевый двигатель: разгон / потолок линейной скорости ────────────
        ["engine_ics_mk1"] = new("engine_ics_mk1", "Engine ICS Mk I", ModuleKind.Engine, 1,
            thrustAccel: 75f, maxLinearSpeed: 260f, boostGain: 0.5f, powerDraw: 2f),
        ["engine_ics_mk2"] = new("engine_ics_mk2", "Engine ICS Mk II", ModuleKind.Engine, 2,
            thrustAccel: 110f, maxLinearSpeed: 340f, boostGain: 0.7f, powerDraw: 3f),
        ["engine_ics_mk3"] = new("engine_ics_mk3", "Engine ICS Mk III", ModuleKind.Engine, 3,
            thrustAccel: 150f, maxLinearSpeed: 420f, boostGain: 1.0f, powerDraw: 4f),

        // ── РСУ: угловое ускорение / потолок вращения ────────────────────────
        // 40000/1200 = 33.333 рад/с² — сегодняшнее число Ship (Р2: делим массу
        // на этом месте НЕ будем, берём готовое ускорение того же размера).
        ["rcs_ics_mk1"] = new("rcs_ics_mk1", "RCS ICS Mk I", ModuleKind.Rcs, 1,
            angularAccel: 40000f / 1200f, maxAngularSpeed: 6f, powerDraw: 1f),
        ["rcs_ics_mk2"] = new("rcs_ics_mk2", "RCS ICS Mk II", ModuleKind.Rcs, 2,
            angularAccel: 60f, maxAngularSpeed: 9f, powerDraw: 1.5f),
        ["rcs_ics_mk3"] = new("rcs_ics_mk3", "RCS ICS Mk III", ModuleKind.Rcs, 3,
            angularAccel: 90f, maxAngularSpeed: 12f, powerDraw: 2f),

        // ── Радар: только Range (на Этап 6 — задел) ──────────────────────────
        ["radar_rn1_mk1"] = new("radar_rn1_mk1", "Radar RN-1", ModuleKind.Radar, 1,
            radarRange: 800f, powerDraw: 1f),
        ["radar_rn2_mk2"] = new("radar_rn2_mk2", "Radar RN-2", ModuleKind.Radar, 2,
            radarRange: 2000f, powerDraw: 1.5f),

        // ── Батарея конденсаторов: прирост форсажа (Boost) ───────────────────
        ["cap_c1_mk1"] = new("cap_c1_mk1", "Capacitor C-1", ModuleKind.Capacitor, 1,
            boostGain: 0.5f, powerDraw: 1f),
        ["cap_c2_mk2"] = new("cap_c2_mk2", "Capacitor C-2", ModuleKind.Capacitor, 2,
            boostGain: 1.0f, powerDraw: 1.5f),

        // ── Реактор: мощность (Powered = Output/Σ Draw) ──────────────────────
        // Mk1 покрывает все Mk1-модули (2+1+1+1 = 5) с запасом.
        ["reactor_rt1_mk1"] = new("reactor_rt1_mk1", "Reactor RT-1", ModuleKind.Reactor, 1,
            powerOutput: 6f),
        ["reactor_rt2_mk2"] = new("reactor_rt2_mk2", "Reactor RT-2", ModuleKind.Reactor, 2,
            powerOutput: 12f),
    };

    /// <summary>Спецификация по id; false + дефолтный spec, если id неизвестен.</summary>
    public static bool TryFind(string id, out ModuleSpec spec) => Specs.TryGetValue(id ?? "", out spec);

    public static ModuleSpec Find(string id)
    {
        if (TryFind(id, out ModuleSpec spec)) return spec;
        GD.PrintErr($"ModuleCatalog: неизвестный модуль '{id}'");
        return default;
    }

    /// <summary>Базовая комплектация = сегодняшние числа Ship (тест п.11).</summary>
    public static string DefaultFor(ModuleKind kind) => kind switch
    {
        ModuleKind.Engine => "engine_ics_mk1",
        ModuleKind.Rcs => "rcs_ics_mk1",
        ModuleKind.Radar => "radar_rn1_mk1",
        ModuleKind.Capacitor => "cap_c1_mk1",
        ModuleKind.Reactor => "reactor_rt1_mk1",
        _ => "",
    };
}

// ══════════════════════════════════════════════════════════════════════════
// EVA: категория модулей скафандра (eva-plan, Б3 — ЗАДЕЛ, без крафта и цен)
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Вид модуля скафандра (eva-plan Б3). Отдельный от <see cref="ModuleKind"/>
/// enum: слоты корабля и слоты скафандра — разные комплекты, их нельзя
/// перепутать одним словарём. Порядок чисел фиксирован: он попадает в битовую
/// маску <see cref="SuitState.ActiveSuitModules"/>, которую возит сеть (Блок D).
/// </summary>
public enum SuitModuleKind { Heat = 0, Light = 1, Jetpack = 2, Boots = 3 }

/// <summary>Бит маски <see cref="SuitState.ActiveSuitModules"/> для вида модуля.</summary>
public static class SuitModules
{
    public static uint BitOf(SuitModuleKind kind) => 1u << (int)kind;
    public static bool Has(uint mask, SuitModuleKind kind) => (mask & BitOf(kind)) != 0u;
    public const int Count = 4;

    /// <summary>Сколько слотов занято в маске (для HUD «4/4 модуля»).</summary>
    public static int CountActive(uint mask)
    {
        int n = 0;
        for (int i = 0; i < Count; i++)
            if ((mask & (1u << i)) != 0u) n++;
        return n;
    }
}

/// <summary>
/// Спецификация модуля скафандра (чистые данные, как <see cref="ModuleSpec"/>).
/// Единицы — проценты расходников скафандра:
///  • DrawPerMin     — Δ расхода батареи, %/мин ОТНОСИТЕЛЬНО базового комплекта
///                     (0 у базовых обогрева/фонаря: их постоянный расход уже
///                     задают константы <see cref="SuitModel.HeatDrawPerMin"/> и
///                     <see cref="SuitModel.LightDrawPerMin"/>, поэтому
///                     двойного счёта нет; отрицательное = экономия);
///  • MassTotal      — реактивная масса, которую даёт этот джетпак, %
///                     (0 = модуль массы не даёт; потолок бака — сумма по слотам);
///  • RechargePerMin — 0 = «нет мнения», действует базовое
///                     <see cref="SuitModel.RechargePerMin"/>.
/// Значения ниже — заглушки-задел: реальные апгрейды (крафт, цены, деревья) на
/// этой стадии сознательно НЕ делаются (eva-plan §6).
/// </summary>
public readonly struct SuitModuleSpec
{
    public readonly string Id;
    public readonly string Name;
    public readonly SuitModuleKind Kind;
    public readonly int Tier;
    public readonly float DrawPerMin;
    public readonly float MassTotal;
    public readonly float RechargePerMin;

    public SuitModuleSpec(string id, string name, SuitModuleKind kind, int tier,
                          float drawPerMin = 0f, float massTotal = 0f, float rechargePerMin = 0f)
    {
        Id = id; Name = name; Kind = kind; Tier = tier;
        DrawPerMin = drawPerMin; MassTotal = massTotal; RechargePerMin = rechargePerMin;
    }
}

/// <summary>
/// Каталог модулей скафандра: базовый комплект (=Mk I, ровно сегодняшние
/// константы `SuitModel`) + по одному условному уровню выше, чтобы структура
/// была обкатана до появления апгрейдов. Никаких цен и деревьев.
/// </summary>
public static class SuitModuleCatalog
{
    private static readonly System.Collections.Generic.Dictionary<string, SuitModuleSpec> Specs = new()
    {
        // Базовый жизнеобеспечивающий контур: его расход = константы SuitModel.
        ["heat_standard"] = new("heat_standard", "Life Support Standard", SuitModuleKind.Heat, 1),
        ["heat_insulated_mk2"] = new("heat_insulated_mk2", "Life Support Insulated Mk II",
            SuitModuleKind.Heat, 2, drawPerMin: -1f), // 4 → 3 %/мин

        // Фонарь: базовый = SuitModel.LightDrawPerMin; Mk II — мощнее и дороже.
        ["light_standard"] = new("light_standard", "Helmet Lamp Standard", SuitModuleKind.Light, 1),
        ["light_flood_mk2"] = new("light_flood_mk2", "Helmet Lamp Flood Mk II",
            SuitModuleKind.Light, 2, drawPerMin: 4f), // 12 → 16 %/мин

        // Джетпак: единственный источник реактивной массы (ТЗ: 100 % = полный бак).
        ["jetpack_mk1"] = new("jetpack_mk1", "Jetpack Mk I", SuitModuleKind.Jetpack, 1,
            massTotal: 100f),

        // Ботинки: пассивный модуль (расхода нет — сцепление магнитное).
        ["boots_mk1"] = new("boots_mk1", "Mag Boots Mk I", SuitModuleKind.Boots, 1),
    };

    public static bool TryFind(string? id, out SuitModuleSpec spec) =>
        Specs.TryGetValue(id ?? "", out spec);

    public static SuitModuleSpec Find(string id)
    {
        if (TryFind(id, out SuitModuleSpec spec)) return spec;
        GD.PrintErr($"SuitModuleCatalog: неизвестный модуль скафандра '{id}'");
        return default;
    }

    /// <summary>Базовая комплектация = сегодняшние числа SuitModel (ТЗ-режим).</summary>
    public static string DefaultFor(SuitModuleKind kind) => kind switch
    {
        SuitModuleKind.Heat => "heat_standard",
        SuitModuleKind.Light => "light_standard",
        SuitModuleKind.Jetpack => "jetpack_mk1",
        SuitModuleKind.Boots => "boots_mk1",
        _ => "",
    };
}
