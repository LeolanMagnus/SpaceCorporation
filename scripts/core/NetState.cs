using Godot;

namespace SpaceCorporation;

/// <summary>
/// Узел-хранилище «сетевого» состояния тела (решение В6: реплицируем состояние,
/// а не трансформ `RigidBody3D`). Висит дочерним узлом у `Ship` (позже у других
/// симулируемых объектов) и настраивается `MultiplayerSynchronizer` с
/// `RootPath = ".."`; в `SceneReplicationConfig` свойства адресуются от RootPath,
/// то как `NetState/NetPosition` (§3.3).
///
/// Свойства обязаны быть `[Export] public ... { get; set; }`: Godot реплицирует и
/// считает спавн-снимком только свойства, видимые его отражению. C#-поле без
/// атрибута репликацией не покрывается, и снимок молча не приходит (§3.11 п.2).
///
/// Здесь НЕТ `GlobalPosition`/`GlobalRotation` самого тела: писать репликой в
/// трансформ `RigidBody3D` — конфликт с локальной физикой каждый кадр (§1.11).
/// Картинку интерполирует узел `ShipHull`, тело двигает только авторитет.
/// </summary>
public partial class NetState : Node
{
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public Quaternion NetRotation { get; set; } = Quaternion.Identity;
    [Export] public Vector3 NetLinearVelocity { get; set; }
    [Export] public Vector3 NetAngularVelocity { get; set; }

    /// <summary>Физический шаг (`Engine.GetPhysicsFrames()`), на котором авторитет
    /// сделал снимок. По нему принимающий отбрасывает устаревшие репорты.</summary>
    [Export] public int SimTick { get; set; }

    /// <summary>Сетевой адрес владельца (один монотонный счётчик на все спавнящиеся
    /// объекты мира, §3.4 п.2). Выдаёт сервер ДО `AddChild` (§4 В4 п.3);
    /// 0 = не назначен — `Ship._Ready()` обязан сообщить об этом в отладку.</summary>
    [Export] public int ShipNetId { get; set; }

    /// <summary>Адрес выдаёт НЕ этот узел: в сети номер назначает сервер до
    /// `AddChild` (§4 В4 п.3), иначе два пира выдали бы разные номера одному
    /// кораблю. В offline (`Role == Offline`, один процесс) адрес выдаёт
    /// `NetworkManager` — счётчик тот же, поэтому offline и сеть не разъезжаются
    /// (§1.8), а панель показывает `Ship#1` и в одиночном режиме (§3.1 п.6).
    /// `_EnterTree`, а не `_Ready`: обход при входе идёт сверху вниз, поэтому
    /// `AttachPoint` (дочерний узел) увидит уже назначенный `OwnerNetId`.</summary>
    public override void _EnterTree()
    {
        NetworkManager.Instance?.AssignNetIdIfUnset(this);
    }
}
