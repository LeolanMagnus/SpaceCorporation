using Godot;

namespace SpaceCorporation;

/// <summary>
/// F2.3 (план «обитаемые палубы», фаза 2): жилая каюта для типов каталога,
/// у которых задан `HabitableVolume` (Hauler, ContainerFreighter). Курьер
/// несёт готовую палубу сценой (Ship.tscn: Decks/*); для остальных каюту
/// строит код: пол, крыша и стенки со срезом под типовой шлюз (Airlock.tscn),
/// врезанный в кормовую стенку так, что наружная дверь смотрит за корму
/// (компоновка — как у курьера: центр бокса в плоскости кормовой стенки,
/// внутренняя дверь в каюту, наружная — за кормовой срез корпуса).
/// Все тела — слой `Deck` (256): в них упирается капсула в ботинках,
/// джетпаком сквозь них не пройти (тот же физический гейт, что у курьера).
///
/// Смена типа — дебаг-кнопка (RequestShipType), перестрой редок:
///  • код-узлы помечены meta `GeneratedDeck` и удаляются `Free()` при
///    обратном переключении (сразу, не QueueFree — иначе старые одноимённые
///    узлы до конца кадра в дереве, и Godot переименовал бы новые в `@2`);
///  • сцена-палуба курьера (DeckHull/DeckInterior/CabinWalls) на время
///    чужой каюты ВЫРЕЗАЕТСЯ из дерева (узлы перекладываются в хранилище-
///    фильтр `DeckStorage`, не освобождаются): их габарит — курьерский
///    корпус, внутри Hauler это лишний физический объект в трюме, и лучи
///    стендов встречали бы её вместо код-пола. `Player.Clip.BootsCarrierShip`
///    (поиск корабля вверх по дереву) опирается на тела палубного слоя —
///    их в чужом корпусе и так строит генератор, поэтому срезка безопасна;
///    при возврате на курьера узлы возвращаются в `Decks` на свои места;
///  • шлюз один на все типы: он остаётся в `Decks`, его сцена-позиция
///    запоминается в meta и возвращается при переключении обратно.
/// Имена код-узлов начинаются с `Gen` — коллизий имён со сценой нет.
/// </summary>
public partial class Ship
{
    /// <summary>Проём в кормовой стенке под шлюз: полуширина 0.85 м (бокс
    /// Airlock ±0.6 + зазор; в остаточную щель 0.25 капсула r=0.3 не
    /// пролезает), верх 2.2 м (потолок бокса шлюза).</summary>
    private const float HatchCutHalfWidth = 0.85f;
    private const float HatchCutTop = 2.2f;

    /// <summary>Зазор кормовой стенки каюты от кормового среза корпуса, м:
    /// стенка внутри корпуса, шлюз из неё выглядывает за корму.</summary>
    private const float CabinAftInset = 0.15f;

    /// <summary>Палуба, принесённая сценой Ship.tscn (габарит курьера).</summary>
    private static readonly string[] SceneDeckNodeNames =
        { "DeckHull", "DeckInterior", "CabinWalls" };

    /// <summary>Есть ли у этой копии код-каюта (построена; ждёт удаления при
    /// обратном переключении на тип без HabitableVolume).</summary>
    private bool _generatedDecks;

    /// <summary>Построить (пересобрать) жилую каюту по спецификации типа.
    /// Вызывается из `ApplyShipTypeSpec` после применения габаритов корпуса.
    /// У Courier габарита в каталоге нет — палубу несёт сцена.</summary>
    private void BuildGeneratedDecks()
    {
        var decks = GetNodeOrNull<Node3D>("Decks");
        if (decks == null)
            return; // урезанные тестовые сцены без Decks — строить негде

        var spec = _shipTypeSpec;

        // Шлюз в сцене один на все типы: позиция зависит от типа, исходную
        // (курьерскую) запоминаем при первом переносе и возвращаем обратно.
        var hatch = decks.GetNodeOrNull<Airlock>("Airlock");
        if (hatch != null && !decks.HasMeta("HatchScenePos"))
            decks.SetMeta("HatchScenePos", hatch.Position);

        if (spec.HabitableVolume == Vector3.Zero)
        {
            // Тип без своей каюты (курьер): снимаем код-надстройки предыдущего
            // типа, возвращаем сцена-палубу из хранилища, ставим шлюз на
            // сцена-место.
            if (_generatedDecks)
            {
                foreach (Node c in decks.GetChildren())
                    if (c.HasMeta("GeneratedDeck"))
                        c.Free();
                _generatedDecks = false;
                SetSceneDeckCut(decks, false);
                if (hatch != null && decks.HasMeta("HatchScenePos"))
                    hatch.Position = (Vector3)decks.GetMeta("HatchScenePos");
            }
            return;
        }

        if (_generatedDecks)
        {
            // Освобождаем сразу (Free, как RebuildHullAccessories): QueueFree
            // оставил бы старые одноимённые узлы в дереве до конца кадра.
            foreach (Node c in decks.GetChildren())
                if (c.HasMeta("GeneratedDeck"))
                    c.Free();
        }
        SetSceneDeckCut(decks, true);

        float hx = spec.HullSize.X * 0.5f, hy = spec.HullSize.Y * 0.5f,
              hz = spec.HullSize.Z * 0.5f;
        // Каюта прижата носовым торцем к носовому срезу корпуса (нос = −Z),
        // кормовой торец — внутри у кормы, шлюз в нём и торчит за корму.
        float cabinAft = hz - CabinAftInset;
        float cabinFore = Mathf.Max(-hz + 0.05f, cabinAft - spec.HabitableVolume.Z);
        float len = cabinAft - cabinFore;
        float halfW = Mathf.Min(spec.HabitableVolume.X * 0.5f, hx - 0.05f);
        float h = spec.HabitableVolume.Y;
        // Пол каюты — плита 0.1 м над внутренним днищем корпуса (полукорпуса
        // hy: днище корпуса в −hy, его «пол» изнутри ~ −hy + 0.1); верх плиты
        // = верх пола шлюза (ниже центр бокса шлюза ставим на 0.05).
        float floorY = -hy + 0.15f, ceilY = floorY + h;
        float midZ = (cabinFore + cabinAft) * 0.5f;
        const float t = 0.1f; // толщина плит/стен — как у курьера

        AddDeckSlab(decks, "GenCabinFloor", new Vector3(halfW * 2f, t, len),
                    new Vector3(0f, floorY, midZ));
        // Крыша каюты — сплошная плита; проём шлюза перекрывается потолком
        // бокса Airlock, из каюты наружу — только через двери.
        AddDeckSlab(decks, "GenCabinCeiling", new Vector3(halfW * 2f, t, len),
                    new Vector3(0f, ceilY, midZ));

        var walls = new Node3D { Name = "GenCabinWalls" };
        walls.SetMeta("GeneratedDeck", true);
        decks.AddChild(walls);
        AddDeckSlab(walls, "WallFore", new Vector3(halfW * 2f, h, t),
                    new Vector3(0f, floorY + h * 0.5f, cabinFore));
        AddDeckSlab(walls, "WallPort", new Vector3(t, h, len),
                    new Vector3(-halfW, floorY + h * 0.5f, midZ));
        AddDeckSlab(walls, "WallStarboard", new Vector3(t, h, len),
                    new Vector3(halfW, floorY + h * 0.5f, midZ));
        // Кормовая стенка со срезом под шлюз: два куска по бокам (+ перемычка
        // над проёмом, если потолок каюты выше потолка шлюза).
        float cut = Mathf.Min(HatchCutHalfWidth, halfW - 0.05f);
        float sideW = halfW - cut;
        if (sideW > 0.01f)
        {
            AddDeckSlab(walls, "WallAftPort", new Vector3(sideW, h, t),
                        new Vector3(-(cut + sideW * 0.5f), floorY + h * 0.5f, cabinAft));
            AddDeckSlab(walls, "WallAftStarboard", new Vector3(sideW, h, t),
                        new Vector3(cut + sideW * 0.5f, floorY + h * 0.5f, cabinAft));
        }
        if (ceilY > floorY + HatchCutTop + 0.01f)
            AddDeckSlab(walls, "WallAftLintel",
                        new Vector3(cut * 2f, ceilY - (floorY + HatchCutTop), t),
                        new Vector3(0f, (ceilY + floorY + HatchCutTop) * 0.5f, cabinAft));

        // Шлюз: центр бокса в кормовой стенке так, чтобы внутренняя дверь
        // (local z=−0.55) встала в плоскость стенки, а наружная (+0.55) —
        // за корму. Плита пола шлюза (верх local y=0) — в один уровень с
        // верхом пола каюты: центр бокса шлюза = верху пола каюты (как у
        // курьера: hatch y=−0.4 = верх палубы −0.4). Двери бокса
        // (низ local −0.1, верх +2.1) целиком в проёме стенки (0..2.2).
        if (hatch != null)
            hatch.Position = new Vector3(0f, floorY + t * 0.5f, cabinAft + 0.55f);

        _generatedDecks = true;
        // DecksCount > 1 (трап на верхнюю палубу) не строится: двухпалубных
        // типов в каталоге нет, генератор трапа появится с первым таким типом.
    }

    /// <summary>Вырезать/вернуть сцена-палубу курьера (DeckHull, DeckInterior,
    /// CabinWalls) из дерева корабля на время чужой код-каюты. Просто
    /// `Frozen` не годится: замороженные тела Godot продолжает попадать в
    /// raycast/стендовые лучи, и курьерская плита (верх y=−0.4 в координатах
    /// корпуса) повисла бы посреди кабины Hauler физическим объектом.
    /// Срезанные узлы лежат в осиротевшем Node3D вне дерева: вне PhysWorld,
    /// вне обхода потомков Clip — и не освобождаются, поэтому возврат на
    /// курьера возвращает те же узлы с их сцена-трансформами.</summary>
    private Node3D? _deckStorage;

    private void SetSceneDeckCut(Node3D decks, bool cutOut)
    {
        if (cutOut)
        {
            _deckStorage ??= new Node3D { Name = "DeckStorage" };
            foreach (string n in SceneDeckNodeNames)
            {
                var body = decks.GetNodeOrNull<Node3D>(n);
                if (body != null && !ReferenceEquals(body.GetParent(), _deckStorage))
                {
                    decks.RemoveChild(body);
                    _deckStorage.AddChild(body); // локальные трансформы целы
                }
            }
        }
        else if (_deckStorage != null)
        {
            foreach (Node c in _deckStorage.GetChildren())
            {
                _deckStorage.RemoveChild(c);
                decks.AddChild(c);
            }
        }
    }

    private static void AddDeckSlab(Node parent, string name, Vector3 size, Vector3 pos)
    {
        var body = new StaticBody3D
        {
            Name = name,
            CollisionLayer = CollisionLayers.Deck,
            CollisionMask = 0u,
            Position = pos,
        };
        body.SetMeta("GeneratedDeck", true);
        body.AddChild(new CollisionShape3D
        {
            Name = "CollisionShape3D",
            Shape = new BoxShape3D { Size = size },
        });
        body.AddChild(new MeshInstance3D
        {
            Name = "Mesh",
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.45f, 0.47f, 0.5f),
            },
        });
        parent.AddChild(body);
    }
}
