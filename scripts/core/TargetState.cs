using Godot;

namespace SpaceCorporation;

/// <summary>
/// Узел-хранилище сетевого состояния учебной мишени (этап 3, §3 плана). По
/// образцу <see cref="NetState"/>: висит дочерним узлом у корня мишени
/// (`Target.tscn`), настраивается `MultiplayerSynchronizer` с `RootPath = ".."`,
/// а `TargetReplication.tres` адресует свойства от RootPath (`NetState:Hp`).
///
/// ПОЧЕМУ ОТДЕЛЬНЫЙ КЛАСС, а не наследник `NetState`: у мишени СВОЙ набор
/// полей и СВОЁ имя адреса (`TargetNetId`) — план §3.4 п.2 требует `TargetNetId`,
/// а не переиспользовать `ShipNetId`, и сервер при хитскане сверяет
/// «`TargetNetId`, которого нет в реестре» как отдельный класс. Наследование от
/// `NetState` только запутало бы (там лишние Ship'овские AppliedTick/AppliedMask,
/// а `ShipNetId` — не то имя, под которым номер ищут в реестре мишеней).
///
/// Свойства ОБЯЗАНЫ быть `[Export] public ... { get; set; }`: Godot реплицирует и
/// кладёт в спавн-снимок только свойства, видимые отражению (§3.11 п.2; C#-поле
/// без атрибута репликацией не покрывается и снимок молча не приходит).
/// </summary>
public partial class TargetState : Node
{
    /// <summary>Адрес мишени из ОБЩЕГО счётчика спавнящихся объектов мира
    /// (тот же `NetworkManager.NextNetId()`, что и ShipNetId/StationNetId —
    /// §3.4 п.2: одна нумерация, чтобы id не сталкивались между типами).
    /// Реплицируется в спавн-снимке: без него клиент не сможет сопоставить
    /// спавнящийся узел с реестром мишеней и RPC урона не найдут цель (§3.4 п.2).</summary>
    [Export] public int TargetNetId { get; set; }

    /// <summary>Текущее здоровье. Меняет ТОЛЬКО авторитет (сервер, §3.3 п.5);
    /// в конфиге спавн + ON_CHANGE, чтобы и первоначальное значение, и каждое
    /// падение от выстрела долетали до пира (нужно для визуала HP-полоски).</summary>
    [Export] public int Hp { get; set; }

    /// <summary>Максимум здоровья (для полоски). Спавн-снимок, дальше не меняется.</summary>
    [Export] public int MaxHp { get; set; } = 100;

    // Позиция/поворот/угловая скорость реплицируются (позиция/поворот — спавн-
    // снимок + дальнейшая синхронизация ON_CHANGE; угловая — всегда, для
    // интерполяции наблюдателем). У мишени в 3.1 авторитет её не двигает, но
    // поля в конфиге нужны: по ним клиент интерполирует картинку, а assert п.14
    // проверяет, что пути/флаги позиции заданы ВЕРНО (spawn=false у позиции —
    // позиция есть не у всех объектов, её гонят отдельным флагом, не спавном).
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public Quaternion NetRotation { get; set; } = Quaternion.Identity;
    [Export] public Vector3 NetAngularVelocity { get; set; }

    // Точка и тик ПОСЛЕДНЕГО попадания (этап 3 §3.4 п.4: `HitPoint`/`HitTick`).
    // Пишет только авторитет (сервер) в момент подтверждения хитскана (шаг 3.6),
    // реплицируется спавн-снимком + ON_CHANGE, чтобы картинка всплеска в точке
    // удара появилась у всех пирам. Держим на TargetState, а не на отдельном
    // сигнале: тогда у поздно зашедшего пира в снимке будет и последняя точка
    // (иначе он увидит только HP без следа последнего выстрела).
    [Export] public Vector3 HitPoint { get; set; }
    [Export] public uint HitTick { get; set; }

    /// <summary>Регистрация в реестре мишеней по адресу (по образцу
    /// <see cref="AttachPoint"/> — реестр по адресу, а не по пути узла: у
    /// спавн-сцены путь на разных пирах разный, а адрес один).
    /// <see cref="_EnterTree"/>, а не <see cref="Node._Ready"/>: адрес обязан быть
    /// назначен ДО входа узла в дерево у сервера (выдаёт спавнер до AddChild),
    /// но и оффлайн-спавн должен получить адрес здесь же — иначе реестр пуст.</summary>
    public override void _EnterTree()
    {
        // Адрес выдаёт авторитет (offline/сервер) до AddChild; на клиенте узел
        // создаёт MultiplayerSpawner, и TargetNetId придёт с репликой.
        NetworkManager.Instance?.AssignTargetNetIdIfUnset(this);
    }

    public override void _Ready()
    {
        NetworkManager.Instance?.RegisterTarget(this);
    }

    public override void _ExitTree()
    {
        NetworkManager.Instance?.UnregisterTarget(this);
    }
}
