using Godot;
using System;
using Godot.Collections;
using SpaceCorporation.UI;

namespace SpaceCorporation;

/// <summary>
/// Радар-виджет (radar-plan.md §2.3, этап R2): квадратная панель в правом
/// нижнем углу, всё рисуется вектором в _Draw() — кольцо-сетка, блипы,
/// векторы скорости, «нос» сверху. Источник данных — <see cref="RadarSource"/>
/// (его чистая RadarModel), панель только проецирует блипы модели в пиксели.
///
/// R2: все блипы белые (заглушка фракций), станция — квадрат, мишень — ромб.
/// Цвета по фракциям, клик-выбор, pinned-маркер — этап R3.
/// Масштаб (радиус обзора) — колесо мыши по списку ступеней.
/// </summary>
public partial class RadarPanel : Control
{
    /// <summary>Ступени радиуса обзора, м (колесо циклически по ним).</summary>
    public static readonly float[] RangeSteps = { 500f, 1000f, 2000f, 4000f };

    /// <summary>Отступ кольца от края панели, px (под подписи дальности).</summary>
    private const float RingMargin = 14f;

    /// <summary>Радиус «попадания» блипа кликом, px (R3).</summary>
    public const float ClickRadius = 10f;

    /// <summary>Источник контактов (ставит HudLayer). null — пустой радар.</summary>
    public RadarSource? Source { get; set; }

    /// <summary>Сервис целей для клик-выбора (ставит HudLayer; R3).</summary>
    public TargetingService? Targeting { get; set; }

    /// <summary>Индекс ступени радиуса (по умолчанию 2000 м).</summary>
    public int RangeStepIndex { get; set; } = 2;

    /// <summary>Текущий радиус обзора, м (зеркало модели — источник правды).</summary>
    public float Range => Source?.Model.Range ?? RadarModel.DefaultRange;

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
        QueueRedraw();
    }

    /// <summary>Установить ступень радиуса (клавиатура/колесо/тест).</summary>
    public void SetRangeStep(int index)
    {
        RangeStepIndex = Mathf.Clamp(index, 0, RangeSteps.Length - 1);
        if (Source != null)
            Source.Model.Range = RangeSteps[RangeStepIndex];
        QueueRedraw();
    }

    // ------------------------------------------------------------------ Draw

    public override void _Draw()
    {
        var (center, ringPx) = Layout();
        _center = center;
        _ringPx = ringPx;

        var border = HudTheme.RadarBorder;
        var dim = new Color(border.R, border.G, border.B, 0.35f);

        // Кольца дальности: внешнее (текущий R) + среднее (R/2) + крест.
        DrawArc(_center, _ringPx, 0f, Mathf.Tau, 48, border, 1f);
        DrawArc(_center, _ringPx * 0.5f, 0f, Mathf.Tau, 32, dim, 1f);
        DrawLine(_center + Vector2.Left * _ringPx, _center + Vector2.Right * _ringPx, dim);
        DrawLine(_center + Vector2.Up * _ringPx, _center + Vector2.Down * _ringPx, dim);

        // Подписи дальности у колец (1000/2000 м при R=2000).
        float r = Range;
        DrawSmallText($"{r:F0}", _center + Vector2.Right * (_ringPx - 2f) + Vector2.Up * 3f, dim);
        DrawSmallText($"{r * 0.5f:F0}", _center + Vector2.Right * (_ringPx * 0.5f + 2f) + Vector2.Up * 3f, dim);

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

        float scale = _ringPx / Mathf.Max(1f, model.Range);

        // Блипы: модель отдаёт отсортированными (дальние первыми) — рисуем по
        // порядку, ближние ложатся поверх дальних.
        foreach (var b in model.Blips)
        {
            var pos = ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, scale);
            if (pos == null)
                continue; // за кольцом (гистерезис 1.1R) — клиппинг по кольцу
            Vector2 p = pos.Value;

            // R3: цвет — по фракции контакта (Player/Hostile/Neutral…),
            // прозрачность — fade исчезнувших.
            var color = HudTheme.FactionColor(b.Contact.Faction);
            color.A = b.Fade;

            // Подбитый контакт мигает (источник — ShipHealthState, через Damaged).
            if (b.Contact.Damaged)
                color.A *= 0.55f + 0.45f * Mathf.Sin(Time.GetTicksMsec() * 0.012f);

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

    /// <summary>Экран X/Y модели (метры, вперёд=+Y) в пиксели центра радара.
    /// null — точка за внешним кольцом (не рисуем).</summary>
    private Vector2? ProjectTo(Vector2 center, float ringPx, float screenX, float screenY, float scale)
    {
        var p = center + new Vector2(screenX * scale, -screenY * scale);
        if ((p - center).Length() > ringPx * RadarModel.HysteresisOut())
            return null;
        return p;
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

    /// <summary>Короткий штрих вектора скорости (масштаб метры→px, сclamp по длине).</summary>
    private void DrawVelocity(RadarBlip b, Vector2 p, float scale, Color color)
    {
        float vx = b.Contact.RelVel.X, vz = b.Contact.RelVel.Z;
        float speed = MathF.Sqrt(vx * vx + vz * vz);
        if (speed < 1f)
            return; // почти неподвижен — штрих не нужен
        // Проекция той же парой векторов, что и позиция (вперёд/вправо).
        var model = Source!.Model;
        float fx = Mathf.Sin(model.ForwardYaw), fz = Mathf.Cos(model.ForwardYaw);
        float sx = vx * fz + vz * -fx;
        float sy = vx * fx + vz * fz;
        var dir = new Vector2(sx, -sy);
        if (dir.LengthSquared() < 0.0001f)
            return;
        dir = dir.Normalized();
        float len = Mathf.Min(4f + speed * scale * 0.25f, 14f);
        DrawLine(p, p + dir * len, new Color(color.R, color.G, color.B, color.A * 0.6f), 1f);
    }

    // ------------------------------------------------------------- ввод мыши

    public override void _GuiInput(InputEvent @event)
    {
        // Колесо — масштаб (R2).
        if (@event is InputEventMouseButton mb && mb.Pressed)
        {
            // Колесо вверх — «приблизить» (меньше радиус → индекс ступени меньше).
            if (mb.ButtonIndex == MouseButton.WheelUp)
                SetRangeStep(RangeStepIndex - 1);
            else if (mb.ButtonIndex == MouseButton.WheelDown)
                SetRangeStep(RangeStepIndex + 1);
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
            var pos = ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, scale);
            if (pos == null)
                continue;
            float d2 = (pos.Value - at).LengthSquared();
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
        return ProjectTo(center, ringPx, b.ScreenX, b.ScreenY, scale);
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
