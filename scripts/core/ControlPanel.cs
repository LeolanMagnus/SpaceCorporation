using Godot;

namespace SpaceCorporation;

/// <summary>
/// eva-plan Блок E (Е1): панель управления у каждого корабля — физический
/// «щит»-мишень для hover-подсказки «{клавиша} сесть за штурвал».
///
/// Это НЕ интерактивный узел: клик по ней не ловится (в headless мыши нет,
/// а 3D-UI-области у панели нет до проекции). Луч из камеры находит форму
/// панели, игрок видит подсказку, а активация идёт по сетевому маршруту
/// `Player.RequestAttach` — тому же, что клавиша F.
///
/// Геометрия: вертикаль = `Player.CapsuleHeight / 4 = 0.45` м (ТЗ: «четверть
/// роста игрока»), ширина/глубина — такой же квадрат (ФЛАГ В27). Потомок
/// hull ⇒ едет вместе с кораблём. Ставится ПЕРЕД креслом `AttachPoints/Seat`
/// и слегка наружу по носу: луч сexterna сначала цепляет панель, потом
/// обшивку — панель всегда передняя по лучу, если игрок на неё смотрит.
///
/// Слой `ControlPanel` (бит 10), маска 0 — ни с чем не сталкивается, только
/// облучается (`EvaRayMask`). `Monitorable` — участвует в query-лучах.
/// Видимости не имеет вовсе (узла-меша нет): в Godot 4 `PhysicsBody3D`
/// участвует в `IntersectRay` независимо от визуала — проверяем пробою
/// Stage7Test п.4 (луч находит панель без единого меша).
///
/// Адрес кресла НЕ дублируем полями: держим ссылку на `AttachPoint` кресла
/// (предпочтение eva-plan — «меньше мест, где адрес можно рассинхронить»);
/// `OwnerNetId`/`NetId` читаем из неё.
/// </summary>
public partial class ControlPanel : Area3D
{
    /// <summary>Вертикаль панели — четверть капсулы игрока (ТЗ, ФЛАГ В27).</summary>
    public const float PanelSizeM = Player.CapsuleHeight / 4f; // 0.45

    /// <summary>Путь к креслу этого корабля (в сцене — сосед `AttachPoints/Seat`).
    /// Через него панель отдаёт сетевой адрес кресла.</summary>
    [Export] public NodePath SeatPath { get; set; } = new NodePath();

    private AttachPoint? _seat;

    /// <summary>Кресло, к «пристёгивает» эта панель (после _Ready).</summary>
    public AttachPoint? Seat => _seat;

    /// <summary>Сетевой адрес владельца (ShipNetId кресла). 0 — кресло ещё
    /// не получило адрес (или ссылка не настроена).</summary>
    public int OwnerNetId => _seat?.OwnerNetId ?? 0;

    /// <summary>Номер точки-кресла внутри владельца (AttachPoint.NetId).</summary>
    public int SeatPointNetId => _seat?.NetId ?? 0;

    public override void _Ready()
    {
        CollisionLayer = CollisionLayers.ControlPanel;
        CollisionMask = 0u;
        // Monitorable=true по умолчанию у PhysicsBody3D: панель участвует в
        // query-лучах (IntersectRay) даже без единого меша и без маски.

        if (string.IsNullOrEmpty(SeatPath.ToString()))
        {
            GD.PrintErr("ControlPanel: SeatPath не задан — панель без кресла, " +
                        "hover будет отказывать");
            return;
        }
        _seat = GetNodeOrNull<AttachPoint>(SeatPath);
        if (_seat == null)
            GD.PrintErr($"ControlPanel: кресло по пути «{SeatPath}» не найдено");

        BuildVisual();
    }

    /// <summary>
    /// Правка 7-й части: панель была физически присутствующей, но абсолютно
    /// невидимой (узла-меша не заводили) — игрок не видел, куда наводить луч,
    /// чтобы сесть за штурвал. Добавляем видимый красный куб по габариту
    /// коллизии (`PanelSizeM` = 0.45) с лёгким свечением (emission), чтобы
    /// панель читалась на тёмном фоне космоса. Строим кодом в `_Ready` — у
    /// панели нет сценного меша, и такой подход не трогает .tscn (меньше мест,
    /// где визуал можно рассинхронить с коллизией).
    /// </summary>
    private void BuildVisual()
    {
        if (GetNodeOrNull<MeshInstance3D>("Visual") != null)
            return; // идемпотентно: повторный _Ready не плодит второй меш

        var box = new BoxMesh { Size = new Vector3(PanelSizeM, PanelSizeM, PanelSizeM) };

        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.85f, 0.12f, 0.12f), // красный
            // Немного самоподсветки: панель должна быть заметна и в тени корабля.
            EmissionEnabled = true,
            Emission = new Color(0.7f, 0.06f, 0.06f),
            EmissionEnergyMultiplier = 1.0f,
            VertexColorUseAsAlbedo = false,
        };

        var mesh = new MeshInstance3D
        {
            Name = "Visual",
            Mesh = box,
            MaterialOverride = mat,
            // Меш чисто визуальный: не участвует в физике (у Area3D mask=0,
            // MeshInstance3D и так не является телом).
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(mesh);
    }
}
