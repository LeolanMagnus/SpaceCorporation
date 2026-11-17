using Godot;

namespace SpaceCorporation;

/// <summary>
/// Круговое меню контекстных действий (radial-menu). Вызывается клавишей Tab
/// (`radial_menu`, обработка в HudLayer): вокруг центра экрана появляется
/// кольцо из 8 секторов — верх, право-верх, право, право-низ, низ,
/// лево-низ, лево, лево-верх. Каждый сектор — активатор действия;
/// наведение мыши выбирает сектор; выбор происходит в МОМЕНТ ЗАКРЫТИЯ —
/// активируется сектор, который был подсвечен (кликом выбирать не нужно).
/// Кольцо открыто, пока Tab зажата (закрытие — отпускание, Esc или ПКМ);
/// Esc/ПКМ — отмена без выбора. Ховер продолжается за внешней дугой —
/// вплоть до края экрана.
///
/// Нумерация секторов по часовой стрелке от верха: 0=верх, 1=право-верх,
/// 2=право, 3=право-низ, 4=низ, 5=лево-низ, 6=лево, 7=лево-верх.
///
/// Сектор 5 (лево-низ) — «Связь с целью»: доступен только когда корабль
/// локального игрока держит залокированную цель (TargetingService). Без
/// цели сектор рисуется серым и не активируется. Само соединение — заглушка
/// (событие в комм-лог), процесс связи реализуется позднее.
/// Остальные сектора — пустые слоты будущего: рисуются, но действия не имеют.
/// </summary>
public partial class RadialMenu : Control
{
    /// <summary>Число секторов кольца.</summary>
    public const int SectorCount = 8;

    /// <summary>Индекс сектора «Связь с целью» (лево-низ).</summary>
    public const int SectorCommlink = 5;

    // Геометрия кольца в px (относительно центра экрана).
    private const float InnerR = 50f;
    private const float OuterR = 250f;

    /// <summary>Сервис целей: по нему определяется доступность «Связи с целью».</summary>
    public TargetingService? Targeting;

    private int _hovered = -1; //.сектор под курсором (-1 — вне кольца)

    // Палитра сектора (общий вид / наведение / недоступен).
    private static readonly Color SectorFill = new(0.10f, 0.15f, 0.21f, 0.72f);
    private static readonly Color SectorFillHover = new(0.22f, 0.36f, 0.52f, 0.85f);
    private static readonly Color SectorFillDisabled = new(0.10f, 0.11f, 0.13f, 0.50f);
    private static readonly Color SectorEdge = HudTheme.PanelBorder;
    private static readonly Color LabelColor = HudTheme.TextPrimary;
    private static readonly Color LabelDisabled = new(0.45f, 0.47f, 0.50f);

    /// <summary>Подписи секторов. ПРИ ВКЛЮЧЁННОМ DebugSlots пустые слоты
    /// заполнены тестовыми «Опция N» (см. SectorAvailable/Activate).</summary>
    private static readonly string[] Labels =
    {
        "Опция 1", "Самая длинная на свете опция", "Опция 3", "Опция 4", "Опция 5",
        "Связь с целью", "Опция 6", "Опция 7",
    };

    /// <summary>ДЕБАГ: все пустые слоты доступны и при выборе пишут событие
    /// в комм-лог («Опция N выбрана»). Снять флаг при наполнении меню
    /// реальными действиями.</summary>
    public static bool DebugSlots = true;

    public override void _Ready()
    {
        // Видимость/якоря/mouse_filter заданы в сцене RadialMenu.tscn; здесь
        // только страховка стартовой невидимости (Open/Close переключают фильтр).
        Visible = false;
    }

    /// <summary>Сколько строк занимает подпись при переносе по словам на
    /// ширине w (перенос как у DrawString с width; нужно для вертикального
    /// центрирования блока текста).</summary>
    private static int CountWrappedLines(Font font, string text, float w, int size)
    {
        int lines = 1;
        float lineW = 0f;
        float spaceW = font.GetStringSize(" ", HorizontalAlignment.Left, 0f, size).X;
        foreach (string word in text.Split(' '))
        {
            if (word.Length == 0) continue;
            float wordW = font.GetStringSize(word, HorizontalAlignment.Left, 0f, size).X;
            if (lineW > 0f && lineW + spaceW + wordW > w)
            {
                lines++;
                lineW = wordW;
            }
            else
            {
                lineW += spaceW + wordW;
            }
        }
        return lines;
    }

    /// <summary>Открыть кольцо (HudLayer при нажатии Tab).</summary>
    public void Open()
    {
        _hovered = -1;
        Visible = true;
        AnyOpen = true;
        // Пока меню открыто — перехватываем клики, чтобы выбор сектора
        // не «протёк» в клик-выбор целей на радаре/оверлее.
        MouseFilter = MouseFilterEnum.Stop;
        QueueRedraw();
    }

    /// <summary>Закрыть кольцо. commit=true — ВЫБОР В МОМЕНТ ЗАКРЫТИЯ:
    /// активируется сектор, подсвеченный на закрытии (отпускание Tab);
    /// commit=false — отмена без выбора (Esc, ПКМ).</summary>
    public void Close(bool commit = false)
    {
        int h = _hovered;
        Visible = false;
        AnyOpen = false;
        _hovered = -1;
        MouseFilter = MouseFilterEnum.Ignore;
        if (commit && h >= 0)
            TryActivate(h); //.TryActivate повторно закрывает — безвредно
    }

    /// <summary>Доступен ли сектор сейчас (в момент наведения/выбора).</summary>
    private bool SectorAvailable(int i)
    {
        if (i != SectorCommlink)
            return DebugSlots; //.прочие слоты: доступны лишь как дебаг-заглушки
        return Targeting != null && Targeting.TargetNode != null
            && GodotObject.IsInstanceValid(Targeting.TargetNode)
            && Targeting.ValidateTarget();
    }

    public override void _Draw()
    {
        if (!Visible)
            return;

        Vector2 c = Size / 2f;
        var font = GetThemeDefaultFont();

        for (int i = 0; i < SectorCount; i++)
        {
            // Угол центра сектора: 0 = «верх», по часовой стрелке; на экране
            // «верх» — это -Y, поэтому базовый угол -90°.
            float a0 = Mathf.DegToRad(i * 45f - 90f - 22.5f);
            float a1 = a0 + Mathf.DegToRad(45f);

            bool avail = SectorAvailable(i);
            bool hov = i == _hovered && avail;
            Color fill = !avail ? SectorFillDisabled : hov ? SectorFillHover : SectorFill;

            DrawSector(c, a0, a1, fill);

            // Подпись сектора — ближе к внешнему краю дуги, с переносом слов.
            if (Labels[i].Length > 0)
            {
                float am = Mathf.DegToRad(i * 45f - 90f);
                // Дальше к внешнему краю (0.78 радиуса кольца вместо середины).
                Vector2 lp = c + new Vector2(Mathf.Cos(am), Mathf.Sin(am))
                    * (InnerR + (OuterR - InnerR) * 0.70f);
                // Горизонтальное центрирование: align работает только внутри
                // заданной ширины (баг: width=0 — текст уезжал влево).
                const float labelW = 170f;
                const int labelSize = 16;
                // Реальная высота блока (с учётом переноса) — для вертикального
                // центрирования одно- и двухстрочных подписей.
                float blockH = labelSize * 1.25f * CountWrappedLines(font, Labels[i], labelW, labelSize);
                // Линия горизонта проходит через оптический центр текста, а не
                // под ним: базовая линия первой строки на ascent/2 НИЖЕ точки lp
                // (DrawString ставит pos на базовую линию, а не на низ блока).
                float ascent = font.GetAscent(labelSize);
                DrawString(font,
                    lp - new Vector2(labelW / 2f, blockH * 0.5f - ascent),
                    Labels[i], HorizontalAlignment.Center, labelW, labelSize,
                    avail ? LabelColor : LabelDisabled);
            }
        }

        // Центральная втулка: имя текущей цели или «No target».
        DrawCircle(c, InnerR - 6f, new Color(0.05f, 0.07f, 0.10f, 0.55f));
        string center = Targeting != null && Targeting.TargetNode != null
            ? Targeting.TargetName : "No target";
        float hubW = (InnerR - 6f) * 2f;
        DrawString(font, c - new Vector2(hubW / 2f, 15f * 0.30f), center,
            HorizontalAlignment.Center, hubW, 15, HudTheme.TextAccent);
    }

    /// <summary>Тесселяция кольцевого сегмента (дуги разбиваем на 6 граней).</summary>
    private void DrawSector(Vector2 c, float a0, float a1, Color fill)
    {
        const int steps = 6;
        var pts = new Vector2[2 * (steps + 1)];
        for (int s = 0; s <= steps; s++)
        {
            float a = a0 + (a1 - a0) * s / steps;
            Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
            pts[s] = c + dir * InnerR;
            pts[pts.Length - 1 - s] = c + dir * OuterR;
        }
        var cols = new Color[pts.Length];
        for (int s = 0; s < cols.Length; s++)
            cols[s] = fill;
        DrawPolygon(pts, cols);

        // Граница сектора: две радиальные кромки + дуги кольца. (Баг «много
        // секторов»: раньше линия рисовалась на каждой подточке аппроксимации.)
        Vector2 d0 = new(Mathf.Cos(a0), Mathf.Sin(a0));
        Vector2 d1 = new(Mathf.Cos(a1), Mathf.Sin(a1));
        DrawLine(c + d0 * InnerR, c + d0 * OuterR, SectorEdge with { A = 0.6f }, 1f);
        DrawLine(c + d1 * InnerR, c + d1 * OuterR, SectorEdge with { A = 0.6f }, 1f);
        DrawArc(c, InnerR, a0, a1, 16, SectorEdge with { A = 0.6f }, 1f);
        DrawArc(c, OuterR, a0, a1, 16, SectorEdge with { A = 0.6f }, 1f);
    }

    /// <summary>Навести ховер на точку p (публично — для теста выбора при
    /// закрытии; обычно вызывается из _GuiInput).</summary>
    public void HoverAt(Vector2 p)
    {
        int h = SectorAt(p);
        if (h != _hovered)
        {
            _hovered = h;
            QueueRedraw();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m)
        {
            HoverAt(m.Position);
        }
        else if (@event is InputEventMouseButton b && b.Pressed)
        {
            if (b.ButtonIndex == MouseButton.Right)
            {
                Close();
                GetViewport().SetInputAsHandled();
            }
            else if (b.ButtonIndex == MouseButton.Left)
            {
                // Выбор — не кликом как таковым, а «закрытием с коммитом»:
                // активируется сектор, подсвеченный в момент закрытия.
                Close(commit: true);
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>Меню открыто (спрос у HudLayer).</summary>
    public bool IsOpen => Visible;

    /// <summary>Открыт ли ЛЮБОЙ экземпляр меню — гейт для сборщика мыши
    /// Ship.PilotCam (он получает события в фазе _Input, ДО _UnhandledInput
    /// HudLayer, поэтому SetInputAsHandled его не глушит).</summary>
    public static bool AnyOpen;

    /// <summary>Enter при открытом меню: активировать сектор под курсором
    /// (клавиатуру обрабатывает HudLayer — у Control без фокуса клавиши не
    /// доходят до него надёжно).</summary>
    public void ActivateHovered()
    {
        if (Visible && _hovered >= 0)
            TryActivate(_hovered);
    }

    /// <summary>Сектор под точкой p: -1 — вне кольца (только внутренняя
    /// граница: за внешней дугой выбор продолжается до самого края экрана,
    /// «налево-вниз у курсора у края» — это всё тот же сектор). Публично —
    /// для теста геометрии.</summary>
    public int SectorAt(Vector2 p)
    {
        Vector2 d = p - Size / 2f;
        float r = d.Length();
        if (r < InnerR)
            return -1;
        // atan2 даёт угол от +X; переводим в «от верха по часовой».
        // Поправка +22.5° (=половина сектора): сектор i рисуется центром на
        // i*45° и занимает полосу [i*45-22.5, i*45+22.5), а без поправки
        // граница попадала в середину сектора — подсветка убегала на
        // соседний сектор, и ховер ловился за его пределами.
        float a = Mathf.RadToDeg(Mathf.Atan2(d.Y, d.X)) + 90f + 22.5f;
        if (a < 0f) a += 360f;
        return (int)(a / 45f) % SectorCount;
    }

    /// <summary>Активировать сектор i (0..7, счёт по часовой от верха):
    /// выполнить действие и закрыть кольцо. Недоступный сектор — молча
    /// ничего не делает (меню остаётся открытым). Публично — для теста.</summary>
    public bool TryActivate(int sector)
    {
        if (!SectorAvailable(sector))
            return false;

        if (sector == SectorCommlink)
            StartCommlink();
        else if (DebugSlots)
            HudEventBus.Report($"{Labels[sector]} выбрана"); //.дебаг-заглушки пустых слотов

        Close();
        return true;
    }

    /// <summary>Крючок «радиовызов сделан»: HudLayer решает исход связи
    /// (сейчас — всегда успешный ответ: открывается меню радиосвязи CommlinkMenu,
    /// П2; заложена ветка отказа: false-исход = только событие в комм-лог).</summary>
    public System.Action<string>? OnCommlinkCalled;

    /// <summary>«Связь с целью» — начало процесса связи. Сейчас: событие в
    /// комм-лог + вызов крючка (меню радиосвязи открывается в HudLayer, П2).</summary>
    private void StartCommlink()
    {
        string name = Targeting?.TargetName ?? "";
        HudEventBus.Report($"Commlink: contacting {name}...");
        OnCommlinkCalled?.Invoke(name);
    }
}
