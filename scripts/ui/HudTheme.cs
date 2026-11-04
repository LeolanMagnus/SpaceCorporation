using Godot;

namespace SpaceCorporation;

/// <summary>
/// Единая палитра и стили HUD в духе X4 (hud-plan.md §4): полупрозрачные
/// панели с тонкой сине-стальной рамкой, сине-белая гамма текста.
/// Все цвета — отсюда, чтобы вид был «x4», а не дефолтный Godot.
/// </summary>
public static class HudTheme
{
    /// <summary>Фон панели: почти чёрный с синевой, полупрозрачный (#10161ecc).</summary>
    public static readonly Color PanelBg = new(0.063f, 0.086f, 0.110f, 0.80f);

    /// <summary>Тонкая рамка панели (#4a6a8a).</summary>
    public static readonly Color PanelBorder = new(0.290f, 0.416f, 0.541f, 0.90f);

    /// <summary>Основной текст — светлый сине-белый.</summary>
    public static readonly Color TextPrimary = new(0.85f, 0.90f, 0.95f);

    /// <summary>Второстепенный текст (подписи, максимумы) — серый.</summary>
    public static readonly Color TextDim = new(0.55f, 0.60f, 0.66f);

    /// <summary>Акцент — голубой (цифры, заголовки).</summary>
    public static readonly Color TextAccent = new(0.44f, 0.66f, 1.0f);

    // HP-состояния (H2): >60 % зелёный, 30–60 жёлтый, <30 красный.
    public static readonly Color HpGood = new(0.35f, 0.85f, 0.45f);
    public static readonly Color HpWarn = new(0.95f, 0.80f, 0.30f);
    public static readonly Color HpCrit = new(0.95f, 0.35f, 0.30f);

    // Фракции (H4, hud-plan.md §4): Player — синий, Ally — зелёный,
    // Hostile — красный, Neutral — серый. Игрок ВСЕГДА синий (§4).
    public static readonly Color FactionPlayer = new(0.30f, 0.60f, 1.00f);
    public static readonly Color FactionAlly = new(0.35f, 0.85f, 0.45f);
    public static readonly Color FactionHostile = new(0.95f, 0.35f, 0.30f);
    public static readonly Color FactionNeutral = new(0.65f, 0.68f, 0.72f);

    /// <summary>Цвет фракции (byte из реплики: FactionValue у кораблей/мишеней).</summary>
    public static Color FactionColor(byte factionValue) =>
        (Faction)factionValue switch
        {
            Faction.Player => FactionPlayer,
            Faction.Ally => FactionAlly,
            Faction.Hostile => FactionHostile,
            _ => FactionNeutral,
        };

    // Заглушка радара (H5 заменит radar-plan).
    public static readonly Color RadarBg = new(0.04f, 0.07f, 0.10f, 0.85f);
    public static readonly Color RadarBorder = new(0.35f, 0.55f, 0.75f, 0.85f);

    /// <summary>Стиль панели HUD: фон PanelBg, рамка 1 px PanelBorder.</summary>
    public static StyleBoxFlat PanelStyle()
    {
        var sb = new StyleBoxFlat
        {
            BgColor = PanelBg,
            BorderColor = PanelBorder,
        };
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(8);
        return sb;
    }

    /// <summary>Стиль внутренней плашки (слоты-заглушки, радар).</summary>
    public static StyleBoxFlat SlotStyle(Color bg, Color border)
    {
        var sb = new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
        };
        sb.SetBorderWidthAll(1);
        sb.SetContentMarginAll(6);
        return sb;
    }

    /// <summary>Цвет полосы HP по доле прочности (H2).</summary>
    public static Color HpColor(float ratio) =>
        ratio > 0.60f ? HpGood : ratio >= 0.30f ? HpWarn : HpCrit;
}
