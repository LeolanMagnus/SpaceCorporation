using System.Collections.Generic;
using Godot;
using SpaceCorporation.UI;

namespace SpaceCorporation;

/// <summary>
/// Экранное поле захвата целей (targeting-plan): замена старого TargetMarker.
///
/// Что рисует:
///  • ВСЕ цели в радиусе радара (снимок RadarSource.Contacts) — тонкую рамку
///    из углов квадрата (размер = угловой размер цели: r = focal·SizeRadius/dist,
///    в пределах [FrameMinHalfPx, FrameMaxHalfPx]) + глиф-метку типа НАД верхним
///    левым углом рамки. Формы метки по ТЗ: станция — квадрат, курьер — трапеция,
///    грузовик — трапеция с палочкой снизу, контейнеровоз — трапеция с двумя
///    палочками, мишень — тонкий косой крестик.
///  • Цель вне экрана / за камерой — та же метка внутри квадрата-выделения,
///    прижатого к краю экрана (плюс штрих направления к центру, если включён).
///  • Захваченная (залоканная) цель — углы/квадрат становятся ЖИРНЕЕ, рисуется
///    с живой позиции узла (плавнее) и получает подпись имени+дистанции (LblTargetInfo).
///
/// Захват кликом: ЛКМ по внутренней части квадрата цели на экране либо по
/// квадрату-метке у края → TargetingService.Select. Работает только при видимом
/// курсоре (режим взаимодействия): в пилоте мышь захвачена и ЛКМ = выстрел.
///
/// Весь внешний вид настраивается ресурсом <see cref="LockMarkerSkin"/> (Skin в
/// инспекторе) — чтобы привести UI к единому дизайну/цвету без правки кода.
///
/// Проекция — вручную через view-space камеры (как у прежнего TargetMarker):
/// за камерой UnprojectPosition выворачивает координаты, а нужен честный флаг «за».
/// </summary>
public partial class TargetOverlay : Control
{
    /// <summary>Скин оформления (рамки/метки/цвета). null → дефолтный LockMarkerSkin.</summary>
    [Export] public LockMarkerSkin? Skin;

    private TargetingService _svc = null!;
    private RadarSource _radar = null!;
    private Label _lbl = null!;
    private bool _labelsOn;

    // Панель данных залоканной цели (HP/щит/скорость) — строится кодом,
    // вся типографика/цвета берутся из Skin (единый редактируемый дизайн).
    private PanelContainer _panel = null!;
    private Label _pName = null!;
    private Label _pHp = null!;
    private Label _pShield = null!;
    private Label _pSpeed = null!;


    /// <summary>Показывать ли подписи целей (хоткей L, hud_labels).</summary>
    public bool LabelsOn { get => _labelsOn; set => _labelsOn = value; }

    public override void _Ready()
    {
        // Ignore — оверлей не ест мышь (стрельба/полёт идут мимо него); клики
        // по целям ловим через _UnhandledInput при видимом курсоре.
        MouseFilter = MouseFilterEnum.Ignore;
        Skin ??= new LockMarkerSkin();

        // HudRoot — дитя HudLayer; TargetingService и RadarSource — его соседи.
        _svc = GetParent().GetNode<TargetingService>("../TargetingService");
        _radar = GetParent().GetNode<RadarSource>("../RadarSource");

        // Подпись текущей цели (имя + дистанция) — сохраняется из прежнего
        // маркера: на неё опирается проба ProbeHudTarget (путь LblTargetInfo).
        _lbl = new Label
        {
            Name = "LblTargetInfo",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _lbl.AddThemeColorOverride("font_color",
            Skin!.LabelColor.A > 0f ? Skin.LabelColor : HudTheme.TextPrimary);
        _lbl.AddThemeFontSizeOverride("font_size", Mathf.Max(6, Skin.LabelFontSize));
        AddChild(_lbl);

        BuildInfoPanel();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        UpdateLabel();
        UpdateInfoPanel();
        QueueRedraw(); // рамки/метки следуют за целью каждый кадр
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Панель данных залоканной цели (HP / щит / скорость). Все оформление
    //  — из Skin: единый редактируемый дизайн, без правок кода.
    // ─────────────────────────────────────────────────────────────────────

    private void BuildInfoPanel()
    {
        _panel = new PanelContainer { Name = "TargetInfoPanel", Visible = false };
        var bg = new StyleBoxFlat
        {
            BgColor = Skin!.TargetPanelBgColor,
            ContentMarginLeft = Mathf.Max(0f, Skin.TargetPanelPadding),
            ContentMarginRight = Mathf.Max(0f, Skin.TargetPanelPadding),
            ContentMarginTop = Mathf.Max(0f, Skin.TargetPanelPadding),
            ContentMarginBottom = Mathf.Max(0f, Skin.TargetPanelPadding),
        };
        _panel.AddThemeStyleboxOverride("panel", bg);

        var col = new VBoxContainer { Name = "Box" };
        col.AddThemeConstantOverride("separation", Mathf.Max(0, Skin.TargetPanelSeparation));
        _pName = MakePanelLabel(bold: true); _pName.Name = "LblTargetName";
        _pHp = MakePanelLabel(bold: false); _pHp.Name = "LblTargetHp";
        _pShield = MakePanelLabel(bold: false); _pShield.Name = "LblTargetShield";
        _pSpeed = MakePanelLabel(bold: false); _pSpeed.Name = "LblTargetSpeed";
        col.AddChild(_pName);
        col.AddChild(_pHp);
        col.AddChild(_pShield);
        col.AddChild(_pSpeed);
        _panel.AddChild(col);
        AddChild(_panel);
    }

    private Label MakePanelLabel(bool bold)
    {
        var l = new Label { MouseFilter = MouseFilterEnum.Ignore };
        int fs = Mathf.Max(6, Skin!.TargetPanelFontSize) + (bold ? Mathf.Max(0, Skin.TargetPanelHeaderDelta) : 0);
        l.AddThemeFontSizeOverride("font_size", fs);
        l.AddThemeColorOverride("font_color",
            Skin.TargetPanelColor.A > 0f ? Skin.TargetPanelColor : HudTheme.TextPrimary);
        return l;
    }

    /// <summary>Читает HP/щит/скорость текущей цели по её типу узла
    /// (Ship→ShipHealthState+NetState, TargetState, StationState) и заполняет
    /// панель. Shield — заглушка 0/0 (щит ещё не реализован, hud-plan H7).</summary>
    private void UpdateInfoPanel()
    {
        if (_panel == null) return;
        var t = _svc.TargetNode;
        if (!Skin!.TargetPanelEnabled || t == null || !GodotObject.IsInstanceValid(t))
        {
            _panel.Visible = false;
            return;
        }

        int hp = 0, maxHp = 0;
        float speed = 0f;

        if (t is Ship ship)
        {
            var health = ship.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (health != null) { hp = health.Hp; maxHp = health.MaxHp; }
            var net = ship.GetNodeOrNull<NetState>("NetState");
            speed = (net?.NetLinearVelocity ?? Vector3.Zero).Length();
        }
        else if (t is TargetState ts)
        {
            hp = ts.Hp; maxHp = ts.MaxHp;
        }
        else if (t is StationState st)
        {
            // Станция: поля Hp нет (статична, всегда цела) → показываем полную.
            hp = st.MaxHp; maxHp = st.MaxHp;
        }
        else
        {
            _panel.Visible = false;
            return;
        }

        _panel.Visible = true;
        _pName.Text = _svc.TargetName;
        _pName.AddThemeColorOverride("font_color", HudTheme.FactionColor(_svc.TargetFaction));

        if (maxHp > 0)
        {
            float ratio = Mathf.Clamp((float)hp / maxHp, 0f, 1f);
            _pHp.Text = $"HP  {hp} / {maxHp}";
            _pHp.AddThemeColorOverride("font_color", HudTheme.HpColor(ratio));
        }
        else
        {
            _pHp.Text = "HP  —";
            _pHp.AddThemeColorOverride("font_color", HudTheme.TextDim);
        }

        _pShield.Text = "SHIELD  0 / 0";
        _pShield.AddThemeColorOverride("font_color", HudTheme.TextDim);

        _pSpeed.Text = $"SPEED  {speed:F0} m/s";

        // Позиция — фиксированный отступ от левого-верхнего угла вьюпорта.
        _panel.Position = Skin.TargetPanelOffset;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Подпись залоканной цели (имя + дистанция) — единственный текст оверлея.
    // ─────────────────────────────────────────────────────────────────────

    private void UpdateLabel()
    {
        var t = _svc.TargetNode;
        if (t == null || !GodotObject.IsInstanceValid(t))
        {
            _lbl.Visible = false;
            return;
        }
        if (!TargetingService.WorldPosOf(t, out Vector3 wpos)) { _lbl.Visible = false; return; }
        var cam = GetViewport().GetCamera3D();
        if (cam == null) { _lbl.Visible = false; return; }

        float dist = cam.GlobalPosition.DistanceTo(wpos);
        _lbl.Visible = true;
        _lbl.Text = $"{_svc.TargetName}\n{FormatDist(dist)}";
        // Цвет подписи цели — по её фракции (H4), как у прежнего маркера.
        Color c = Skin != null && Skin.LabelColor.A > 0f ? Skin.LabelColor : HudTheme.FactionColor(_svc.TargetFaction);
        _lbl.AddThemeColorOverride("font_color", c);
        if (Skin != null)
            _lbl.AddThemeFontSizeOverride("font_size", Skin.LabelFontSize);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Отрисовка
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Хит-зоны за последний кадр кликабельных целей: сетевой id,
    /// центр на экране, полуразмер зоны, флаг «прижата к краю». Нужны чтобы
    /// кликом внутрь квадрата/метки взять цель в лок (targeting-plan).</summary>
    private readonly List<(int NetId, Vector2 Center, float Half, bool Edge)> _hits = new(32);

    public override void _Draw()
    {
        _hits.Clear();
        if (_svc == null || _radar == null) return;
        var cam = GetViewport().GetCamera3D();
        if (cam == null || Size.X <= 0f || Size.Y <= 0f) return;

        var skin = Skin!;
        Vector2 center = Size * 0.5f;
        Transform3D xf = cam.GlobalTransform;
        Vector3 camPos = xf.Origin;
        Vector3 fwd = -xf.Basis.Z, right = xf.Basis.X, up = xf.Basis.Y;
        // Фокальная длина в пикселях: fy = (H/2)/tan(fov/2). Полуразмер рамки
        // на экране = fy · (габаритный радиус · запас) / дистанция (угловой размер).
        float fy = Size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f);
        float range = _radar.Model.Range;
        float age = _radar.ContactAge;
        Vector3 sync = _radar.SyncCenter;
        int lockedId = NetIdOf(_svc.TargetNode);
        float margin = Mathf.Max(skin.EdgeMarginPx, 2f);

        // ── все живые контакты в радиусе радара (кроме залоканной) ──
        foreach (var c in _radar.Contacts)
        {
            Vector3 rel = new(c.RelPos.X, c.RelPos.Y, c.RelPos.Z);
            if (rel.LengthSquared() > range * range)
                continue; // вне радиуса радара — не подсвечиваем
            if (c.NetId != 0 && c.NetId == lockedId)
                continue; // залоканную рисуем отдельно (жирно + подпись)

            // Мировая экстраполяция по АБСОЛЮТНОЙ скорости контакта (AbsVel),
            // а не относительной и без скорости своего корабля:
            // world = pos_atSync + vцели·age. Статичные сущности (AbsVel=0)
            // дают точную неподвижную позицию — выделение не дёргается ни у
            // станции, ни у будущих мусора/астероидов/обломков/контейнеров;
            // движущиеся идут ровно своей скоростью между сборками радара.
            Vector3 absVel = new(c.AbsVel.X, c.AbsVel.Y, c.AbsVel.Z);
            Vector3 wpos = sync + rel + absVel * age; // экстраполяция до «сейчас»
            float dist = camPos.DistanceTo(wpos);
            if (dist < 0.5f) continue;

            float r = fy * c.SizeRadius * skin.FramePadding * skin.AngularScale / dist;
            r = Mathf.Clamp(r, skin.FrameMinHalfPx, skin.FrameMaxHalfPx);
            Color col = MarkerColor(c.Faction, c.Shape, c.ShipTypeValue, skin, locked: false);
            float w = skin.CornerWidthThin;

            DrawContact(c.NetId, c.Shape, c.ShipTypeValue, wpos, camPos, fwd, right, up,
                xf, fy, center, margin, r, col, w, skin);
        }

        // ── залоканная цель: с живой позиции узла (плавнее), жирные углы ──
        if (_svc.TargetNode != null && GodotObject.IsInstanceValid(_svc.TargetNode)
            && TargetingService.WorldPosOf(_svc.TargetNode, out Vector3 wlock))
        {
            byte shape = ShapeOf(_svc.TargetNode);
            byte shipType = ShipTypeByteOf(_svc.TargetNode);
            float sizeR = SizeRadiusOf(shape, shipType);
            float dist = Mathf.Max(camPos.DistanceTo(wlock), 1f);
            float r = fy * sizeR * skin.FramePadding * skin.AngularScale / dist;
            r = Mathf.Clamp(r, skin.FrameMinHalfPx, skin.FrameMaxHalfPx);
            Color col = MarkerColor(_svc.TargetFaction, shape, shipType, skin, locked: true);
            float w = skin.CornerWidthLocked;

            bool drawnOnScreen = false;
            Vector2 screen = Vector2.Zero;
            if (!IsBehind(wlock, xf, camPos))
            {
                Vector2 p = Project(xf, fy, center, wlock);
                if (InsideRect(p, margin))
                {
                    DrawCornerFrame(p, r, col, w, skin);
                    DrawTypeMarker(p - new Vector2(r + skin.MarkerSizePx * 0.5f, r + skin.MarkerSizePx * 0.5f),
                        shape, shipType, col, skin);
                    _hits.Add((lockedId, p, r, false));
                    drawnOnScreen = true;
                    screen = p;
                }
            }
            if (!drawnOnScreen)
            {
                Vector2 edge = EdgePoint(wlock, camPos, fwd, right, up, center, margin, skin);
                DrawEdgeContact(lockedId, shape, shipType, edge, col, w, skin);
                screen = edge;
            }
            PlaceLabelAt(screen, margin);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Рисование одного контакта (на экране → рамка+метка; иначе → край)
    // ─────────────────────────────────────────────────────────────────────

    private void DrawContact(int netId, byte shape, byte shipType, Vector3 wpos,
        Vector3 camPos, Vector3 fwd, Vector3 right, Vector3 up, Transform3D xf, float fy,
        Vector2 center, float margin, float r, Color col, float w, LockMarkerSkin skin)
    {
        if (!IsBehind(wpos, xf, camPos))
        {
            Vector2 p = Project(xf, fy, center, wpos);
            if (InsideRect(p, margin))
            {
                DrawCornerFrame(p, r, col, w, skin);
                DrawTypeMarker(p - new Vector2(r + skin.MarkerSizePx * 0.5f, r + skin.MarkerSizePx * 0.5f),
                    shape, shipType, col, skin);
                _hits.Add((netId, p, r, false));
                return;
            }
        }
        Vector2 edge = EdgePoint(wpos, camPos, fwd, right, up, center, margin, skin);
        DrawEdgeContact(netId, shape, shipType, edge, col, w, skin);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Геометрия проекции
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>За камерой? local.Z >= -порог (ось −Z вперёд у камеры).</summary>
    private static bool IsBehind(Vector3 wpos, Transform3D xf, Vector3 camPos)
    {
        Vector3 local = xf.AffineInverse() * wpos;
        return local.Z >= -0.5f;
    }

    /// <summary>Экранные координаты цели перед камеры (центр = Size/2).</summary>
    private Vector2 Project(Transform3D xf, float fy, Vector2 center, Vector3 wpos)
    {
        Vector3 local = xf.AffineInverse() * wpos;
        float zz = -local.Z;
        if (zz < 0.01f) zz = 0.01f;
        return center + new Vector2(local.X * fy / zz, -local.Y * fy / zz);
    }

    /// <summary>Лежит ли точка в поля экрана с отступом margin (иначе — «за экраном»).</summary>
    private bool InsideRect(Vector2 p, float margin)
        => p.X >= margin && p.X <= Size.X - margin && p.Y >= margin && p.Y <= Size.Y - margin;

    /// <summary>Точка на прямоугольнике экрана (с отступом) вдоль направления dir от центра.</summary>
    private Vector2 ClampToEdge(Vector2 center, Vector2 dir, float margin)
    {
        if (dir.LengthSquared() < 0.0001f) dir = new Vector2(0, 1);
        Vector2 half = new(Size.X * 0.5f - margin, Size.Y * 0.5f - margin);
        Vector2 n = dir.Normalized();
        float tx = n.X != 0 ? half.X / Mathf.Abs(n.X) : float.PositiveInfinity;
        float ty = n.Y != 0 ? half.Y / Mathf.Abs(n.Y) : float.PositiveInfinity;
        return center + n * Mathf.Min(tx, ty);
    }

    /// <summary>Крайняя точка «за экраном»: куда прижать метку цели. Цель за
    /// камерой → направление от бокового отклонения (X4-образно), как у маркера.</summary>
    private Vector2 EdgePoint(Vector3 wpos, Vector3 camPos, Vector3 fwd, Vector3 right, Vector3 up,
        Vector2 center, float margin, LockMarkerSkin skin)
    {
        Vector3 d = wpos - camPos;
        float z = d.Dot(fwd); // перед камеры > 0
        if (z >= -0.5f)
        {
            // за камерой (или почти на ней): направление = боковая сторона,
            // где реально лежит цель (тот же знак, что у экранной проекции:
            // +X вправо = d·right, +Y вниз = −d·up). Не инвертируем на 180°,
            // иначе метка прыгает на противоположный край при уходе за спину.
            float x = d.Dot(right), y = d.Dot(up);
            var dir = new Vector2(x, -y); // экран: +X вправо, +Y вниз = −up
            if (dir.LengthSquared() < 0.0001f) dir = new Vector2(0, 1);
            return ClampToEdge(center, dir, margin);
        }
        // видима впереди, но вне поля → направление от центра к проекции.
        float fy = Size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(GetViewport().GetCamera3D()?.Fov ?? 75f) * 0.5f);
        Vector2 p = center + new Vector2(d.Dot(right) * fy / z, -d.Dot(up) * fy / z);
        return ClampToEdge(center, p - center, margin);
    }


    // ─────────────────────────────────────────────────────────────────────
    //  Рамка-выделение (углы квадрата) и глиф-метка типа
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Рамка из 4 уголков (ломаные, X4-образно). Толщина w — тонкая у
    /// обычных целей, жирная у залоканной. Длина уголка = r·CornerLenFraction.</summary>
    private void DrawCornerFrame(Vector2 c, float r, Color col, float w, LockMarkerSkin skin)
    {
        float h = r, k = r * Mathf.Clamp(skin.CornerLenFraction, 0.05f, 1f);
        var pts = new (Vector2 a, Vector2 b, Vector2 d)[]
        {
            (new(c.X - h, c.Y - h), new(c.X - h + k, c.Y - h), new(c.X - h, c.Y - h + k)),
            (new(c.X + h, c.Y - h), new(c.X + h - k, c.Y - h), new(c.X + h, c.Y - h + k)),
            (new(c.X - h, c.Y + h), new(c.X - h + k, c.Y + h), new(c.X - h, c.Y + h - k)),
            (new(c.X + h, c.Y + h), new(c.X + h - k, c.Y + h), new(c.X + h, c.Y + h - k)),
        };
        foreach (var (a, b, d) in pts)
        {
            DrawLine(a, b, col, w);
            DrawLine(a, d, col, w);
        }
    }

    private void DrawTypeMarker(Vector2 center, byte shape, byte shipType, Color col, LockMarkerSkin skin)
    {
        float m = skin.MarkerSizePx * 0.5f, w = skin.MarkerWidth;

        if (shape == RadarShape.Station)
        {
            var s0 = new Vector2(center.X - m, center.Y - m);
            var s1 = new Vector2(center.X + m, center.Y - m);
            var s2 = new Vector2(center.X + m, center.Y + m);
            var s3 = new Vector2(center.X - m, center.Y + m);
            DrawLine(s0, s1, col, w); DrawLine(s1, s2, col, w);
            DrawLine(s2, s3, col, w); DrawLine(s3, s0, col, w);
            return;
        }
        if (shape == RadarShape.Target)
        {
            // тонкий косой крестик: две диагонали (X повёрнут на ~20°).
            float a = Mathf.DegToRad(20f);
            Vector2 u = new(Mathf.Cos(a), Mathf.Sin(a));
            Vector2 v = new(Mathf.Cos(a + Mathf.Pi * 0.5f), Mathf.Sin(a + Mathf.Pi * 0.5f));
            DrawLine(center - u * m, center + u * m, col, w);
            DrawLine(center - v * m, center + v * m, col, w);
            return;
        }
        // ship: трапеция + палочки снизу по типу (курьер 0 / грузовик 1 / контейнеровоз 2).
        float top = m * 0.45f, bot = m * 0.8f, baseY = center.Y - m * 0.5f;
        Vector2 tl = new(center.X - top, baseY), tr = new(center.X + top, baseY);
        Vector2 bl = new(center.X - bot, baseY + m), br = new(center.X + bot, baseY + m);
        DrawLine(tl, tr, col, w);
        DrawLine(bl, br, col, w);
        DrawLine(tl, bl, col, w);
        DrawLine(tr, br, col, w);

        int sticks = shipType switch { (byte)ShipType.Hauler => 1, (byte)ShipType.ContainerFreighter => 2, _ => 0 };
        float stickLen = m * 0.5f, by = baseY + m;
        if (sticks == 1)
            DrawLine(new Vector2(center.X, by), new Vector2(center.X, by + stickLen), col, w);
        else if (sticks >= 2)
        {
            float off = bot * 0.5f;
            DrawLine(new Vector2(center.X - off, by), new Vector2(center.X - off, by + stickLen), col, w);
            DrawLine(new Vector2(center.X + off, by), new Vector2(center.X + off, by + stickLen), col, w);
        }
    }

    /// <summary>Метка цели «за экраном»: квадрат-выделение у края + глиф типа
    /// внутри + опциональный штрих направления к центру. Хит-зона = квадрат.</summary>
    private void DrawEdgeContact(int netId, byte shape, byte shipType, Vector2 edge, Color col, float w, LockMarkerSkin skin)
    {
        float hb = skin.EdgeBoxHalfPx;
        DrawCornerFrame(edge, hb, col, w, skin);
        DrawTypeMarker(edge, shape, shipType, col, skin);
        if (skin.EdgeShowTick)
        {
            Vector2 n = (Size * 0.5f - edge);
            if (n.LengthSquared() > 0.0001f)
            {
                n = n.Normalized();
                float from = hb + 2f;
                DrawLine(edge + n * from, edge + n * (from + skin.EdgeTickLengthPx), col, skin.MarkerWidth);
            }
        }
        _hits.Add((netId, edge, hb, true));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Цвета/данные цели
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Цвет метки: Override (MarkerColor/LockColor, alpha>0) иначе
    /// HudTheme.FactionColor(фракция).</summary>
    private static Color MarkerColor(byte faction, byte shape, byte shipType, LockMarkerSkin skin, bool locked)
    {
        if (locked && skin.LockColor.A > 0f) return skin.LockColor;
        if (!locked && skin.MarkerColor.A > 0f) return skin.MarkerColor;
        return HudTheme.FactionColor(faction);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Подпись цели (кламп к краю)
    // ─────────────────────────────────────────────────────────────────────

    private void PlaceLabelAt(Vector2 p, float margin)
    {
        float wLbl = Skin!.LabelWidthPx;
        float hLbl = Mathf.Max(16f, Skin.LabelHeightPx);
        _lbl.Position = new Vector2(
            Mathf.Clamp(p.X - wLbl * 0.5f, 4, Size.X - wLbl - 4),
            Mathf.Clamp(p.Y + margin + 4, 4, Size.Y - hLbl - 4));
        _lbl.Size = new Vector2(wLbl, hLbl);
    }

    private static string FormatDist(float m) =>
        m >= 1000f ? $"{m / 1000f:F2} km" : $"{m:F0} m";

    // ─────────────────────────────────────────────────────────────────────
    //  Данные о залекованном узле: id / форма / тип / габарит
    // ─────────────────────────────────────────────────────────────────────

    private static int NetIdOf(Node? n)
    {
        if (n == null) return 0;
        if (n is Ship s) return s.Net?.ShipNetId ?? 0;
        if (n is TargetState t) return t.TargetNetId;
        if (n is StationState st) return st.StationNetId;
        if (n.GetParent() is Ship sp) return sp.Net?.ShipNetId ?? 0;
        if (n.GetParent() is TargetState tp) return tp.TargetNetId;
        return 0;
    }

    private static byte ShapeOf(Node? n)
    {
        if (n == null) return RadarShape.Ship;
        if (n is StationState) return RadarShape.Station;
        if (n is TargetState) return RadarShape.Target;
        if (n.GetParent() is StationState) return RadarShape.Station;
        if (n.GetParent() is TargetState) return RadarShape.Target;
        return RadarShape.Ship;
    }

    private static byte ShipTypeByteOf(Node? n)
    {
        if (n is Ship s) return s.Net?.ShipTypeValue ?? (byte)ShipType.Courier;
        if (n?.GetParent() is Ship sp) return sp.Net?.ShipTypeValue ?? (byte)ShipType.Courier;
        return (byte)ShipType.Courier;
    }

    private static float SizeRadiusOf(byte shape, byte shipType)
    {
        if (shape == RadarShape.Station) return RadarSource.StationSizeRadius;
        if (shape == RadarShape.Target) return RadarSource.TargetSizeRadius;
        return RadarSource.HullRadiusOf(shipType);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Захват кликом (ЛКМ по внутренней части рамки / по краю-метке)
    // ─────────────────────────────────────────────────────────────────────

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        // Только в режиме взаимодействия (курсор виден): в пилоте мышь
        // захвачена и ЛКМ = выстрел — оверлей не должен мешать.
        if (Input.MouseMode != Input.MouseModeEnum.Visible) return;
        if (e is not InputEventMouseButton mb || !mb.Pressed || mb.ButtonIndex != MouseButton.Left)
            return;

        Vector2 mp = mb.GlobalPosition;
        foreach (var (netId, c, half, edge) in _hits)
        {
            var box = new Rect2(c.X - half, c.Y - half, half * 2f, half * 2f);
            if (box.HasPoint(mp))
            {
                // клик по текущей цели — снять лок (переключатель), иначе — взять.
                if (netId == NetIdOf(_svc.TargetNode)) _svc.Clear();
                else _svc.Select(netId);
                GetViewport().SetInputAsHandled();
                return;
            }
        }
    }
}
