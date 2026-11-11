using Godot;
using System.Text;

namespace SpaceCorporation.Tests;

/// <summary>
/// Стенд Stage7Test (EVA, eva-plan §3.6): мир стенда + снимки скафандра +
/// протокол проверки. Отделён от самих проверок, чтобы файл теста читался как
/// чек-лист (образец — Stage5Test.Helpers.cs / Stage6Test.cs).
///
/// Как крутится физика: у стенда — свой `PhysicsProcess`, а не `_PhysicsProcess`
/// (сцена-автостарт, см. Stage6Test.tscn: `physics_process/... = true`). Внутри
/// каждого шага Godot делает физический шаг, поэтому у стендового игрока
/// `_PhysicsProcess` вызывается сам (0-й шаг, сразу после переноса в дерево —
/// «окошко» до первого MoveAndSlide, §3 п.2). Между шагами стенда этот кадр
/// не крутится: тест сам решает, когда дёргать EVA-шаг (шов `EvaTestStep`).
/// </summary>
public partial class Stage7Test : Node
{
    protected Node _world = null!;
    protected StaticBody3D _deck = null!; // палуба-примитив (Блок F1: layer Deck, mask 0)
    protected Node _ship = null!;       // живая сцена Ship.tscn (даёт кресло Seat)
    protected Player _player = null!;   // испытуемый

    protected const string ShipGroup = "ship";

    // ─────────────────────────────────────────────────────────────────────
    // Мир стенда
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Мир стенда: `WorldRoot` (Node3D, как в Main.tscn — его и находит
    /// `FindWorldRoot` при отстыковке) + корабль со сцены (кресло) + палуба по
    /// образцу eva-plan F1 (StaticBody3D, layer Deck, mask 0) + игрок.
    /// `shipInTree = false` — корабль-донор: сцена собрана, но в дерево не
    /// перенесена (для проверок вида «кресло чужого корабля»).</summary>
    protected void BuildEvaWorld(bool shipInTree = true)
    {
        _world = new Node3D { Name = "WorldRoot" };
        AddChild(_world);

        _ship = ((PackedScene)GD.Load("res://scenes/Ship.tscn")).Instantiate();
        _ship.Name = "TestShip";
        // Корабль-донор заморожен, НО остаётся в физическом пространстве:
        // `ProcessMode = Disabled` (PAUSE_MODE_STOP) у `RigidBody3D` вычёркивает
        // тело (и вложенную в него `Area3D`-панель) из `PhysicsDirectSpaceState` —
        // hover-луч п.4 не нашёл бы ни корпуса, ни панели. Заморозка через
        // `Freeze` + `FreezeMode.Static` держит тело в пространстве и не даёт
        // ему падать/двигаться: кресло и панель — статичный ориентир для луча.
        if (_ship is RigidBody3D rb)
        {
            rb.Freeze = true;
            rb.FreezeMode = RigidBody3D.FreezeModeEnum.Static;
        }
        if (shipInTree)
            _world.AddChild(_ship);

        _deck = new StaticBody3D
        {
            Name = "DeckHull",
            CollisionLayer = CollisionLayers.Deck,
            CollisionMask = 0u,
            Transform = new Transform3D(Basis.Identity, new Vector3(0f, -1.05f, 0f)),
        };
        _deck.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(12f, 1f, 12f) },
        });
        _world.AddChild(_deck);

        SpawnPlayer();
    }

    /// <summary>Испытуемый игрок из той же сцены, что в Main.tscn, свободный
    /// (не пристёгнут) и с чистой физикой: автокрепление выключено ДО переноса в
    /// дерево, чтобы игрок не улетел в кресло раньше первого проверочного шага.</summary>
    protected void SpawnPlayer(Vector3 at = default)
    {
        if (at == default)
            at = new Vector3(0f, 1.05f + Player.CapsuleHeight * 0.5f, 0f);

        _player = GD.Load<PackedScene>("res://scenes/Player.tscn")
            .Instantiate<SpaceCorporation.Player>();
        _player.Name = "TestPlayer";
        _player.AutoAttachOnSpawn = false;
        _world.AddChild(_player);
        // Проверка занятости кресла (`IsSeatTakenBy`) перебирает группу "player"
        // — в бою её вешает WorldSpawner.Spawn.cs; стенд добавляет игрока прямо в
        // мир, поэтому повторяем группировку здесь.
        _player.AddToGroup("player");
        _player.GlobalPosition = at;
        _player.Velocity = Vector3.Zero;
        // Физический шаг стендовому тесту не нужен: игрок «шагает» явными
        // EvaTestStep/EvaTestSuitSeatStep. На паузе реальный _PhysicsProcess не
        // разгоняет тело на реальном delta с пустым кадром между фазами
        // (остаточная скорость Frame-фаз уносила игрока за десятки метров —
        // именно так Boot-фазы стартовали в 30 м от палубы).
        _player.EvaTestSetPhysicsPaused(true);
    }

    /// <summary>Очистить мир стенда (перед следующим сценарием) и собрать заново.
    /// Нужно, потому что один и тот же игрок не может быть «и в кресле, и в
    /// джетпаке» одновременно — каждый сценарий начинается с чистого тела.</summary>
    /// <summary>Выключить все коллайдеры под деревом (донор-корабль в
    /// стендовых тестах ботинок: Frozen RigidBody3D держит свою позу тела в
    /// физсервере, перенос ноды не меняет позу форм, а QueueFree в стенде
    /// не исполняется). CollisionShape3D.Disabled=true — форма вне лучей.</summary>
    protected static void DisableShipColliders(Node root)
    {
        if (root is CollisionShape3D cs)
            cs.Disabled = true;
        else if (root is CollisionShape2D)
            ((CollisionShape2D)root).Disabled = true;
        foreach (Node ch in root.GetChildren())
            DisableShipColliders(ch);
    }

    protected void RebuildWorld()
    {
        if (_world != null && IsInstanceValid(_world))
        {
            // Коллайдеры уходящего мира гасим ДО QueueFree: удаление отложено
            // до конца кадра, а их свободные боксы весь следующий сценарий
            // попапались бы магнитным лучам ботинок (фантомный DeckHull «держал»
            // игрока вместо свежей палубы).
            DisableShipColliders(_world);
            _world.QueueFree();
        }
        // QueueFree отложен, а free-space-боксы удалённых тел ещё могут попадаться
        // лучам, поэтому ждём реального удаления в физическом шаге.
        for (int i = 0; i < 3; i++)
            ForceSyncFrame();
        BuildEvaWorld();
    }

    /// <summary>Синхронизация с перемещением носителя (палуба/корабль уехали).
    /// В Godot 4 присваивание `GlobalPosition`/`GlobalRotationDegrees` узлу
    /// синхронно обновляет и его трансформ, и трансформ форм в физическом
    /// сервере (NOTIFICATION_TRANSFORM_CHANGED пушет состояние сразу), а
    /// `ToGlobal`/дочерние трансформы читаются уже по новому базису. Драйвер
    /// живёт ВНУТРИ `_PhysicsProcess`, поэтому выбрасывать отсюда ещё один
    /// кадр нельзя (рекурсивно докрутился бы тот же шаг фазовой машины).
    /// Кадровых задержек здесь нет — только заметка, почему метод пустой.</summary>
    protected void ForceSyncFrame()
    {
    }

    // ─────────────────────────────────────────────────────────────────────
    // Шаги и снимки
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>N шагов EVA по 1/60 с (шов Player.Eva.cs — тот же вход, что у
    /// `_PhysicsProcess`). В конце — снимок в поля теста.</summary>
    protected void EvaSteps(int n)
    {
        for (int i = 0; i < n; i++)
            _player.EvaTestStep();
        Sync();
    }

    /// <summary>N шагов EVA с фиксированной длительностью (чистые проверки
    /// расхода без разгона физикой).</summary>
    protected void EvaStepsTimed(int n, float dt)
    {
        for (int i = 0; i < n; i++)
            _player.EvaTestStep(dt);
        Sync();
    }

    protected void SuitSeatSteps(int n)
    {
        for (int i = 0; i < n; i++)
            _player.EvaTestSuitSeatStep();
        Sync();
    }

    /// <summary>Следующий физический шаг игрока получит эти биты (маска ввода,
    /// следующий шаг соберёт ввод сам, как в бою).</summary>
    protected void Feed(params uint[] bits)
    {
        uint mask = 0;
        foreach (uint b in bits)
            mask |= b;
        _player.EvaTestSetInput(InputFrame.FromMask(0, mask));
    }

    /// <summary>Кадр ввода с поворотом мыши (в радианах; тот же канал, что у
    /// корабля — `LookYaw`/`LookPitch`).</summary>
    protected void FeedLook(float yaw, float pitch, params uint[] bits)
    {
        uint mask = 0;
        foreach (uint b in bits)
            mask |= b;
        _player.EvaTestSetInput(InputFrame.FromMask(0, mask, yaw, pitch));
    }

    /// <summary>Снимок скафандра/тела в поля теста (после любого шага).</summary>
    protected void Sync()
    {
        Suit = _player.Suit;
        Mode = _player.Mode;
        IsAttached = _player.IsAttached;
        BodyPos = _player.GlobalPosition;
        BodyUp = _player.GlobalTransform.Basis.Y;
        BodyVel = _player.Velocity;
        Frame = _player.EvaTestLastFrame();
        LastMiss = _player.EvaTestLastMiss();
        LightOn = Suit.LightOn;
        BootsOn = Suit.BootsOn;
        Ticks = _player.EvaTestTick();
    }

    protected SuitState Suit;
    protected EvaMode Mode;
    protected bool IsAttached;
    protected Vector3 BodyPos, BodyUp, BodyVel;
    protected InputFrame Frame;
    protected string LastMiss = "";
    protected bool LightOn, BootsOn;
    protected uint Ticks;

    protected static bool IsFinite(Vector3 v)
        => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // ─────────────────────────────────────────────────────────────────────
    // Кресло
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Кресло стендового корабля (для прямого `AttachTo`) + диагностика,
    /// если его нет: сбой должен называть причину, а не падать молча (урок
    /// BUG-016).</summary>
    protected AttachPoint? Seat(out string why)
    {
        var found = SeatOn(_ship);
        why = found == null
            ? $"attach_point в дереве TestShip: {DumpPoints(_ship)}"
            : $"NetId={found.NetId} Owner={found.OwnerNetId} Kind={found.Kind} pos={found.GlobalPosition}";
        return found;
    }

    protected AttachPoint? SeatOn(Node root)
    {
        foreach (Node n in root.GetChildren())
        {
            if (n is AttachPoint ap && ap.Kind == AttachPoint.KindOf.Seat)
                return ap;
            var deep = SeatOn(n);
            if (deep != null)
                return deep;
        }
        return null;
    }

    protected static string DumpPoints(Node root)
    {
        var sb = new StringBuilder();
        foreach (Node n in root.GetChildren())
        {
            if (n is AttachPoint ap)
                sb.Append($"[{n.Name} Kind={ap.Kind} NetId={ap.NetId} Owner={ap.OwnerNetId}] ");
            else
                sb.Append(DumpPoints(n));
        }
        return sb.Length == 0 ? "нет" : sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    // Протокол (тот же формат, что Stage1/5/6Test — grep по «RESULT»)
    // ─────────────────────────────────────────────────────────────────────

    protected int _checks, _failures;

    protected void Check(string label, bool ok)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
            // Диагностика провала: снимок состояния в момент проверки — иначе
            // «FAIL» в длинной секвенции нельзя отладить глазами.
            GD.Print($"    [DBG] mode={Mode} att={IsAttached} boots={BootsOn} light={LightOn}"
                     + $" pos={BodyPos} vel={BodyVel} up={BodyUp}"
                     + $" bat={Suit.Battery:F1} fuel={Suit.Fuel:F1} heat={Suit.HeatOn}"
                     + $" floorN={_player.EvaTestFloorNormal()}"
                     + $" deckT={_deck.GlobalTransform.Origin}"
                     + $" miss=\"{LastMiss}\"");
        }
        GD.Print($"{(ok ? "PASS" : "FAIL")} {label}");
    }

    protected void Finish()
    {
        GD.Print($"RESULT checks={_checks} failures={_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
