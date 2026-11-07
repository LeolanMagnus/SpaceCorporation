using Godot;
using System;
using Godot.Collections;
using SpaceCorporation.UI;

namespace SpaceCorporation;

/// <summary>
/// Радар-виджет (radar-plan.md §2.3): квадратная панель в правом нижнем углу,
/// всё рисуется вектором в _Draw() — псевдотрёхмерная сфера-каркас (внешний
/// контур шара, эллипс-экватор = горизонтальная плоскость корабля, меридианы),
/// блипы с линией проекции на эту плоскость, векторы скорости, «нос» сверху.
/// Источник данных — <see cref="RadarSource"/> (его чистая RadarModel),
/// панель только проецирует блипы модели в пиксели (орфографическая проекция).
///
/// Проекция псевдо-3D (ElevationDeg): горизонталь сжимается по вертикали в
/// sin(наклон) раз, высота ΔY поднимает точку на cos(наклон)·ΔY. От блипа
/// вниз/вверх к его следy на плоскости идёт тонкая линия-«штырь» — по ней
/// видно, спереди/сзади объект (положение относительно эллипса) и
/// сверху/снизу (штырь).
///
/// R2: все блипы белые (заглушка фракций), станция — квадрат, мишень — ромб.
/// Цвета по фракциям, клик-выбор, pinned-маркер — этап R3.
/// Режим дальности (RadarRangeMode): ближний 400 м, средний 2 км, дальний —
/// вся мощность радара корабля (RadarModel.MaxRange), и авторежим по
/// ближайшей цели. Shift+R (HudLayer) цикляет Near→Mid→Long→Auto; колесо мыши
/// по панели — ручной перебор трёх фиксированных ступеней. Текущий режим —
/// мелкий текст в нижнем правом углу панели.
/// </summary>
public partial class RadarPanel : Control
{
    /// <summary>Фиксированные ступени для ручного перебора колесом (без Auto).</summary>
    private static readonly RadarRangeMode[] ManualModes =
        { RadarRangeMode.Near, RadarRangeMode.Mid, RadarRangeMode.Long };

    /// <summary>Короткие подписи режимов для текста под радаром.</summary>
    private static readonly string[] ModeLabels =
        { "400 м", "2 км", "макс", "авто" };

    /// <summary>Отступ кольца от края панели, px (под подписи дальности).</summary>
    private const float RingMargin = 14f;

    /// <summary>Радиус «попадания» блипа кликом, px (R3).</summary>
    public const float ClickRadius = 10f;

    /// <summary>Наклон камеры радара над горизонтальной плоскостью корабля,
    /// град (псевдо-3D сфера). 90° — строго сверху (старый плоский вид),
    /// меньше — «сфера» раскрывается: экватор = локальная горизонталь
    /// корабля, полюс — вверх/вниз по оси Y.</summary>
    public const float ElevationDeg = 28f;

    /// <summary>Сжатие экватора по вертикали: sin(наклон). Экватор-эллипс
    /// имеет ry/rx = BetaSin. Кешируется (статический, меняется только в коде).</summary>
    private static readonly float BetaSin = Mathf.Sin(Mathf.DegToRad(ElevationDeg));

    /// <summary>Подъём точки на экране за единицу высоты ΔY: cos(наклон).</summary>
    private static readonly float BetaCos = Mathf.Cos(Mathf.DegToRad(ElevationDeg));

    /// <summary>Оттенок сетки псевдо-3D сферы (полусфера-каркас).</summary>
    private static readonly Color GridDim = new(0.30f, 0.45f, 0.62f, 0.22f);
    private static readonly Color GridMid = new(0.32f, 0.50f, 0.68f, 0.35f);

    /// <summary>Источник контактов (ставит HudLayer). null — пустой радар.</summary>
    public RadarSource? Source { get; set; }

    /// <summary>Сервис целей для клик-выбора (ставит HudLayer; R3).</summary>
    public TargetingService? Targeting { get; set; }

    /// <summary>Текущий режим дальности (Auto — радиус пересчитывается каждый
    /// кадр по ближайшей цели).</summary>
    public RadarRangeMode Mode { get; private set; } = RadarRangeMode.Mid;

    /// <summary>Текущий радиус обзора, м (зеркало модели — источник правды).</summary>
    public float Range => Source?.Model.Range ?? RadarModel.DefaultRange;

    /// <summary>Радиус ступени фиксированного режима (Long — мощность радара).</summary>
    private float RangeFor(RadarRangeMode mode)
    {
        var m = Source?.Model;
        return mode switch
        {
            RadarRangeMode.Near => RadarModel.NearRange,
            RadarRangeMode.Long => m != null ? m.MaxRange : RadarModel.DefaultMaxRange,
            _ => RadarModel.MidRange,
        };
    }

    /// <summary>Применить режим: у фиксированных — выставить радиус сразу;
    /// Auto — пересчитывать каждый кадр (<see cref="ApplyAutoRange"/>).</summary>
    public void SetMode(RadarRangeMode mode)
    {
        Mode = mode;
        if (Source != null && mode != RadarRangeMode.Auto)
            Source.Model.Range = RangeFor(mode);
        QueueRedraw();
    }

    /// <summary>Shift+R: перебор Near → Mid → Long → Auto → Near…</summary>
    public void CycleMode() =>
        SetMode((RadarRangeMode)(((int)Mode + 1) % ((int)RadarRangeMode.Auto + 1)));

    /// <summary>Авторежим: радиус по дистанции ближайшей цели (ближе 400 м —
    /// ближний, до 2 км — средний, дальше — вся мощность). Публично — и для
    /// headless-теста.</summary>
    public void ApplyAutoRange()
    {
        var m = Source?.Model;
        if (m != null)
            m.Range = RadarModel.AutoRangeFor(m.NearestContactDistance(), m.MaxRange);
    }

    // Пиксельный радиус кольца и центр — для попаданий мыши и рисования.
    private float _ringPx = 1f;
    private Vector2 _center = Vector2.Zero;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop; // панель съедает клики/колесо (R3)
        SetCustomMinimumSize(new Vector2(140, 140));
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Process(double delta)
    {
        // Модель обновляется в RadarSource (он выше по дереву и раньше по
        // порядкуprocess); панель только перерисовывает каждое кадр.
        if (Mode == RadarRangeMode.Auto)
            ApplyAutoRange();
        QueueRedraw();
    }

    /// <summary>Колесо по панели: ручной перебор фиксированных ступеней
    /// Near → Mid → Long (выход из Auto при первом же скролле).</summary>
    public void SetRangeStep(int dir)
    {
        int i = Mode == RadarRangeMode.Auto
            ? (dir > 0 ? 0 : ManualModes.Length - 1)
            : (int)Mode + dir;
        i = ((i % ManualModes.Length) + ManualModes.Length) % ManualModes.Length;
        SetMode(ManualModes[i]);
    }

    // ------------------------------------------------------------------ Draw

    public override void _Draw()
    {
        var (center, ringPx) = Layout();
        _center = center;
        _ringPx = ringPx;

        var border = HudTheme.RadarBorder;
        var dim = new Color(border.R, border.G, border.B, 0.35f);

        // ── Псевдотрёхмерная сфера (каркас) ─────────────────────────────
        // Внешняя окружность — контур сферы радиуса R (орфографическая
        // проекция шара при любом наклоне остаётся кругом). Эллипс-экватор —
        // горизонтальное сечение: локальная горизонтальная плоскость корабля.
        DrawArc(_center, _ringPx, 0f, Mathf.Tau, 64, border, 1f);
        DrawEquatorEllipse(_center, _ringPx, border, dim);           // экватор R
        DrawEquatorEllipse(_center, _ringPx * 0.5f, dim, dim);       // экватор R/2
        DrawMeridians(_center, _ringPx);                              // меридианы
        // Оси горизонтальной плоскости (хорды эллипса): вперёд-назад, влево-вправо.
        DrawLine(_center + Vector2.Left * _ringPx, _center + Vector2.Right * _ringPx, GridDim);
        DrawLine(_center + Vector2.Up * (_ringPx * BetaSin),
                 _center + Vector2.Down * (_ringPx * BetaSin), GridDim);
        // Вертикальная ось сферы (полюса «вверх/вниз» локальной вертикали корабля).
        DrawLine(_center + Vector2.Up * (_ringPx * BetaCos),
                 _center + Vector2.Down * (_ringPx * BetaCos), GridDim);
        // Маркер своего корабля в центре горизонтальной плоскости.
        var ownMark = new Color(border.R, border.G, border.B, 0.9f);
        float mk = 3f;
        DrawLine(_center + Vector2.Right * mk, _center + Vector2.Left * mk, ownMark, 1f);
        DrawLine(_center + Vector2.Up * mk, _center + Vector2.Down * mk, ownMark, 1f);

        // Подписи дальности у внешнего контура (1000/2000 м при R=2000).
        float r = Range;
        DrawSmallText($"{r:F0}", _center + Vector2.Right * (_ringPx - 2f) + Vector2.Up * 3f, dim);
        // Правый край эллипса (там же, где и у круга): вертикального смещения нет.
        DrawSmallText($"{r * 0.5f:F0}", _center + Vector2.Right * (_ringPx * 0.5f + 2f)
            + Vector2.Up * 3f, dim);

        // Стрелка-«нос» сверху: курс радара = курс своего корабля.
        var nose = new Color(border.R, border.G, border.B, 0.9f);
        DrawPolygon(new Godot.Vector2[]
        {
            _center + Vector2.Up * (_ringPx + 8f),
            _center + Vector2.Up * (_ringPx + 1f) + Vector2.Left * 4f,
            _center + Vector2.Up * (_ringPx + 1f) + Vector2.Right * 4f,
        }, new Godot.Color[] { nose });

        var model = Source?.Model;
        if (model == null)
            return;

        // frame-plan шаг 9: строка-подсказка у блипов в фазе захвата — рисуется
        // только когда такие контакты есть (легенды у панели раньше не было).
        bool anyCaptured = false;
        foreach (var b in model.Blips)
            if (b.Contact.Captured && !b.Stale) { anyCaptured = true; break; }
        if (anyCaptured)
            DrawSmallText("ships in capture are marked with \"<\" near the blip",
                new Vector2(6f, Size.Y - 22f), new Color(0.35f, 0.7f, 1f, 0.85f));

        // Текущий режим — мелким в нижнем правом углу панели (right-align).
        var modeColor = new Color(border.R, border.G, border.B, 0.8f);
        DrawString(GetThemeDefaultFont(),
                   new Vector2(Size.X - 6f - 80f, Size.Y - 13f),
                   ModeLabels[(int)Mode], HorizontalAlignment.Right, 80, 9, modeColor);

        float scale = _ringPx / Mathf.Max(1f, model.Range);

        // Блипы: модель отдаёт отсортированными (дальние первыми) — рисуем по
        // порядку, ближние ложатся поверх дальних. Псевдо-3D: точка поднимается
        // над/опускается под эллипс-экватор по HeightOffset (ΔY), и от неё к
        // её проекции на горизонтальную плоскость идёт тонкая линия-«штырь»
        // (1 px): видно спереди/сзади (по экватору) и сверху/снизу (по штырю).
        foreach (var b in model.Blips)
        {
            if (!ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, b.HeightOffset, scale,
                           out Vector2 ground, out Vector2 p))
                continue; // за контуром сферы — клиппинг

            // R3: цвет — по фракции контакта (Player/Hostile/Neutral…),
            // прозрачность — fade исчезнувших.
            var color = HudTheme.FactionColor(b.Contact.Faction);
            color.A = b.Fade;

            // Подбитый контакт мигает (источник — ShipHealthState, через Damaged).
            if (b.Contact.Damaged)
                color.A *= 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);

            // frame-plan шаг 9: корабль в фазе захвата — скобка-«⊂» рядом с
            // блипом (тот же блип, НЕ новый тип метки).
            if (b.Contact.Captured)
                DrawString(GetThemeDefaultFont(), p + new Vector2(10f, 4f), "⊂",
                    HorizontalAlignment.Left, 0, 9, new Color(0.35f, 0.7f, 1f, 0.9f));

            // Линия проекции на плоскость корабля (только при заметной высоте).
            if ((p - ground).LengthSquared() > 4f)
            {
                var drop = new Color(color.R, color.G, color.B, color.A * 0.45f);
                DrawLine(ground, p, drop, 1f);
                DrawCircle(ground, 1.2f, new Color(color.R, color.G, color.B, color.A * 0.5f));
            }

            DrawBlip(b, p, color);
            DrawVelocity(b, p, scale, color);

            // R3: pinned-маркер текущей цели — тонкое кольцо вокруг блипа.
            if (b.Contact.IsTarget)
            {
                var ring = HudTheme.TextAccent;
                ring.A = color.A;
                DrawArc(p, 7f, 0f, Mathf.Tau, 20, ring, 1f);
            }
        }
    }

    /// <summary>Пиксельная геометрия панели: центр и радиус кольца (px).
    /// Наружу — для попаданий мыши; _Draw() кеширует их в _center/_ringPx.</summary>
    private (Vector2 Center, float RingPx) Layout()
    {
        float size = Mathf.Min(Size.X, Size.Y);
        float ring = Mathf.Max(4f, size * 0.5f - RingMargin);
        return (Size * 0.5f, ring);
    }

    /// <summary>Псевдотрёхмерная проекция: экранная X/Y модели (метры; вперёд=+Y)
    /// и высота ΔY в пиксели. center/ground — проекция на горизонтальную
    /// плоскость корабля (эллипс-экватор), p — сама точка (приподнята на
    /// ΔY·cos наклон·scale). false — точка за контуром сферы (не рисуем).</summary>
    private static bool ProjectTo(Vector2 center, float ringPx, float screenX,
        float screenY, float heightY, float scale, out Vector2 ground, out Vector2 p)
    {
        float x = screenX * scale, y = screenY * scale;
        ground = center + new Vector2(x, -y * BetaSin);
        float dy = heightY * scale * BetaCos;
        p = ground + Vector2.Up * dy;
        // Клиппинг по контуру сферы (орфографический шар: радиус = ringPx),
        // с запасом гистерезиса — как раньше по кольцу.
        float lim = ringPx * RadarModel.HysteresisOut();
        return (p - center).Length() <= lim;
    }

    /// <summary>Эллипс горизонтального сечения сферы (локальная горизонталь
    /// корабля): дальняя половина тусклее передней (эффект «прозрачной сферы»).</summary>
    private void DrawEquatorEllipse(Vector2 center, float radius, Color front, Color back)
    {
        float ry = radius * BetaSin;
        var ptsFront = new Godot.Vector2[33];
        var ptsBack = new Godot.Vector2[33];
        for (int i = 0; i <= 32; i++)
        {
            float a = Mathf.Pi * i / 32f; // 0..π — ближняя половина (низ экрана)
            var v = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            ptsFront[i] = center + new Vector2(v.X * radius, v.Y * ry);
            ptsBack[i] = center + new Vector2(v.X * radius, -v.Y * ry);
        }
        DrawPolyline(ptsBack, back);
        DrawPolyline(ptsFront, front);
    }

    /// <summary>Меридианы сферы (вертикальные эллипсы) — читают объём.</summary>
    private void DrawMeridians(Vector2 center, float ringPx)
    {
        // Наклонная проекция: горизонталь меридиана сжимается по фазе так же,
        // что и экватор (вклад cos-компоненты вдоль «вправо» — на всю ширину,
        // вдоль «вперёд» — в BetaSin раз меньше).
        for (int m = 0; m < 3; m++)
        {
            float lam = Mathf.Pi / 3f * m; // ориентация плоскости меридиана
            float cx = Mathf.Cos(lam), sz = Mathf.Sin(lam);
            var pts = new Godot.Vector2[49];
            for (int i = 0; i <= 48; i++)
            {
                float a = Mathf.Tau * i / 48f;
                float cosA = Mathf.Cos(a), sinA = Mathf.Sin(a);
                // Точка сферы: горизонтальная составляющая (cosA) в направлении
                // меридиана, вертикальная (sinA) — вверх.
                float gx = cosA * cx * ringPx;
                float gy = -cosA * sz * ringPx * BetaSin;
                pts[i] = center + new Vector2(gx, gy - sinA * ringPx * BetaCos);
            }
            DrawPolyline(pts, m == 1 ? GridMid : GridDim);
        }
    }

    /// <summary>Форма блипа по классу контакта (RadarShape).</summary>
    private void DrawBlip(RadarBlip b, Vector2 p, Color color)
    {
        switch (b.Contact.Shape)
        {
            case RadarShape.Station: // квадрат — станция
            {
                const float s = 3.5f;
                DrawRect(new Rect2(p.X - s, p.Y - s, s * 2f, s * 2f), color);
                break;
            }
            case RadarShape.Target: // ромб — мишень/цель
            {
                const float s = 4f;
                DrawPolygon(new Godot.Vector2[]
                {
                    p + Vector2.Up * s, p + Vector2.Right * s,
                    p + Vector2.Down * s, p + Vector2.Left * s,
                }, new Godot.Color[] { color });
                break;
            }
            default: // точка — корабль
                DrawCircle(p, 2.5f, color);
                break;
        }
    }

    /// <summary>Короткий штрих вектора скорости (масштаб метры→px, сclamp по длине).
    /// Псевдо-3D: та же проекция, что и позиция — горизонтальная часть по
    /// local-осям радара (forward/right своего корабля) плюс вертикальная
    /// составляющая RelVel·up (нормаль горизонтальной плоскости корабля).</summary>
    private void DrawVelocity(RadarBlip b, Vector2 p, float scale, Color color)
    {
        var m = Source!.Model;
        var rv = b.Contact.RelVel;
        // Горизонтальная часть в local-систему радара (те же оси, что у позиции).
        float sx = rv.X * m.RightX + rv.Y * m.RightY + rv.Z * m.RightZ;
        float sy = rv.X * m.FwdX + rv.Y * m.FwdY + rv.Z * m.FwdZ;
        float h = rv.X * m.UpX + rv.Y * m.UpY + rv.Z * m.UpZ;
        var dir = new Vector2(sx, -sy * BetaSin - h * BetaCos);
        float speed = dir.Length();
        if (speed < 0.1f)
            return; // почти неподвижен на экране — штрих не нужен
        dir /= speed;
        float len = Mathf.Min(4f + speed * scale * 0.25f, 14f);
        DrawLine(p, p + dir * len, new Color(color.R, color.G, color.B, color.A * 0.6f), 1f);
    }

    // ------------------------------------------------------------- ввод мыши

    public override void _GuiInput(InputEvent @event)
    {
        // Колесо — масштаб (R2).
        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            // Колесо вверх — «приблизить»: ближняя ступень (Near→Mid→Long).
            if (mb.ButtonIndex == MouseButton.WheelUp)
                SetRangeStep(-1);
            else if (mb.ButtonIndex == MouseButton.WheelDown)
                SetRangeStep(+1);
            // ЛКМ по блипу — выбор цели (R3: TargetingService.Select).
            else if (mb.ButtonIndex == MouseButton.Left)
                ClickSelect(mb.Position);
        }
    }

    /// <summary>Hit-test: NetId ближайшего блипа в радиусе ClickRadius px от
    /// точки панели (0 — никого). Публично — для клик-выбора и headless-теста.</summary>
    public int HitTestBlipId(Vector2 at)
    {
        var model = Source?.Model;
        if (model == null)
            return 0;
        // Геометрию берём из Layout() напрямую — headless-тест может не рисовать
        // (_Draw не вызывается), а хит-тест обязан работать без кадра отрисовки.
        var (center, ringPx) = Layout();
        float scale = ringPx / Mathf.Max(1f, model.Range);

        int bestId = 0;
        float bestD2 = ClickRadius * ClickRadius;
        foreach (var b in model.Blips)
        {
            if (!ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, b.HeightOffset, scale,
                           out _, out Vector2 p))
                continue;
            float d2 = (p - at).LengthSquared();
            if (d2 < bestD2)
            {
                bestD2 = d2;
                bestId = b.Contact.NetId;
            }
        }
        return bestId;
    }

    /// <summary>Экранная позиция блипа (px) или null, если блипа/попадания нет.</summary>
    public Vector2? BlipPixel(int netId)
    {
        var model = Source?.Model;
        var b = model?.FindBlip(netId);
        if (b == null || model == null)
            return null;
        var (center, ringPx) = Layout();
        float scale = ringPx / Mathf.Max(1f, model.Range);
        if (!ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, b.HeightOffset, scale,
                       out _, out Vector2 p))
            return null;
        return p;
    }

    /// <summary>Клик по блипу → цель (блип в ClickRadius px от курсора).</summary>
    private void ClickSelect(Vector2 at)
    {
        if (Targeting == null)
            return;
        int id = HitTestBlipId(at);
        if (id != 0)
            Targeting.Select(id);
    }

    // ---------------- мелкий текст без шрифтовых метрик (моноширинный bitmap)

    private void DrawSmallText(string text, Vector2 at, Color color)
    {
        // Подписи рисуем минимальным размером: используем стандартный шрифт
        // темы через DrawString ниже (метрики не нужны — текст маленький).
        var font = GetThemeDefaultFont();
        DrawString(font, at, text, HorizontalAlignment.Left, 0, 9, color);
    }
}
