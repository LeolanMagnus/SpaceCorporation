using Godot;

namespace SpaceCorporation;

/// <summary>
/// «Скин» экранного lock-оверлея (targeting-plan): все параметры меток и
/// рамок-выделений в одном ресурсе — чтобы UI можно было редактировать в
/// инспекторе и сводить к единому дизайну/цветовой схеме без правки кода
/// рисования (тот же приём, что <see cref="ReticleSkin"/> для прицела).
///
/// Назначается узлу <c>TargetOverlay</c> (HudLayer.tscn). Пустые цвета —
/// «взять из палитры» (HudTheme.FactionColor цели), непустые — переопределить.
/// </summary>
[GlobalClass]
public partial class LockMarkerSkin : Resource
{
    // ─────────────────────── Рамка-выделение (углы квадрата) ───────────────────

    /// <summary>Толщина уголков НЕзалоканной рамки, px (тонкие по ТЗ).</summary>
    [Export] public float CornerWidthThin = 1.5f;

    /// <summary>Толщина уголков залоканной цели, px («жирные» при локе).</summary>
    [Export] public float CornerWidthLocked = 3.5f;

    /// <summary>Длина одного уголка как доля половины стороны квадрата (0..1):
    /// 0.25 → уголок занимает четверть стороны с каждого края.</summary>
    [Export(PropertyHint.Range, "0.05,1.0,0.01")]
    public float CornerLenFraction = 0.30f;

    /// <summary>Минимальный полуразмер рамки, px — чтобы далёкая крошечная
    /// цель всё ещё была кликабельна (ТЗ: клик по внутренней части).</summary>
    [Export] public float FrameMinHalfPx = 12.0f;

    /// <summary>Максимальный полуразмер рамки, px — рамка не разрастается
    /// дальше цели разумного размера на экране.</summary>
    [Export] public float FrameMaxHalfPx = 140.0f;

    /// <summary>Запас к габаритному радиусу цели (доля): рамка чуть больше
    /// силуэта (1.15 → +15 %).</summary>
    [Export(PropertyHint.Range, "1.0,2.0,0.01")]
    public float FramePadding = 1.15f;

    /// <summary>Масштаб «фокальной длины» для углового размера: 1 = реальный
    /// FOV камеры. Больше → рамки крупнее при той же дистанции.</summary>
    [Export(PropertyHint.Range, "0.25,4.0,0.05")]
    public float AngularScale = 1.0f;

    // ─────────────────────────── Метки у края экрана ───────────────────────────

    /// <summary>Отступ квадрата «цель за экраном» от края вьюпорта, px.</summary>
    [Export] public float EdgeMarginPx = 34.0f;

    /// <summary>Полуразмер квадрата-метки у края экрана, px.</summary>
    [Export] public float EdgeBoxHalfPx = 16.0f;

    /// <summary>Рисовать штрих от края-квадрата к центру экрана (направление
    /// на цель). По образцу X4 — «луч» к уплывающей цели.</summary>
    [Export] public bool EdgeShowTick = true;

    /// <summary>Длина штриха направления у края-квадрата, px.</summary>
    [Export] public float EdgeTickLengthPx = 12.0f;

    // ───────────────────────────── Метка типа ──────────────────────────────────

    /// <summary>Размер глифа метки типа (сторона вписанного квадрата), px.
    /// Для целей на экране — над верхним левым углом рамки; для заэкраных —
    /// внутри края-квадрата.</summary>
    [Export] public float MarkerSizePx = 13.0f;

    /// <summary>Толщина линий глифа метки типа, px.</summary>
    [Export] public float MarkerWidth = 1.6f;

    // ───────────────────────────────── цвета ───────────────────────────────────
    // Пустой (Alpha == 0) — цвет по фракции цели (HudTheme.FactionColor);
    // непустой — переопределение для всей схемы (единый дизайн).

    /// <summary>Цвет меток/рамок. Alpha 0 → по фракции цели.</summary>
    [Export] public Color MarkerColor = new(0, 0, 0, 0);

    /// <summary>Цвет залоканной цели (жирные углы + метка). Alpha 0 → тот же
    /// цвет, что у остальной цели (локо читается толщиной уголков).</summary>
    [Export] public Color LockColor = new(0, 0, 0, 0);

    // ─────────────────────────── текстовые подписи ─────────────────────────────

    /// <summary>Кегль подписи под рамкой (имя + дистанция), px.</summary>
    [Export] public int LabelFontSize = 13;

    /// <summary>Цвет подписи. Alpha 0 → HudTheme.TextPrimary.</summary>
    [Export] public Color LabelColor = new(0, 0, 0, 0);

    /// <summary>Ширина подписи под рамкой (имя + дистанция), px.</summary>
    [Export] public float LabelWidthPx = 180f;

    /// <summary>Высота подписи под рамкой (2 строки), px.</summary>
    [Export] public float LabelHeightPx = 40f;

    // ───────────────────── текстурные слоты (будущий арт) ──────────────────────
    // Пустой слот → глиф рисуется процедурно (формы из ТЗ: квадрат/трапеции/
    // крестик). Художник может подставить свои спрайты без правки кода.

    /// <summary>Текстура метки станции (вписывается в квадрат MarkerSizePx).</summary>
    [Export] public Texture2D? MarkerStation;
    /// <summary>Текстура метки курьера.</summary>
    [Export] public Texture2D? MarkerCourier;
    /// <summary>Текстура метки грузовика.</summary>
    [Export] public Texture2D? MarkerHauler;
    /// <summary>Текстура метки контейнеровоза.</summary>
    [Export] public Texture2D? MarkerFreighter;
    /// <summary>Текстура метки мишени.</summary>
    [Export] public Texture2D? MarkerTarget;
    /// <summary>Текстура unknown-цели (корабль неизвестного типа).</summary>
    [Export] public Texture2D? MarkerUnknown;

    // ─────────────────── панель данных залоканной цели (HudInfo) ───────────────
    // Небольшие подписи HP/щит/скорость цели — редактируемые, чтобы сводить
    // к единому дизайну/цветовой схеме. Панель строится кодом внутри
    // TargetOverlay (тот же узел), поэтому все knobs здесь.

    /// <summary>Показывать ли панель данных цели (HP/щит/скорость).</summary>
    [Export] public bool TargetPanelEnabled = true;

    /// <summary>Кегль подписей панели данных цели, px.</summary>
    [Export] public int TargetPanelFontSize = 14;

    /// <summary>Цвет подписей панели. Alpha 0 → HudTheme.TextPrimary
    /// (HP всё равно красится по HudTheme.HpColor).</summary>
    [Export] public Color TargetPanelColor = new(0, 0, 0, 0);

    /// <summary>Фон панели (полупрозрачная плашка). Alpha 0 → без фона.</summary>
    [Export] public Color TargetPanelBgColor = new(0.03f, 0.05f, 0.08f, 0.55f);

    /// <summary>Отступ панели от левого/верхнего края вьюпорта, px.</summary>
    [Export] public Vector2 TargetPanelOffset = new(24f, 24f);

    /// <summary>Внутренний отступ плашки панели (все стороны), px.</summary>
    [Export] public float TargetPanelPadding = 10f;

    /// <summary>Вертикальный зазор между подписями панели, px.</summary>
    [Export] public int TargetPanelSeparation = 2;

    /// <summary>Насколько крупнее заголовок (имя цели) относительно подписей, px.</summary>
    [Export] public int TargetPanelHeaderDelta = 2;
}
