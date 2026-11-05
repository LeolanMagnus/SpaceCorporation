using Godot;

namespace SpaceCorporation;

/// <summary>
/// Прицельная марка режима пилотирования 2.0 (замена старого <see cref="Crosshair"/>).
/// Управление теперь «на курсор»: центр экрана — точка, куда смотрит нос, а
/// прицел — свободный курсор мыши. Марка показывает это наглядно:
///
///   • точка-маркер носа в центре экрана (куда целится нос прямо сейчас);
///   • курсор-перекрестие на позиции мыши (это и есть прицел);
///   • полупрозрачная пунктирная линия толщиной <c>DashWidth</c> из центра к
///     курсору — рисуется, когда курсор за «слепой зоной» (см. <see cref="PilotAim"/>):
///     пока внутри — нос не крутится и линии нет;
///   • три галочки-шеврона на линии в 1/4, 1/2 и 3/4, вершиной развёрнутые к
///     курсору (показывают «куда доворачиваем»).
///
/// Каждый элемент рисуется либо текстурой из <see cref="ReticleSkin"/>, либо
/// примитивом (fallback) — поэтому до концепт-арта марка живёт, а «приодеть» её
/// артом = назначить четыре текстуры в скине, без правки этого кода.
///
/// <see cref="ShowAim"/> включает/выключает линию+галочки+перекрестие-прицел
/// (их нет, когда локальный пир не ведёт корабль в режиме пилотирования; сама
/// точка носа рисуется всегда — она просто «куда смотрит нос»).
/// </summary>
public partial class AimReticle : Control
{
    [Export] public ReticleSkin? Skin;

    /// <summary>Показывать ли прицельный слой (курсор-перекрестие, линия,
    /// галочки). Ставит <c>HudLayer</c>: true — только когда локальный пир ведёт
    /// корабль в режиме пилотирования (не в режиме взаимодействия).</summary>
    public bool ShowAim { get; set; }

    /// <summary>Центр экрана = нос + «центр» расчёта PilotAim. Полноэкранный
    /// Control с якорями на весь rect: Size == размер вьюпорта, центр — Size/2.</summary>
    private Vector2 Center => Size * 0.5f;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // Якоря «на весь rect» ставятся в сцене (HudLayer.tscn, anchors_preset=15,
        // как у HudRoot) — тогда Size == размер вьюпорта и центр = Size/2. Никаких
        // anchor/grow-хелперов в коде не нужно: растяжка задаётся якорями в tscn.
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        // Курсор двигается независимо от кадров — перерисовываем каждый кадр,
        // иначе линия/галочки/прицел будут «заморожены» между событиями мыши.
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 c = Center;
        ReticleSkin skin = Skin ?? DefaultSkin;
        // Экранная позиция курсора в координатах вьюпорта — ровно те, что видит
        // Ship (InputEventMouseMotion.Position / GetViewport().GetVisibleRect()).
        // Control растянут якорями на весь rect без смещения, поэтому глобальные
        // координаты совпадают с локальными; берём глобальные — они однозначны.
        Vector2 cursor = GetGlobalMousePosition();
        Vector2 off = cursor - c;
        float r = off.Length();

        // Прицельный слой — только когда пир реально ведёт корабль.
        if (ShowAim)
        {
            DrawNosePoint(c, skin);

            // Вне слепой зоны: линия «центр → курсор» + галочки + курсор-прицел.
            if (r > PilotAim.DeadzoneRadiusPx && r > 0.001f)
            {
                DrawAimDashLine(c, cursor, skin);
                DrawChevrons(c, off, r, skin);
            }
            DrawCursorReticle(cursor, skin);
        }
        else
        {
            // Не пилотируем — точка носа остаётся ориентиром «куда смотрит нос».
            DrawNosePoint(c, skin);
        }
    }

    // ────────────────────────────── Элементы ───────────────────────────────────

    /// <summary>Точка-маркер носа (центр экрана).</summary>
    private void DrawNosePoint(Vector2 c, ReticleSkin skin)
    {
        if (DrawTextured(skin.NoseDot, c, 0f, skin.NoseColor))
            return;
        // Примитив: маленький круг + крошечный крестик внутри, чтобы «точка»
        // читалась на любом фоне.
        DrawCircle(c, skin.NoseRadius, skin.NoseColor);
        float k = skin.NoseRadius + 2f;
        DrawLine(new Vector2(c.X - k, c.Y), new Vector2(c.X - skin.NoseRadius, c.Y), skin.NoseColor, 1f);
        DrawLine(new Vector2(c.X + skin.NoseRadius, c.Y), new Vector2(c.X + k, c.Y), skin.NoseColor, 1f);
        DrawLine(new Vector2(c.X, c.Y - k), new Vector2(c.X, c.Y - skin.NoseRadius), skin.NoseColor, 1f);
        DrawLine(new Vector2(c.X, c.Y + skin.NoseRadius), new Vector2(c.X, c.Y + k), skin.NoseColor, 1f);
    }

    /// <summary>Полупрозрачная пунктирная линия от центра к курсору, толщиной
    /// <c>skin.DashWidth</c> (по ТЗ = 2 пикселя).</summary>
    private void DrawAimDashLine(Vector2 c, Vector2 cursor, ReticleSkin skin)
    {
        if (skin.Dash != null)
        {
            // Текстурный пунктир: ставим «штрих» по линии через равные шаги.
            DrawDashedTexturedLine(c, cursor, skin);
            return;
        }
        float dash = Mathf.Max(1f, skin.DashLength);
        float gap = Mathf.Max(0f, skin.DashGap);
        float period = dash + gap;
        Vector2 dir = (cursor - c).Normalized();
        float len = (cursor - c).Length();
        for (float t = 0f; t < len; t += period)
        {
            Vector2 a = c + dir * t;
            Vector2 b = c + dir * Mathf.Min(t + dash, len);
            DrawLine(a, b, skin.DashColor, skin.DashWidth);
        }
    }

    private void DrawDashedTexturedLine(Vector2 c, Vector2 cursor, ReticleSkin skin)
    {
        Vector2 ts = skin.Dash!.GetSize();
        float dash = Mathf.Max(1f, ts.X);
        float gap = Mathf.Max(0f, skin.DashGap);
        float period = dash + gap;
        float angle = (cursor - c).Angle();
        Vector2 dir = (cursor - c).Normalized();
        float len = (cursor - c).Length();
        for (float t = 0f; t < len; t += period)
        {
            float seg = Mathf.Min(dash, len - t);
            Vector2 mid = c + dir * (t + seg * 0.5f);
            DrawSetTransform(mid, angle, new Vector2(seg / dash, 1f));
            DrawTextureRect(skin.Dash, new Rect2(-ts * 0.5f, ts), false, skin.DashColor);
        }
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One); // сброс трансформации (в C# нет DrawSetResetTransform)
    }

    /// <summary>Три галочки-шеврона на линии в 1/4, 1/2 и 3/4, развёрнутые к
    /// курсору (вершина «>» смотрит вдоль направления на курсор).</summary>
    private void DrawChevrons(Vector2 c, Vector2 off, float r, ReticleSkin skin)
    {
        Vector2 dir = off / r;
        float angle = dir.Angle();
        const float count = 3f;
        for (int i = 1; i <= 3; i++)
        {
            // Равноудалённые точки на линии: 1/4, 1/2, 3/4 от центра к курсору.
            float frac = i / (count + 1f);
            Vector2 p = c + off * frac;
            if (DrawTextured(skin.Chevron, p, angle, skin.ChevronColor))
                continue;
            DrawChevronPrim(p, dir, skin);
        }
    }

    /// <summary>Процедурная галочка «>»: вершина в <paramref name="p"/>, два
    /// луча назад (против <paramref name="dir"/>) под ±45°.</summary>
    private void DrawChevronPrim(Vector2 p, Vector2 dir, ReticleSkin skin)
    {
        float s = skin.ChevronSize;
        Vector2 back = -dir * s;
        Vector2 perp = new Vector2(-dir.Y, dir.X) * s;
        DrawLine(p, p + back + perp, skin.ChevronColor, skin.ChevronWidth);
        DrawLine(p, p + back - perp, skin.ChevronColor, skin.ChevronWidth);
    }

    /// <summary>Курсор-перекрестие: четыре луча с зазором в центре.</summary>
    private void DrawCursorReticle(Vector2 m, ReticleSkin skin)
    {
        if (DrawTextured(skin.CursorCross, m, 0f, skin.CursorColor))
            return;
        float arm = skin.CursorArm;
        float g = skin.CursorGap;
        float w = skin.CursorWidth;
        DrawLine(new Vector2(m.X - g - arm, m.Y), new Vector2(m.X - g, m.Y), skin.CursorColor, w);
        DrawLine(new Vector2(m.X + g, m.Y), new Vector2(m.X + g + arm, m.Y), skin.CursorColor, w);
        DrawLine(new Vector2(m.X, m.Y - g - arm), new Vector2(m.X, m.Y - g), skin.CursorColor, w);
        DrawLine(new Vector2(m.X, m.Y + g), new Vector2(m.X, m.Y + g + arm), skin.CursorColor, w);
    }

    // ─────────────────────────── Общие хелперы ────────────────────────────────

    /// <summary>Нарисовать текстуру центром в <paramref name="p"/>, повёрнутую на
    /// <paramref name="angle"/>. true — если текстура была; false — caller рисует
    /// примитив.</summary>
    private bool DrawTextured(Texture2D? tex, Vector2 p, float angle, Color modulate)
    {
        if (tex == null)
            return false;
        Vector2 ts = tex.GetSize();
        DrawSetTransform(p, angle, Vector2.One);
        DrawTextureRect(tex, new Rect2(-ts * 0.5f, ts), false, modulate);
        DrawSetTransform(Vector2.Zero, 0f, Vector2.One); // сброс трансформации
        return true;
    }

    /// <summary>Скин по умолчанию, если в сцене не назначен ресурс — чтобы марка
    /// работала «из коробки» до подключения арт-ресурса.</summary>
    private static readonly ReticleSkin DefaultSkin = new();
}
