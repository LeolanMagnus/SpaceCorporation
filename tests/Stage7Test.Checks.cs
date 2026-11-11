using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Тела проверок Stage7Test (EVA, eva-plan Блок C / §G п.1–11). Драйвер фаз —
/// в Stage7Test.cs, стенд мира — в Stage7Test.Helpers.cs. Каждый метод — ОДНА
/// фаза: делает шаги, снимает состояние хелперами (Sync уже вызван внутри
/// EvaSteps/…) и объявляет Check(...). Никаких задержек и корутин: драйвер сам
/// переходит по фазам и сторожит бюджет кадров.
///
/// ВАЖНО (прочитано из Player.Eva.cs/SuitModel.cs, а не наугад):
///  • F свободным = луч под КУРСОРОМ (`SeatPointUnderCursor`, ray length 30 м);
///    в headless курсора нет — поэтому фаза Seat4 проверяет саму ветку отказа,
///    а вход в кресло — прямым `AttachTo` (кейс Stage1Test 16);
///  • мгновенная заправка/зарядка — только при владельце точки-КОРАБЛЕ
///    (`OwnerIsShip`, ФЛАГ В25), станция/палуба не заправляют;
///  • сухая EVA-ветка (`NetworkManager` не инициализирован) ведёт себя как
///    offline-сервер: и заправка, и `RequestDetach` работают;
///  • `EvaTestSetInput` сгорает на первом шаге (StepEva обнуляет `_testInput`),
///    поэтому sustained-режим = кормить кадром ПЕРЕД каждым шагом;
///  • `Suit.HeatOn` после первого же шага в кресле = false: `StepSuitInSeat`
///    первым делом берёт `StepRecharge` (+20 %/мин батарею) и включает обогрев
///    обратно, а `StepDrain` снимает лишь 4 %/мин — сеть не «ест» заряд;
///  • `EvaTestStickTo`/`EvaTestSitAt`/`EvaTestSetSuit`/`EvaTestSetBoots` —
///    публичные швы Player.Eva.cs для стендовых входов, недоступных в headless
///    (луч под курсором, физический контакт ботинок).
/// </summary>
public partial class Stage7Test : Node
{
    // ==================================================================
    // P0 — клавиши (C4): detach/toggle_boots/suit_light + литеры
    // ==================================================================
    private void CheckKeysInputMap()
    {
        // Godot.InputMap — статический класс (в .NET-привязке Godot 4).
        Check("C4: действие detach существует", InputMap.HasAction("detach"));
        Check("C4: действие toggle_boots существует", InputMap.HasAction("toggle_boots"));
        Check("C4: действие suit_light существует", InputMap.HasAction("suit_light"));

        // П.7/план §8: F=отстёжка/кресло (одна клавиша на два действия, В21),
        // G=ботинки, N=фонарь. Литеры проверяем по событиям действия (в
        // project.godot привязки именно литерные; экзотические модификаторы
        // здесь не используются).
        Check("клавиша F => detach", ActionHasKey("detach", Key.F));
        Check("клавиша G => toggle_boots", ActionHasKey("toggle_boots", Key.G));
        Check("клавиша N => suit_light", ActionHasKey("suit_light", Key.N));

        // В23: у действия один владелец. `toggle_seat` НЕ должно существовать:
        // F уже занята `detach`, вторая акция на одном клавишном событии
        // стреляла бы дважды за кадр.
        Check("C4: отдельного toggle_seat нет (В23)", !InputMap.HasAction("toggle_seat"));
    }

    private static bool ActionHasKey(string action, Key key)
    {
        if (!InputMap.HasAction(action))
            return false;
        foreach (Resource res in InputMap.ActionGetEvents(action))
        {
            // Godot 4: список Variant'ов; в .NET-привязке элементы приходят как
            // Resource. Привязки в project.godot литерные через
            // physical_keycode (keycode=0), поэтому сверяем оба поля.
            if (res is InputEventKey k && !k.Echo
                && (k.Keycode == key || k.PhysicalKeycode == key))
                return true;
        }
        return false;
    }

    // ==================================================================
    // P1 — п.1/п.8/п.18: инварианты EVA-пути (чистые функции SuitModel —
    // в SuitModelTest; здесь — что EVA-код их действительно зовёт)
    // ==================================================================
    private void Suit0_Begin()
    {
        Sync();
        bool fresh = Suit.Fuel > 99.9f && Suit.Battery > 99.9f
                     && Suit.HeatOn && !Suit.LightOn && !Suit.BootsOn;
        Check("п.1: скафандр новый = 100/100, обогрев вкл, свет/ботинки выкл", fresh);
    }

    private void Suit1_AttachRefuel()
    {
        // Б2: посадка в кресло КОРАБЛЯ мгновенно заправляет реакционную массу
        // (SuitModel.ApplyShipAttach) и запускает зарядку батареи. Топливо
        // сажаем заранее — иначе проверять нечего.
        var s = SuitState.Fresh();
        s.Fuel = 30f;
        s.Battery = 30f;
        _player.EvaTestSetSuit(s);
        Sync(); // снимок скафандра должен соответствовать заданному (иначе
                // _fuel0/_battery0 — устаревшие 100, и дельты не проверить)
        _fuel0 = Suit.Fuel;
        _battery0 = Suit.Battery;

        var seat = Seat(out string why);
        Check($"кресло стендового корабля найдено ({why})", seat != null);
        if (seat == null)
            return;

        _player.AttachTo(seat);
        Sync();
        Check("п.8: посадка в кресло корабля = мгновенная заправка массы",
              IsAttached && Mode == EvaMode.Seat && Suit.Fuel > 99.9f && _fuel0 < 40f);

        SuitSeatSteps(120); // 2 с: +20 %/мин ⇒ +0.667 %
        Check("п.8: батарея заряжается в кресле корабля", Suit.Battery > _battery0 + 0.5f);
    }

    private void Suit2_WarnHysteresis()
    {
        // ФЛАГ: включение предупреждения при 10 %, выключение только после 12 %
        // (в полосе 10–12 предупреждение держится, иначе HUD мигал бы у
        // границы — те же ожидания, что в SuitModelTest п.11).
        var s = SuitState.Fresh();
        s.Fuel = 10f;
        SuitModel.UpdateWarnings(ref s);
        bool onAt10 = s.LowFuelWarn;
        s.Fuel = 11f;
        SuitModel.UpdateWarnings(ref s);
        bool holdAt11 = s.LowFuelWarn;
        s.Fuel = 12f;
        SuitModel.UpdateWarnings(ref s);
        bool holdAt12 = s.LowFuelWarn;
        s.Fuel = 16f;
        SuitModel.UpdateWarnings(ref s);
        bool offAt16 = !s.LowFuelWarn;
        Check("п.18: предупреждение топлива: вкл@10, держится@11–12, выкл@16",
              onAt10 && holdAt11 && holdAt12 && offAt16);
    }

    private void Suit3_BatteryZero()
    {
        // ФЛАГ В22-оговорка: севшая батарея глушит свет и обогрев, реактивная
        // масса — отдельный счётчик и остаётся.
        var s = SuitState.Fresh();
        s.Battery = 0f;
        s.LightOn = true;
        SuitModel.StepDrain(ref s, 0f, 0f, SuitModel.SuitMaxAngularSpeed, 1f);
        bool extinguished = !s.LightOn && !s.HeatOn && s.Battery == 0f
                            && s.Fuel > 99.9f;
        Check("ФЛАГ В22: батарея 0 ⇒ свет/обогрев погашены, масса цела", extinguished);

        // Фонарь при пустой батарее не включается (Player.ToggleSuitLight).
        _player.EvaTestSetSuit(new SuitState
        { Fuel = 100f, Battery = 0f, HeatOn = false, LightOn = false });
        _player.EvaTestKeyPress("suit_light");
        Sync();
        Check("фонарь при севшей батарее не включается", !LightOn);
    }

    private void Suit4_DeckNoRefuel()
    {
        // ФЛАГ В25 (п.8): заправляет/заряжает ТОЛЬКО кресло корабля. Палуба
        // стенда — StaticBody3D без сетевого владельца (AttachPoint.OwnerNetId
        // = -1): ни мгновенной заправки при входе, ни зарядки батареи в шагах.
        var s = SuitState.Fresh();
        s.Fuel = 40f;
        s.Battery = 40f;
        _player.EvaTestSetSuit(s);

        var deckPoint = new AttachPoint
        {
            Name = "DeckPoint",
            Kind = AttachPoint.KindOf.Floor,
            NetId = 7,
            // OwnerNetId=0: владелец (палуба) без NetState — разрешение в _Ready
            // само даст 0, точка в реестр сети не попадёт как корабельная.
        };
        _deck.AddChild(deckPoint);
        _player.AttachTo(deckPoint, snapToAttachPoint: false);
        Sync();
        Check("вход на палубу: режим кресла", IsAttached && Mode == EvaMode.Seat);
        float fuelAtSeat = Suit.Fuel, batteryAtSeat = Suit.Battery;

        SuitSeatSteps(600); // 10 с: зарядка дала бы +3.33 %, обогрев съел бы 0.67 %
        Sync();
        Check("п.8/В25: палуба не заправляет массу", Suit.Fuel == fuelAtSeat);
        Check("п.8/В25: палуба не заряжает батарею", Suit.Battery < batteryAtSeat);

        // Дальше идут frame/seat-фазы, они стартуют из джетпака — освобождаем
        // игрока (снимать AttachPoint с палубы нельзя при живом креплении).
        _player.Detach();
        Sync();
    }

    // ==================================================================
    // P2 — п.3: EVA и корабль = ОДИН формат кадра
    // ==================================================================
    private void Frame0_Begin()
    {
        _p0 = BodyPos;
        Check("старт frame-фазы: игрок в джетпаке", Mode == EvaMode.Jetpack);
    }

    private void Frame1_SameBit()
    {
        // Кормим джетпак кадром с той же битой, что использует корабль
        // (InputMask.ThrustF — «W»). Формат единый, поэтому та же бита даёт
        // ускорение вдоль носа и в EVA (п.3 — «своих» бит у EVA нет).
        // ВАЖНО: тестовый кадр — ОДНОРАЗОВЫЙ (`_testInput` съедается следующим
        // шагом), поэтому кормим КАЖДЫЙ шаг цикла, как в бою держат клавишу.
        for (int i = 0; i < 30; i++)
        {
            Feed(InputMask.ThrustF);
            _player.EvaTestStep();
        }
        Vector3 nose = -BodyUp.Cross(BodyPos - _p0 + Vector3.Right * 1e-6f);
        nose = (GlobalNose()).Normalized();
        bool moved = (BodyPos - _p0).Length() > 0.01f;
        bool alongNose = (BodyPos - _p0).Normalized().Dot(nose) > 0.9f;
        bool sameBit = (Frame.Mask & InputMask.ThrustF) != 0u;
        GD.Print($"[PROBE Frame1] moved={moved} along={alongNose} bit={sameBit}"
                 + $" disp={BodyPos - _p0} nose={nose} mask={_player.EvaTestLastFrame().Mask}");
        Check("п.3: бита ThrustF двигает EVA вперёд (тот же формат)",
              moved && alongNose && sameBit);
    }

    private void Frame2_SameDeltaV()
    {
        // П.3: на одинаковый кадр — одинаковый ΔV при равных параметрах.
        // Инвариант детерминизма: та же стартовая скорость (ноль) + те же
        // 30 кадров тяги ⇒ тот же итоговый |V|. Модель кадра и шаг — одни и
        // те же функции, что у корабля (`FlightModel.Step`), расходятся только
        // параметры скафандра. Скорость обнуляем ЯВНО: гасить инерцию шагами
        // покоя долго (демпфер 0.1/с), а старт «с места» — часть условия
        // «равные параметры».
        uint bits = InputMask.ThrustF;

        _player.Velocity = Vector3.Zero;
        _player.EvaTestSyncFlightFromBody();
        for (int i = 0; i < 30; i++)
        {
            Feed(bits);
            _player.EvaTestStep();
        }
        Sync();
        float dv = BodyVel.Length();

        _player.Velocity = Vector3.Zero;
        _player.EvaTestSyncFlightFromBody();
        for (int i = 0; i < 30; i++)
        {
            Feed(bits);
            _player.EvaTestStep();
        }
        Sync();
        float dv2 = BodyVel.Length();
        bool sameDelta = Mathf.Abs(dv - dv2) < 1e-3f && dv > 0.1f;
        Check("п.3: одинаковый кадр ⇒ одинаковый ΔV (детерминизм)", sameDelta);
    }

    private void Frame3_End()
    {
        Check("кадры конечны (NaN-страховка)", IsFinite(BodyPos) && IsFinite(BodyVel));
    }

    /// <summary>Мировой нос тела (−Z базиса) — тот же признак, что у корабля.</summary>
    private Vector3 GlobalNose()
    {
        Sync();
        return -_player.GlobalTransform.Basis.Z;
    }

    // ==================================================================
    // P3 — C3 кресло (п.7/п.10)
    // ==================================================================
    private void Seat0_Begin()
    {
        var seat = Seat(out string why);
        Check($"кресло найдено ({why})", seat != null);
        _chair = seat ?? _chair;
        Sync();
        Check("старт seat-фазы: игрок свободен (джетпак)", !IsAttached && Mode == EvaMode.Jetpack);
    }

    private void Seat1_EnterExit()
    {
        if (_chair == null || !GodotObject.IsInstanceValid(_chair))
        {
            Check("Seat1: кресло пропало — вход/выход не проверить", false);
            return;
        }
        // Вход — прямым AttachTo (F-путь свободным — луч под курсором, в
        // headless недоступен; сетевой маршрут проверяется Stage1/2Test).
        _player.AttachTo(_chair);
        Sync();
        bool inSeat = IsAttached && Mode == EvaMode.Seat && IsFinite(BodyPos);
        Check("п.7: вход в кресло ⇒ режим Seat", inSeat);

        _savedPos = BodyPos;
        _wasChild = _player.GetParent() == _chair;

        // Выход — шов F (пристёгнут ⇒ RequestDetach; в offline-ветке он
        // отрабатывает мгновенно).
        _player.EvaTestDetachOrSeat();
        Sync();
        bool outSeat = !IsAttached && Mode == EvaMode.Jetpack
                       && _player.GetParent() == _world;
        Check("п.7: F из кресла ⇒ отстёжка, свободный джетпак, возврат в мир", outSeat);
        Check("выход сохраняет мировую позицию", BodyPos.DistanceTo(_savedPos) < 0.6f);
        Check("в кресле игрок был ребёнком точки (Reparent, ФЛАГ В6)", _wasChild);
    }

    private void Seat2_MovingShip()
    {
        // П.10: игрок в кресле едет ВМЕСТЕ с кораблём без всякого EVA-шага —
        // Reparent-иерархия делает это сама. Кораблю стенда процесс выключен,
        // поэтому «езда» = прямая запись глобального трансформа ship.
        if (_chair == null || !GodotObject.IsInstanceValid(_chair))
        {
            Check("Seat2: кресло пропало", false);
            return;
        }
        _player.AttachTo(_chair);
        Sync();
        _p0 = BodyPos;
        if (_ship is Node3D ship3)
            ship3.GlobalPosition += new Vector3(5f, 0f, 0f);
        ForceSyncFrame();
        Sync();
        Vector3 shipMove = new Vector3(5f, 0f, 0f);
        bool rode = (BodyPos - _p0).DistanceTo(shipMove) < 0.5f;
        Check("п.10: игрок в кресле уехал вместе с кораблём", rode);
    }

    private void Seat3_MovingShip2()
    {
        // Снятие замера: второй сдвиг корабля + проверка, что никто не
        // «разгоняет» игрока: скорость тела в кресле остаётся нулевой.
        if (_ship is Node3D ship3)
            ship3.GlobalPosition += new Vector3(0f, 0f, 3f);
        ForceSyncFrame();
        Sync();
        bool rode2 = BodyPos.Z > 2.5f && BodyPos.DistanceTo(_p0) > 5f;
        bool calm = BodyVel.Length() < 0.5f && IsFinite(BodyPos);
        Check("п.10: второй сдвиг — тоже едем, скорость тела не растёт", rode2 && calm);
    }

    private void Seat4_Occupied()
    {
        // П.6/п.7: F свободным целится по креслу ПОД КУРСОРОМ; в headless
        // курсора нет — проверяем контракт ветки отказа: нет точки под лучом
        // ⇒ крепление не происходит, а причина объяснена текстом (UX C4).
        if (IsAttached)
            _player.Detach();
        Sync();
        var under = _player.EvaTestSeatPointUnderCursor();
        _player.EvaTestDetachOrSeat();
        Sync();
        bool refused = !IsAttached && Mode == EvaMode.Jetpack && under == null;
        bool explained = LastMiss.Contains("кресла") || LastMiss.Contains("курсор");
        Check("п.6: F без кресла под курсором ⇒ отказа нет крепления", refused);
        Check("C4: отказ объяснён текстом подсказки", explained);
    }

    private void Seat5_End()
    {
        Check("seat-фаза завершена корректно", IsFinite(BodyPos) && Mode == EvaMode.Jetpack);
    }

    // ==================================================================
    // P4 — C1 джетпак (п.2/п.11)
    // ==================================================================
    private void Jet0_Begin()
    {
        _player.EvaTestSetSuit(SuitState.Fresh());
        _player.Velocity = Vector3.Zero;
        Sync();
        Check("старт jet-фазы: джетпак, полный бак", Mode == EvaMode.Jetpack && Suit.Fuel > 99.9f);
    }

    private void Jet1_SpeedCap()
    {
        // П.2: потолок скорости скафандра 10 м/с при тяге 8 м/с². Тягу держим
        // EVERY шаг (подменённый кадр сгорает на первом шаге).
        uint bits = InputMask.ThrustF;
        float maxSeen = 0f;
        for (int i = 0; i < 180; i++) // 3 с
        {
            Feed(bits);
            _player.EvaTestStep();
            maxSeen = Mathf.Max(maxSeen, _player.Velocity.Length());
        }
        Sync();
        Check("п.2: после 3 с тяги скорость не превышает 10 м/с",
              BodyVel.Length() <= 10.01f && maxSeen <= 10.01f && BodyVel.Length() > 9f);
    }

    private void Jet2_SpeedCap2()
    {
        // Снятие замера: на покое демпфер 0.1 гасит скорость, за счётчик —
        // 300 шагов; «потолок не сломал физику» = траектория конечна.
        for (int i = 0; i < 300; i++)
        {
            Feed();
            _player.EvaTestStep();
        }
        Sync();
        Check("п.2: на покое скорость гаснет, координаты конечны",
              BodyVel.Length() < 0.5f && IsFinite(BodyPos));
    }

    private void Jet3_SixDoF()
    {
        // П.11/В16: 6DoF-маска EVA = те же биты, что у корабля: линейные
        // (WASD+Space/Ctrl) + угловые (Q/E/Z/X/C/V). Проверяем, что EVA их
        // читает: «A» (ThrustL) даёт смещение вдоль мира X, «V»
        // (TorqueYawRight — тот же бит момента, что у корабля) — поворот
        // носа; обе биты доходят в маске последнего кадра.
        _player.Velocity = Vector3.Zero;
        _p0 = _player.GlobalPosition;
        Vector3 noseBefore = GlobalNose();
        for (int i = 0; i < 30; i++)
        {
            Feed(InputMask.ThrustL, InputMask.TorqueYawRight);
            _player.EvaTestStep();
        }
        Sync();
        bool linBit = (Frame.Mask & (InputMask.ThrustL | InputMask.TorqueYawRight))
                      == (InputMask.ThrustL | InputMask.TorqueYawRight);
        bool turned = GlobalNose().AngleTo(noseBefore) > 0.01f;
        bool moved = BodyPos.DistanceTo(_p0) > 0.01f;
        Check("п.11: EVA читает биты того же формата (лин+ангул)", linBit && moved && turned);
    }

    private void Jet4_FuelDrain()
    {
        // С1: расход по ФАКТУ. Тяга 1 с ⇒ −1 %; покой 1 с ⇒ расход
        // реактивной массы нулевой (0.2 %/с — только при довороте).
        _player.EvaTestSetSuit(SuitState.Fresh());
        Sync();
        _fuel0 = Suit.Fuel;
        float battery0 = Suit.Battery;
        for (int i = 0; i < 60; i++)
        {
            Feed(InputMask.ThrustF);
            _player.EvaTestStep();
        }
        Sync();
        float burned = _fuel0 - Suit.Fuel;
        bool thrustBurn = Mathf.Abs(burned - 1.0f) < 0.1f; // 1 %/с · 1 с
        _fuel0 = Suit.Fuel;
        for (int i = 0; i < 60; i++)
        {
            Feed();
            _player.EvaTestStep();
        }
        Sync();
        bool idleBurn = Suit.Fuel == _fuel0;
        bool heatDrain = Suit.Battery < battery0; // обогрев точится всегда
        Check("С1: тяга 1 с = ровно −1 % массы", thrustBurn);
        Check("С1: в покое джетпак массу не тратит", idleBurn);
        Check("п.1: обогрев ест батарею и в EVA", heatDrain);
    }

    // ==================================================================
    // P5 — C2 ботинки (п.9/п.10)
    // ==================================================================
    private void Boot0_Begin()
    {
        // Шов EvaTestStickTo — стендовый вход в ботинки: физический контакт и
        // луч выбора поверхности в headless недоступны напрямую (курсор/рендер),
        // но вся дальнейшая логика шага — настоящая.
        _player.EvaTestSetMode(EvaMode.Jetpack);
        _player.EvaTestStickTo((StaticBody3D)_deck, new Vector3(0f, -0.05f, 0f), Vector3.Up);
        _player.EvaTestSetMode(EvaMode.Boots);
        Sync();
        GD.Print($"[PROBE boot0] pos={BodyPos} vel={BodyVel} deckT={_deck.GlobalTransform.Origin}"
                 + $" gap={_player.EvaTestGapForTest()}");
        Check("старт boots-фазы: режим Boots на палубе",
              Mode == EvaMode.Boots && BootsOn && _player.EvaTestBootsBody() == _deck);
    }

    private void Boot1_Stick()
    {
        Sync();
        bool upright = BodyUp.AngleTo(Vector3.Up) < 0.05f;
        bool bootsFlag = BootsOn;
        bool noHierarchy = _player.GetParent() == _world;
        Check("п.9: в ботинках Up тела = нормаль палубы", upright && bootsFlag);
        Check("п.17: ботинки НЕ дочерят игрока к палубе", noHierarchy);
    }

    private void Boot2_RideDeck()
    {
        _p0 = BodyPos;
        _deck.GlobalPosition += new Vector3(4f, 0f, 0f); // палуба уехала
        ForceSyncFrame();
        EvaSteps(1); // шаг ботинок: перенос вместе с телом-носителем
        Check("п.10: сдвиг палубы — игрок поехал следом",
              (BodyPos - _p0).DistanceTo(new Vector3(4f, 0f, 0f)) < 0.5f);
    }

    private void Boot3_RideDeck2()
    {
        // Снятие замера + поворот палубы: нормаль опоры обязана пересчитываться
        // из ТЕКУЩЕГО базиса носителя (локальная нормаль контакта).
        _deck.GlobalRotationDegrees = new Vector3(0f, 0f, 20f);
        ForceSyncFrame();
        for (int i = 0; i < 2; i++)
        {
            _player.EvaTestStep();
            Sync();
            GD.Print($"[PROBE boot3 step {i}] pos={BodyPos} vel={BodyVel} up={BodyUp}"
                     + $" fN={_player.EvaTestFloorNormal()} gap={_player.EvaTestGapForTest()}"
                     + $" deckT={_deck.GlobalTransform.Origin}");
        }
        Sync();
        Vector3 deckNormal = _deck.GlobalTransform.Basis.Y;
        bool normalFollows = _player.EvaTestFloorNormal().AngleTo(deckNormal) < 0.1f;
        bool stillStuck = Mode == EvaMode.Boots && BodyPos.X > 3.5f
                          && IsFinite(BodyPos);
        Check("п.9: наклон палубы ⇒ нормаль опоры следует за телом",
              normalFollows && stillStuck);
    }

    private void Boot4_Walk()
    {
        // Ходьба: W (ThrustF) в плоскости нормали палубы со скоростью 3 м/с.
        _deck.GlobalRotationDegrees = Vector3.Zero;
        ForceSyncFrame();
        Sync();
        _p0 = BodyPos;
        for (int i = 0; i < 60; i++)
        {
            Feed(InputMask.ThrustF);
            _player.EvaTestStep();
        }
        Sync();
        float walked = (BodyPos - _p0).Length();
        bool speedOk = BodyVel.Length() <= SuitModel.BootsWalkSpeed + 0.1f;
        Check("п.9: ходьба в ботинках идёт вперёд (~3 м/с)",
              walked > 2f && walked < 4f && speedOk);
    }

    private void Boot5_Walk2()
    {
        // Снятие замера: движение — в ПЛОСКОСТИ нормали (Y не улетает), липкость
        // держит у поверхности: режим не гаснет, координаты конечны.
        bool planar = Mathf.Abs(BodyPos.Y - _p0.Y) < 0.3f;
        bool stuck = Mode == EvaMode.Boots && IsFinite(BodyPos);
        // реактивный расход в ботинках = 0 (В13=B: скафандр в ботинках не
        // реактивный — ориентация кинематична)
        _fuel0 = Suit.Fuel;
        for (int i = 0; i < 30; i++)
        {
            FeedLook(0.1f, 0f, InputMask.ThrustF);
            _player.EvaTestStep();
        }
        Sync();
        Check("п.9: ходьба держится плоскости, липкость не гаснет", planar && stuck);
        Check("В13: доворот в ботинках не жрёт реакционную массу", Suit.Fuel == _fuel0);
    }

    private void Boot6_TiltedDeck()
    {
        // П.9: крен палубы 45° ⇒ Up тела следует за нормалью, тело не «прямо
        // над покатавшейся стеной».
        _deck.GlobalRotationDegrees = new Vector3(0f, 0f, 45f);
        ForceSyncFrame();
        EvaSteps(2);
        Sync();
        Vector3 deckNormal = _deck.GlobalTransform.Basis.Y;
        bool aligned = BodyUp.AngleTo(deckNormal) < 0.15f;
        bool normalOk = _player.EvaTestFloorNormal().AngleTo(deckNormal) < 0.15f;
        _deck.GlobalRotationDegrees = Vector3.Zero;
        ForceSyncFrame();
        Check("п.9: крен палубы 45° ⇒ тело выровнялось по нормали", aligned && normalOk);

        // Передача следующей секции (переходы клавиш): снимем ботинки в джетпак
        // и вернём палубу/игрока в домашние позиции — иначе Sw0 стартовал бы в
        // Boots, а Sw2 нашёл бы игрока в метрах от уехавшей на x=4 палубы.
        _player.EvaTestSetMode(EvaMode.Jetpack);
        _deck.GlobalPosition = new Vector3(0f, -1.05f, 0f);
        _player.GlobalPosition = new Vector3(0f, 1.05f + Player.CapsuleHeight * 0.5f, 0f);
        _player.Velocity = Vector3.Zero;
        _player.EvaTestSyncFlightFromBody();
        ForceSyncFrame();
    }

    // ==================================================================
    // P6 — п.13–15: переходы клавиш
    // ==================================================================
    private void Sw0_Begin()
    {
        Sync();
        Check("старт sw-фазы: джетпак", Mode == EvaMode.Jetpack && !IsAttached);
    }

    private void Sw1_SeatToJetpack()
    {
        // Кейс 16 + п.13: F из кресла = отстёжка ⇒ джетпак.
        var seat = Seat(out string why);
        if (seat == null)
        {
            Check($"Sw1: кресло не найдено ({why})", false);
            return;
        }
        _player.AttachTo(seat);
        Sync();
        bool sat = Mode == EvaMode.Seat;
        _player.EvaTestDetachOrSeat(); // F: пристёгнут ⇒ RequestDetach
        Sync();
        Check("п.13: F из кресла ⇒ джетпак (кейс 16)",
              sat && !IsAttached && Mode == EvaMode.Jetpack);
    }

    private void Sw2_JetpackToBoots()
    {
        // П.14: G над палубой ⇒ ботинки (путь ActivateBoots: реальный луч по
        // слою BootsMask — палуба стенда на нужном слое, курсор не нужен).
        // СТАРТУЕМ В ДЖЕТПАКЕ прямо над палубой: `StickTo-шов` сюда НЕ годится —
        // он сразу включает Boots, и последующее G лишь выключило бы их (тест
        // проверяет именно лучевую активацию с нуля). Ставим игрока на домашнюю
        // высоту палубы и даём реальное G.
        _deck.GlobalPosition = new Vector3(0f, -1.0f, 0f);
        _deck.GlobalRotationDegrees = Vector3.Zero;
        _player.EvaTestSetMode(EvaMode.Jetpack);
        // Центр капсулы (пивот) — в 0.1 м, палуба приподнята: сфера
        // `ActivateBoots` (r=BootsMaxStickDist, центр = pos+Up*0.2, низ на
        // pos-0.8 = -0.7) достает до верха палубы (top = -1.0+0.5 = -0.5),
        // точка попадания в 0.6 м от игрока (< 1.0) — луч честно находит опору.
        _player.GlobalPosition = new Vector3(0f, 0.1f, 0f);
        _player.Velocity = Vector3.Zero;
        _player.EvaTestSyncFlightFromBody();
        ForceSyncFrame();
        _player.EvaTestKeyPress("toggle_boots");
        Sync();
        Check("п.14: G над палубой ⇒ Boots", Mode == EvaMode.Boots && BootsOn);
    }

    private void Sw3_BootsToJetpack()
    {
        // В28: G повторно — всегда выход в джетпак, позиция сохраняется.
        _p0 = BodyPos;
        _player.EvaTestKeyPress("toggle_boots");
        Sync();
        Check("п.14: G повторно ⇒ джетпак без телепорта",
              Mode == EvaMode.Jetpack && !BootsOn && BodyPos.DistanceTo(_p0) < 0.6f);
    }

    private void Sw4_BootsNoSurface()
    {
        // П.15: G без поверхности ⇒ отказа прилипания, режим НЕ меняется,
        // подсказка есть. Сажаем игрока высоко над миром (луч 1 м ничего не
        // найдёт) и жмём G.
        _player.GlobalPosition = new Vector3(0f, 25f, 0f);
        _player.Velocity = Vector3.Zero;
        _player.EvaTestKeyPress("toggle_boots");
        Sync();
        bool refused = Mode == EvaMode.Jetpack && !BootsOn;
        bool hint = LastMiss.Contains("Ботинки");
        Check("п.15: G без поверхности ⇒ отказ с текстом, режим прежний", refused && hint);
        _player.GlobalPosition = new Vector3(0f, 1.05f + Player.CapsuleHeight * 0.5f, 0f);
    }

    private void Sw5_Light()
    {
        // П.11(E-часть): N — фонарь туда-обратно + расход батареи 12 %/мин
        // суммируется с обогревом.
        _player.EvaTestSetSuit(SuitState.Fresh());
        Sync();
        float battery0 = Suit.Battery;
        _player.EvaTestKeyPress("suit_light");
        Sync();
        bool on = LightOn;
        EvaStepsTimed(60, 1f / 60f); // 60 кадров по 1/60 = 1 с: 4+12 %/мин ⇒ −0.2667 %
        float drain = battery0 - Suit.Battery;
        bool drainOk = Mathf.Abs(drain - (4f + 12f) / 60f) < 0.02f;
        _player.EvaTestKeyPress("suit_light");
        Sync();
        Check("п.11: N включает/выключает фонарь", on && !LightOn);
        Check("п.11: расход света 12 %/мин + обогрев 4 %/мин", drainOk);
    }

    private void Sw6_BootsToSeat()
    {
        // П.14: из ботинок в кресло — разрешено (F: свободен ⇒ занять кресло;
        // headless-джок F = кресло под курсором, поэтому берём шов посадки в
        // КОНКРЕТНУЮ точку — ветка отказа «не то кресло / занято» там та же).
        _player.EvaTestSetMode(EvaMode.Jetpack);
        var seat = Seat(out string why);
        if (seat == null)
        {
            Check($"Sw6: кресло не найдено ({why})", false);
            return;
        }
        bool sat = _player.EvaTestSitAt(seat);
        Sync();
        Check("п.14: переход из EVA-режима в кресло разрешён",
              sat && IsAttached && Mode == EvaMode.Seat && !BootsOn);
    }
    // ==================================================================
    // P9 — п.9: три режима — три расхода (масса / батарея / зарядка)
    // ==================================================================
    private void Suit2m_JetpackDrain()
    {
        // П.9: джетпак жжёт РЕАКЦИОННУЮ МАССУ при тяге; батарея при этом
        // тает только на обогрев (реактивная масса — отдельный счётчик).
        if (IsAttached)
            _player.Detach();
        Sync();
        if (Mode != EvaMode.Jetpack)
            _player.EvaTestSetMode(EvaMode.Jetpack);

        var s = SuitState.Fresh();
        s.Fuel = 60f;
        s.Battery = 60f;
        _player.EvaTestSetSuit(s);
        Sync();
        _fuel0 = Suit.Fuel;
        _battery0 = Suit.Battery;

        Feed(InputMask.ThrustF);
        EvaStepsTimed(60, 1f / 6f); // 10 с тяги: батарея −0.667 % гарантир.
        Feed(0u);
        Sync();
        float dFuel = _fuel0 - Suit.Fuel;
        float dBat = _battery0 - Suit.Battery;
        Check("п.9: джетпак при тяге расходует реакционную массу",
              dFuel > 0.05f && Suit.Fuel < 60f);
        Check("п.9: джетпак: батарея тает только на обогрев (4 %/мин)",
              dBat > 0.3f && dBat < 1.2f);
        // Инвариант В13=B: тяга ботинки/кресло массу не жгут — только джетпак.
        Check("п.9: масса не ушла в ноль и не выросла",
              Suit.Fuel > 0f && Suit.Fuel <= 60f);
    }

    private void Suit2m_BootsDrain()
    {
        // П.9 (В13=B): в ботинках реактивная масса СТОИТ, батарея тает
        // (обогрев). Шаг StepBoots обязан крутить StepDrain — иначе Battery
        // в EVA-режиме «бесплатный» (реальный баг, исправлен в Block C).
        _player.EvaTestStickTo((StaticBody3D)_deck,
                               new Vector3(BodyPos.X, -0.05f, BodyPos.Z), Vector3.Up);
        Sync();
        if (Mode != EvaMode.Boots || !BootsOn)
        {
            Check($"п.14: stick-шов включил Boots (mode={Mode})", false);
            return;
        }
        var s = SuitState.Fresh();
        s.Fuel = 60f;
        s.Battery = 60f;
        _player.EvaTestSetSuit(s);
        Sync(); // снимок обязан соответствовать заданному: без Sync Suit —
                // устаревшее значение до SetSuit, дельты неверны (корень бага)
        _fuel0 = Suit.Fuel;
        _battery0 = Suit.Battery;

        Feed(0u);
        EvaSteps(120); // 2 с: обогрев 4 %/мин ⇒ −0.133 %
        Sync();
        float dFuel = _fuel0 - Suit.Fuel;
        float dBat = _battery0 - Suit.Battery;
        Check("п.9/В13B: ботинки реакционную массу НЕ жгут", dFuel < 1e-4f);
        Check("п.9: ботинки тратят батарею на обогрев (2 с ≈ 0.13 %)",
              dBat > 0.05f && dBat < 0.5f);
    }

    private void Suit2m_SeatRecharge()
    {
        // П.8/п.9: в кресле корабля батарея ЗАРЯЖАЕТСЯ (20 %/мин), масса
        // доливается при входе. Обогрев продолжает жрать — net-прирост.
        if (Mode == EvaMode.Boots)
        {
            _player.EvaTestKeyPress("toggle_boots");
            Sync();
        }
        var s = SuitState.Fresh();
        s.Fuel = 55f;
        s.Battery = 50f;
        _player.EvaTestSetSuit(s);

        Check("п.9: кресло стендового корабля доступно",
              _chair != null && GodotObject.IsInstanceValid(_chair));
        if (_chair == null || !GodotObject.IsInstanceValid(_chair))
            return;

        _player.AttachTo(_chair);
        Sync();
        _fuel0 = Suit.Fuel;
        _battery0 = Suit.Battery;

        SuitSeatSteps(300); // 5 с: +20 %/мин − обогрев ⇒ net ≈ +1.33 %
        Sync();
        Check("п.9: в кресле корабля батарея растёт",
              IsAttached && Mode == EvaMode.Seat && Suit.Battery > _battery0 + 0.5f);
        Check("п.8: кресло долило массу до полного бака при входе",
              _fuel0 > 99.9f);
    }

    private void Suit2m_End()
    {
        // Расходники скафандра настраиваем обратно на полный: иерархия
        // ниже к ресурсам нечувствительна, но полный комплект — страховка
        // от накопленных флагов предупреждений.
        _player.Detach();
        _player.EvaTestSetSuit(SuitState.Fresh());
        _player.EvaTestSetMode(EvaMode.Jetpack);
        Feed(0u);
        Sync();
        Check("переход к иерархии: свободный джетпак, полный скафандр",
              !IsAttached && Mode == EvaMode.Jetpack && Suit.Fuel > 99.9f && Suit.Battery > 99.9f);
    }

    // ==================================================================
    // п.20 (В4): иерархия — EVA-тело в WorldRoot, кресло — в родителе точки
    // ==================================================================
    private void Hier0_Jetpack()
    {
        // П.20: EVA-тело живёт в корневом Node3D мира, а НЕ в кресле/сцене
        // игрока — иначе позывной/иконка «прилипли» бы к последней точке.
        var parent = _player.GetParent();
        Check("п.20: джетпак-режим: тело в WorldRoot",
              parent == _world && !IsAttached);
    }

    private void Hier1_Boots()
    {
        // П.20: прилипание ботинками НЕ переподвешивает тело (контакт —
        // данные, не родитель): игрок остаётся в WorldRoot.
        _player.EvaTestStickTo((StaticBody3D)_deck,
                               new Vector3(BodyPos.X, -0.05f, BodyPos.Z), Vector3.Up);
        Sync();
        Check("п.20: ботинки НЕ меняют родителя (мир общий)",
              Mode == EvaMode.Boots && _player.GetParent() == _world);
    }

    private void Hier2_Seat()
    {
        // П.20 + п.13: посадка в кресло ПЕРЕПОДВЕШИВАЕТ тело в узел точки
        // (тогда локальная поза «в кресле» едет вместе с кораблём на всём
        // этапе). Это та же ветка AttachTo, что в Stage2 (регрессия — не
        // сломать: здесь кресло корабля, не кресло станции).
        if (Mode == EvaMode.Boots)
            _player.EvaTestKeyPress("toggle_boots");
        _player.AttachTo(_chair);
        Sync();
        bool ok = IsAttached && Mode == EvaMode.Seat
                  && _player.GetParent() == _chair;
        Check("п.20/п.13: посадка ⇒ родитель = узел точки кресла", ok);
    }

    private void Slope0_Gate()
    {
        // П.24 (ФЛАГ В13=B): чистый предикат `SuitModel.CanStepOn` обязан
        // пропускать поверхность ровно на границе (60°) и отвергать на 1°
        // круче. Нормаль строим как повёрнутую от вертикали вокруг оси Z:
        // n = (sin A, cos A, 0) ⇒ угол к Up ровно A°. Проверяем 59/60/61°
        // и вырожденную (нулевую) нормаль — та даёт «нет поверхности», отказ.
        static Vector3 Tilt(float deg)
        {
            double a = System.Math.PI * deg / 180.0;
            return new Vector3((float)System.Math.Sin(a), (float)System.Math.Cos(a), 0f);
        }

        bool a59 = SuitModel.CanStepOn(Tilt(59f));
        bool a60 = SuitModel.CanStepOn(Tilt(60f));
        bool a61 = SuitModel.CanStepOn(Tilt(61f));
        bool zero = SuitModel.CanStepOn(Vector3.Zero);
        bool up = SuitModel.CanStepOn(Vector3.Up);
        bool down = SuitModel.CanStepOn(-Vector3.Up); // знак не важен — потолок тоже годится

        Check("п.24: CanStepOn — 59° липнет", a59);
        Check("п.24: CanStepOn — граница 60° включительно липнет", a60);
        Check("п.24: CanStepOn — 61° отказ", !a61);
        Check("п.24: CanStepOn — вырожденная нормаль ⇒ отказ", !zero);
        Check("п.24: CanStepOn — Up/−Up (пол/потолок) липнут", up && down);

        // Реальный путь: отвесная стена (нормаль горизонтальна, 90°) ⇒ отказ.
        // Проверяем предикат тем же значением, каким играет `ActivateBoots`.
        Check("п.24: CanStepOn — стена 90° отказ", !SuitModel.CanStepOn(Vector3.Right));
    }

    // ==================================================================
    // eva-plan Блок E (Е2): hover панели управления + HUD-источник «Suit» (п.32)
    // ==================================================================

    private Player _occupant = null!; // второй игрок-«занятость» (Hover2)

    /// <summary>Найти узел панели управления (ControlPanel) в дереве корабля.</summary>
    private static ControlPanel? PanelOn(Node root)
    {
        foreach (Node n in root.GetChildren())
        {
            if (n is ControlPanel cp)
                return cp;
            var deep = PanelOn(n);
            if (deep != null)
                return deep;
        }
        return null;
    }

    /// <summary>Готовим игрока к hover-фазе: снимаем пристёжку, чтобы он был
    /// свободен (локальный джетпак). МИР НЕ ПЕРЕСОБИРАЕМ: корабль/панель/кресло из
    /// `BuildEvaWorld` стоят с фазы Build и уже многократно схлопнуты в
    /// `PhysicsDirectSpaceState` — пересборка (`RebuildWorld`) в том же кадре, где
    /// запускается `IntersectRay`, наоборот убрала бы корабль из физики (свежие
    /// тела попадают в пространство только на СЛЕДУЮЩЕМ флеше) и луч промахнулся
    /// бы. Корабль статичен (`ProcessMode.Disabled`) и не уезжает между фазами.</summary>
    private Player FreshForHover()
    {
        if (IsAttached) { _player.Detach(); Sync(); }
        _chair = Seat(out _) ?? _chair;
        return _player;
    }

    /// <summary>Внешняя точка-«глаз» перед панелью (сторона −z): панель свисает
    /// с носа корабля, поэтому луч с −z на +z попадает в переднюю грань панели,
    /// НЕ протыкая собственный корпус/палубу корабля (layer 1/256 — они тоже в
    /// маске EVA-луча и перекрыли бы прицел «из тела игрока»).</summary>
    private Vector3 HoverEye()
    {
        var panel = PanelOn(_ship);
        return panel != null ? panel.GlobalPosition + new Vector3(0f, 0.15f, -1.5f)
                             : new Vector3(0f, 0.65f, -3.95f);
    }

    /// <summary>Направление от внешней точки-глаза к центру панели.</summary>
    private Vector3 ToPanel()
    {
        var panel = PanelOn(_ship);
        return panel != null ? (panel.GlobalPosition - HoverEye()) : Vector3.Back;
    }

    private void Hover0_FreePanel()
    {
        FreshForHover();
        Sync();
        Check("hover: старт — свободный джетпак, локальный",
              !IsAttached && Mode == EvaMode.Jetpack);
        Check("hover: панель корабля найдена в дереве", PanelOn(_ship) != null);

        var h = _player.EvaTestHoverAt(HoverEye(), ToPanel());
        Check("п.4: луч находит панель (хит есть)", h != null);
        if (h == null) { GD.Print($"    [DBG] miss=\"{_player.EvaTestLastMiss()}\""); return; }

        Check("п.4: хит по панели кресла, не занятая", h.Value.Panel != null && !h.Value.IsBusy);
        Check("п.4: адрес кресла валидный (OwnerNetId>0, NetId>=0)",
              h.Value.Seat!.OwnerNetId > 0 && h.Value.Seat.NetId >= 0);

        string hint = _player.EvaTestPanelHintText();
        Check("п.5: подсказка строится и содержит «сесть за штурвал»",
              hint.Contains("сесть за штурвал"));
        Check("п.5: подсказка содержит литеру клавиши (keylabel=)",
              hint.Contains("keylabel=") && hint.Length > "keylabel=".Length);
        Check("п.5: свободная панель НЕ говорит «занято»", !hint.Contains("занято"));

        // Готовим перекрытие на СЛЕДУЮЩУЮ фазу: добавляем стенку сейчас — она
        // попадёт в `PhysicsDirectSpaceState` только на следующем флеше, поэтому
        // проверяем промах ровно через одну фазу (Hover1), а не в этом же кадре.
        ArmOccluder();
    }

    private StaticBody3D _occluder = null!; // стенка-препятствие для hover-луча (Hover1)

    /// <summary>Поставить стенку СЛОЯ ПАЛУБЫ (256 — она в маске EVA-луча) ровно
    /// на пути луча между внешней точкой-глазом (z≈−3.95) и панелью (z≈−2.45):
    /// поперёк луча на z≈−3.2. Добавляем её ДО фазы, где проверяем перекрытие:
    /// `PhysicsDirectSpaceState3D` читает состояние прошлого физического флеша,
    /// поэтому тело, добавленное в том же кадре, лучом ещё не видно.</summary>
    private void ArmOccluder()
    {
        // Стенку ставим ровно ПОСЕРЕДИНЕ реального луча «глаз→панель» (корабль
        // после предыдущих фаз уехал, поэтому координаты панели неизвестны
        // заранее — берём их из живой трансформации). Ориентация вдоль -z-оси
        // луча: тонкий по z бокс поперёк луча его перебивает.
        var panel = PanelOn(_ship);
        var eye = HoverEye();
        Vector3 mid = panel != null
            ? eye + (panel.GlobalPosition - eye) * 0.5f
            : new Vector3(0f, 0.5f, -3.2f);
        _occluder = new StaticBody3D
        {
            Name = "HoverOccluder",
            CollisionLayer = CollisionLayers.Deck,
            CollisionMask = 0u,
            Transform = new Transform3D(Basis.Identity, mid),
        };
        _occluder.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(1.5f, 1.5f, 0.2f) },
        });
        _world.AddChild(_occluder);
    }

    private void Hover1_Occluded()
    {
        // Стенка добавлена в прошлом кадре (в конце Hover0) ⇒ в этом кадре она
        // уже в физическом пространстве и перекрывает луч глаз→панель.
        var h = _player.EvaTestHoverAt(HoverEye(), ToPanel());
        bool occluded = h == null && _player.EvaTestLastMiss().Contains("перекрыта");
        if (h != null) GD.Print($"    [DBG occluded?] hit z={h.Value.HitPos.Z:F2} busy={h.Value.IsBusy}");
        Check("п.4: стенка перекрывает луч ⇒ хита нет, отказ объяснён «перекрыта»", occluded);
        Check("п.5: без хита подсказка пуста", _player.EvaTestPanelHintText() == "");

        // Убираем стенку: сначала СИНХРОННО уводим её далеко (присваивание
        // GlobalPosition толкает форму в физическом сервере сразу — луч следующего
        // кадра её уже не видит), затем QueueFree. Freed-бокс стенки какое-то время
        // ещё может попадаться лучам, поэтому не надеемся только на QueueFree.
        if (GodotObject.IsInstanceValid(_occluder))
        {
            _occluder.GlobalPosition = new Vector3(0f, 50f, 0f);
            _occluder.QueueFree();
        }
    }

    private void Hover2_Busy()
    {
        FreshForHover();
        Sync();
        if (IsAttached) { _player.Detach(); Sync(); }
        // Второй игрок занимает кресло этого же корабля (прямое AttachTo —
        // локальная реплика; занятость читается группой "player").
        // Ставим авторитет 2 ДО переноса в дерево: игрок становится НЕ локальным
        // (LocalAuthorityId=1), поэтому его `_Ready` не перезапишет единственный
        // HUD-источник «Suit» локального игрока (§3.5: источник принадлежит только
        // наблюдаемому/локальному). Для занятости кресла авторитет не важен —
        // узел всё равно в группе "player" и `AttachTo` без гейта авторитета.
        _occupant = GD.Load<PackedScene>("res://scenes/Player.tscn")
            .Instantiate<SpaceCorporation.Player>();
        _occupant.Name = "OccupantPlayer";
        _occupant.AutoAttachOnSpawn = false;
        _occupant.SetMultiplayerAuthority(2, false);
        _world.AddChild(_occupant);
        _occupant.AddToGroup("player");
        _occupant.GlobalPosition = new Vector3(3f, 5f, 3f);
        _occupant.EvaTestSetPhysicsPaused(true);
        _occupant.AttachTo(_chair);
        Sync();

        var h = _player.EvaTestHoverAt(HoverEye(), ToPanel());
        Check("п.6: хит по занятой панели есть", h != null);
        Check("п.6: хит сообщает кресло занятым", h != null && h.Value.IsBusy);
        if (h != null)
            Check("п.6: подсказка занятой панели говорит «занято»",
                  Player.PanelHoverHint(h.Value).Contains("занято"));

        // Активация занятого кресла — отказ (свежая занятость, не устаревший хит).
        bool activated = h != null && _player.EvaTestActivateHover(h.Value);
        Check("п.6: активация занятого кресла ⇒ отказ, игрок не пристёгнут",
              !activated && !IsAttached);
    }

    private void Hover3_Activate()
    {
        // Освобождаем кресло от второго игрока и убираем его из мира.
        if (_occupant != null && GodotObject.IsInstanceValid(_occupant))
        {
            _occupant.Detach();
            _occupant.QueueFree();
        }
        for (int i = 0; i < 2; i++)
            ForceSyncFrame();
        if (IsAttached) { _player.Detach(); Sync(); }

        var h = _player.EvaTestHoverAt(HoverEye(), ToPanel());
        Check("п.6: после освобождения панель снова свободна", h != null && !h.Value.IsBusy);
        if (h == null) { GD.Print("    [DBG] Hover3: нет хита, активацию не проверить"); return; }

        int owner = h.Value.Seat!.OwnerNetId;
        bool activated = _player.EvaTestActivateHover(h.Value);
        Sync();
        Check("п.6: активация свободной панели ⇒ игрок в кресле (Seat)",
              activated && IsAttached && Mode == EvaMode.Seat);
        Check("п.6: Reparent в узел кресла", _player.GetParent() == _chair);

        var ship = NetworkManager.Instance?.FindShip(owner);
        Check("п.6: сетевой пилот назначен владельцу игрока (offline⇒1)",
              ship != null && ship.PilotPeerId == _player.GetMultiplayerAuthority());
    }

    private void SuitHud0_Source()
    {
        // п.32/Е3: HUD-источник скафандра «Suit» живёт в DebugInfoBus и даёт
        // mode=/fuel=/batt=. Игрок сейчас в кресле (после Hover3) ⇒ mode=SEAT.
        Check("п.32: в шине есть источник «Suit»",
              DebugInfoBus.HasInfoSource(Player.SuitInfoSourceKey));
        string t = _player.SuitInfoText();
        GD.Print($"    [HUD Suit] {t}");
        Check("п.32: строка «Suit» содержит mode=, fuel=, batt=",
              t.Contains("mode=") && t.Contains("fuel=") && t.Contains("batt="));
        Check("п.32: в кресле строка «Suit» ⇒ mode=SEAT",
              IsAttached && Mode == EvaMode.Seat && t.Contains("mode=SEAT"));
        // Источник переживает перестройку мира (ленив на autoload-шине, снимается
        // в _ExitTree игрока) — не протекает дубликатом: один ключ = одна лямбда.
        Check("п.32: источник «Suit» не дублируется (один ключ)",
              DebugInfoBus.HasInfoSource(Player.SuitInfoSourceKey));
    }

    // ==================================================================
    // P9 — п.12 (Блок D, Д3, BUG-009): серверная занятость кресла
    // ==================================================================
    // Гейт в Player.AttachServerSide: вход в кресло корабля, чей PilotPeerId
    // занят ДРУГИМ живым пиром, обязан быть отвергнут ДО AttachTo/SetPilot —
    // иначе прыжок из EVA «уводит» пилота у чужого корабля (два игрока гоняют
    // один корпус). Тест offline (как стыковка в Stage5Test): реестр NetworkManager
    // работает в offline-режиме, поэтому AttachServerSide прогоняется полным
    // серверным путём. Чужой пир симулируется TestSetPilotForTest(7) — ровно так
    // же, как Stage5Test симулирует двух пиров доками (TestExtraPeers).
    private Ship? NetSeatShip => NetworkManager.Instance?.FindShip(_netSeatOwner);
    private int _netSeatOwner;

    private void NetSeat0_Begin()
    {
        // Чистый мир: прежний (после Hover) уже пережил occupant'а, берём свежий
        // корабль со свежим адресом — иначе счётчик пилотов тащит следы фаз P7/P8.
        RebuildWorld();
        _player.SetMultiplayerAuthority(1, false);

        var chair = Seat(out string why);
        Check("п.12: кресло стенда найдено", chair != null);
        _chair = chair ?? _chair;
        _netSeatOwner = _chair?.OwnerNetId ?? 0;
        Check($"п.12: кресло зарегистрировано в реестре (OwnerNetId>0, {why})",
              _netSeatOwner > 0);
        var ship = NetSeatShip;
        Check("п.12: корабль-владелец найден в реестре по OwnerNetId",
              ship != null && ReferenceEquals(ship, _ship));
        var point = NetworkManager.Instance?.FindAttachPoint(
            _netSeatOwner, _chair?.NetId ?? -1);
        Check("п.12: точка найдена по паре (owner, point) — полный сетевой адрес",
              point != null && point == _chair);
    }

    private void NetSeat1_DenyForeign()
    {
        if (_chair == null || NetSeatShip == null)
        {
            Check("п.12: нет кресла/корабля — DENY не проверить", false);
            return;
        }
        // «Чужой» пир 7 — реального узла у него нет, но серверному гейту важен
        // только PilotPeerId: кабина занята живым игроком-пиром.
        NetSeatShip!.TestSetPilotForTest(7);
        Sync();
        bool freeBefore = !IsAttached && _player.GetParent() == _world;
        Check("п.12: старт DENY-кейса: игрок свободен в мире", freeBefore);

        _player.AttachServerSide(_netSeatOwner, _chair.NetId);
        Sync();
        bool refused = !IsAttached && _player.GetParent() == _world &&
                       _player.AttachedTo == null;
        Check("п.12: вход в занятое чужим пиром кресло ⇒ отказ, игрок не пристёгнут",
              refused);
        Check("п.12: PilotPeerId занятого корабля НЕ перезаписан (BUG-009)",
              NetSeatShip!.PilotPeerId == 7);
        Check("п.12: режим игрока после отказа — прежний EVA (джетпак)",
              Mode == EvaMode.Jetpack && IsFinite(BodyPos));
    }

    private void NetSeat2_AllowFreeAndSelf()
    {
        if (_chair == null || NetSeatShip == null)
        {
            Check("п.12: нет кресла/корабля — ALLOW не проверить", false);
            return;
        }
        // A) Кресло «свободно»: offline FreeShipPilotId = 0 («никто»), вход легален
        //    (тот же путь, что спавн-кресло и AutoAttachToFreeSeat).
        NetSeatShip!.TestSetPilotForTest(NetworkManager.Instance!.FreeShipPilotId);
        _player.AttachServerSide(_netSeatOwner, _chair.NetId);
        Sync();
        bool joinedFree = IsAttached && Mode == EvaMode.Seat &&
                          _player.GetParent() == _chair;
        Check("п.12: вход в свободное кресло легален (PilotPeerId = free)", joinedFree);
        Check("п.12: вход назначил пилотом владельца игрока",
              NetSeatShip!.PilotPeerId == _player.GetMultiplayerAuthority());

        _player.Detach();
        Sync();

        // B) Кресло «занято этим же игроком» (реконнект / пересадка в своё):
        //    PilotPeerId == авторитет игрока — легальный повторный вход.
        NetSeatShip.TestSetPilotForTest(_player.GetMultiplayerAuthority());
        _player.AttachServerSide(_netSeatOwner, _chair.NetId);
        Sync();
        Check("п.12: повторный вход в «своё» кресло легален (PilotPeerId = self)",
              IsAttached && Mode == EvaMode.Seat);
        _player.Detach();
        Sync();
        Check("п.12: net-seat-фаза завершена корректно",
              !IsAttached && Mode == EvaMode.Jetpack && IsFinite(BodyPos));
    }

    // ==================================================================
    // п.13 (Блок D, Д1): поза-реплика EVA — apply на не-владельце
    // ==================================================================
    // Headless без UDP: реального RPC нет, поэтому проверяем СЕРДЦЕВИНУ apply
    // (тот же код, что RpcEvaState вызывает на принимающем пире) через публичный
    // шов TestApplyEvaState, явно указывая пира-авторитета позы. Ключевые
    // свойства, которые проверяет фаза:
    //   • не-владелец применяет мировую позицию/базис из пакета;
    //   • гейт по тиковому счётчику (unreliable переупорядочен/дублируется);
    //   • гейт по авторитету (сторона, не являющаяся владельцем позы, отвергается);
    //   • владелец (_isLocal) чужие позы НЕ применяет;
    //   • расходники и режим едут попутно в пакете.
    private void EvaNet0_PoseReplica()
    {
        RebuildWorld();

        // Реплика чужого игрока: авторитет ставим ДО AddChild (как WorldSpawner),
        // чтобы _Ready зафиксировал _isLocal = (2 == localId 1) = false и копия
        // была именно принимающей стороной, а не нашим испытуемым.
        var replica = GD.Load<PackedScene>("res://scenes/Player.tscn")
            .Instantiate<SpaceCorporation.Player>();
        replica.Name = "RemoteReplica";
        replica.AutoAttachOnSpawn = false;
        replica.SetMultiplayerAuthority(2);
        _world.AddChild(replica);
        replica.EvaTestSetPhysicsPaused(true);
        replica.GlobalPosition = new Vector3(0f, 1f, 0f);

        Check("п.13: реплика — не-владелец (GetMultiplayerAuthority=2)",
              replica.GetMultiplayerAuthority() == 2);

        // База: пакет с тиком 1 ставит позицию/базис/расходники.
        var pos = new Vector3(5f, 2f, -3f);
        var basis = Basis.FromEuler(new Vector3(0f, Mathf.Pi * 0.5f, 0f));
        var quat = basis.GetRotationQuaternion();
        replica.TestApplyEvaState(senderPeer: 2, tick: 1u, pos, quat,
            mode: (int)EvaMode.Jetpack, linVel: new Vector3(3f, 0f, 0f),
            fuel: 73, battery: 41);

        bool posOk = replica.GlobalPosition.DistanceTo(pos) < 1e-3f;
        bool rotOk = replica.GlobalTransform.Basis.IsEqualApprox(basis);
        Check("п.13: поза применена (позиция)", posOk);
        Check("п.13: поза применена (базис/ориентация)", rotOk);
        Check("п.13: расходники реплицированы (fuel=73, batt=41)",
              replica.EvaRemoteFuel() == 73f && replica.EvaRemoteBattery() == 41f);
        Check("п.13: режим пришёл вместе с позой",
              replica.EvaRemoteMode() == EvaMode.Jetpack);
        Check("п.13: тик-гейт зафиксирован (LastRecvTick=1)",
              replica.EvaNetLastRecvTick() == 1u);

        // Просроченный/дублирующий пакет (тик <= последнего) НЕ откатывает позу.
        var stale = new Vector3(-9f, 0f, 0f);
        replica.TestApplyEvaState(senderPeer: 2, tick: 1u, stale,
            Basis.Identity.GetRotationQuaternion(), (int)EvaMode.Boots,
            Vector3.Zero, 10, 10);
        Check("п.13: просроченный пакет (тик не вырос) отвергнут — поза не откатилась",
              replica.GlobalPosition.DistanceTo(pos) < 1e-3f &&
              replica.EvaRemoteMode() == EvaMode.Jetpack &&
              replica.EvaRemoteFuel() == 73f);

        // Сторонний пир (не авторитет позы) не вправе двигать реплику.
        var foreign = new Vector3(99f, 99f, 99f);
        replica.TestApplyEvaState(senderPeer: 5, tick: 9u, foreign,
            Basis.Identity.GetRotationQuaternion(), (int)EvaMode.Jetpack,
            Vector3.Zero, 1, 1);
        Check("п.13: чужой отправитель (не авторитет) отвергнут",
              replica.GlobalPosition.DistanceTo(pos) < 1e-3f);

        // Новое валидное обновление (тик вырос, отправитель = авторитет) принято,
        // даже далёкий скачок (> порога) — поза ставится (жёсткий snap внутри).
        var far = new Vector3(40f, 0f, 0f);
        replica.TestApplyEvaState(senderPeer: 2, tick: 2u, far,
            Basis.Identity.GetRotationQuaternion(), (int)EvaMode.Boots,
            Vector3.Zero, 5, 5);
        Check("п.13: валидное обновление (тик=2) принято, поза=далёкая точка",
              replica.GlobalPosition.DistanceTo(far) < 1e-3f &&
              replica.EvaRemoteMode() == EvaMode.Boots &&
              replica.EvaNetLastRecvTick() == 2u);

        // Владелец (_isLocal, у _player авторитет = localId) чужие позы НЕ применяет:
        // TestApplyEvaState сразу выходит, поза _player не меняется извне.
        var before = _player.GlobalPosition;
        _player.TestApplyEvaState(senderPeer: _player.GetMultiplayerAuthority(),
            tick: 777u, new Vector3(123f, 0f, 0f),
            Basis.Identity.GetRotationQuaternion(), (int)EvaMode.Jetpack,
            Vector3.Zero, 3, 3);
        Check("п.13: владелец (_isLocal) чужие позы не применяет",
              _player.GlobalPosition.DistanceTo(before) < 1e-3f);

        replica.QueueFree();
    }


    // ═════════════ Правки 7-й части: плавный поворот ботинок, FPS-камера,
    // прыжок Space, крепление к палубе (край) ═════════════

    /// <summary>Шаг EVA с кадром заданной маски. `hold`=true — маска держится
    /// все кадры цикла (удержание клавиши); false — только первый кадр
    /// (just-pressed: на следующих шагах бит отсутствует ⇒ «только что нажали»).</summary>
    protected void StepsMask(uint mask, int frames, bool hold = true)
    {
        for (int i = 0; i < frames; i++)
        {
            if (hold || i == 0)
                _player.EvaTestSetInput(InputFrame.FromMask(0, mask));
            _player.EvaTestStep(FlightModel.FixedDt);
        }
        Sync();
    }

    /// <summary>Маленькая палуба для тестов края: плита 2.5×2.5×0.4, ВЕРХ на
    /// высоте `topY`. Строго StaticBody3D (луч `IntersectRay` видит только
    /// коллайдеры физических тел), слой Deck, маска 0.</summary>
    protected StaticBody3D MakeSmallDeck(float centerX, float topY, float centerZ)
    {
        var slab = new StaticBody3D
        {
            Name = "EdgeDeck",
            CollisionLayer = CollisionLayers.Deck,
            CollisionMask = 0u,
            Transform = new Transform3D(Basis.Identity,
                new Vector3(centerX, topY - 0.25f, centerZ)),
        };
        slab.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2.5f, 0.5f, 2.5f) },
        });
        _world.AddChild(slab);
        return slab;
    }

    /// <summary>Тест «поворот НЕ мгновенный» (правка 7-й части): игрок в
    /// джетпаке наклонён на 60°, стоит над палубой, жмёт реальную G. Ботинки
    /// включаются, но доворот тела к нормали — НЕ снапом, а slerp за
    /// `BootsTurnTime` = 0.3 с (18 кадров по 1/60).</summary>
    protected void Turn0_ActivateAnimated()
    {
        RebuildWorld();
        // Замороженный донор-корабль перехватывает лучи ботинок СВОИМ DeckHull
        // (слой Station ∈ BootsMask): у Frozen RigidBody3D физическое тело
        // живёт своей позой в физсервере (перенос ноды его не двигает, а
        // QueueFree в стенде не исполняется — ForceSyncFrame пуст). Гасим
        // коллайдеры донора: формы выпадают из PhysicsDirectSpaceState.
        DisableShipColliders(_ship);
        // Домашняя палуба (верх y=-0.55). Игрок в 0.25 м над верхом: луч
        // ActivateBoots достает до опоры (BootsMaxStickDist+0.1=1.1).
        // Наклон 60° вокруг X — голова завалена вперёд.
        var p = new Vector3(0f, -0.3f, 0f);
        _player.EvaTestSetMode(EvaMode.Jetpack);
        _player.GlobalPosition = p;
        _player.Velocity = Vector3.Zero;
        _player.GlobalTransform = new Transform3D(
            Basis.FromEuler(new Vector3(Mathf.DegToRad(60f), 0f, 0f)), p);
        _player.EvaTestSyncFlightFromBody();
        ForceSyncFrame();
        float upBefore = _player.GlobalTransform.Basis.Y.Dot(Vector3.Up); // ≈0.50
        Check("правка7: наклон до G ≈60° (up·Y≈0.5)", upBefore > 0.45f && upBefore < 0.55f);

        _player.EvaTestKeyPress("toggle_boots");   // реальная клавиша G (без снап-шва)
        Sync();

        Check("правка7: G включила ботинки над палубой",
              Mode == EvaMode.Boots && BootsOn && _player.EvaTestBootsBody() != null);
        Check("правка7: после G запущен плавный доворот (не снап)",
              _player.EvaTestBootsTurning());
        float up0 = BodyUp.Dot(Vector3.Up);
        Check("правка7: сразу после G поза НЕ выровнена (нет мгновенного снапа)",
              up0 < 0.9f && Mathf.Abs(up0 - upBefore) < 0.15f);

        // Первый же кадр доворота НЕ обязан быть равновесным — серво ещё
        // подтягивает игрока к держимому зазору; привязку к палубе ловим
        // после остановки (проверка «не уехал» — от финальной точки стойки).
        _player.EvaTestStep(FlightModel.FixedDt);   // 1-й кадр анимации
        float up1 = _player.GlobalTransform.Basis.Y.Dot(Vector3.Up);
        Check("правка7: 1-й кадр — доворот пошёл (up·Y растёт) и НЕ завершён",
              _player.EvaTestBootsTurning() && up1 > up0 + 0.002f && up1 < 0.9f);

        Vector3 standAfterTurn = Vector3.Zero;
        for (int i = 0; i < 20; i++)                // ещё 20 кадров: всего ≈0.35 с
        {
            _player.EvaTestStep(FlightModel.FixedDt);
            if (!_player.EvaTestBootsTurning() && standAfterTurn == Vector3.Zero)
                standAfterTurn = _player.GlobalPosition;   // финальный кадр доворота
        }
        Sync();
        Check("правка7: за ~0.3+с доворот завершён (таймер погас)",
              !_player.EvaTestBootsTurning());
        Check("правка7: тело довернуто точно к нормали палубы (up·Y>0.999)",
              BodyUp.Dot(Vector3.Up) > 0.999f);
        // Серво липкости дотягивает зазор до держимого (BootsStickOffset=0.05)
        // с малой скоростью (~0.5 м/с) — ждём остановки: равновесие над палубой
        // (верх y=-0.55 ⇒ центр y≈0.4), по горизонтали игрок не уполз.
        for (int i = 0; i < 90; i++)
            _player.EvaTestStep(FlightModel.FixedDt);
        Sync();
        Check("правка7: за время доворота не отцепился",
              _player.EvaTestBootsBody() != null && Mode == EvaMode.Boots);
        Check("правка7: остановился в равновесии над палубой, по горизонтали на месте",
              standAfterTurn != Vector3.Zero
              && Mathf.Abs(_player.GlobalPosition.X) < 0.1f
              && Mathf.Abs(_player.GlobalPosition.Z) < 0.1f
              && _player.GlobalPosition.Y > 0.25f
              && _player.GlobalPosition.Y < 0.55f);
    }

    /// <summary>Тест FPS-камеры ботинок (правка 7-й части): straight-дельта
    /// мыши (пиксели, вброс без курсора) крутит ТЕЛО вокруг нормали (yaw) и
    /// наклоняет ТОЛЬКО камеру (pitch); позиционный suit-канал (0.1 рад/с)
    /// в этом режиме заглушён — 400 px = 1.0 рад за один кадр.</summary>
    protected void Cam0_FpsLook()
    {
        RebuildWorld();
        // Замороженный донор-корабль перехватывает лучи ботинок СВОИМ DeckHull
        // (слой Station ∈ BootsMask): у Frozen RigidBody3D физическое тело
        // живёт своей позой в физсервере (перенос ноды его не двигает, а
        // QueueFree в стенде не исполняется — ForceSyncFrame пуст). Гасим
        // коллайдеры донора: формы выпадают из PhysicsDirectSpaceState.
        DisableShipColliders(_ship);
        _player.EvaTestStickTo(_deck, new Vector3(0f, -0.55f, 0f), Vector3.Up);
        _player.EvaTestStep(FlightModel.FixedDt);
        Sync();
        Check("правка7: камера-тест стартует прилипшим в ботинках",
              Mode == EvaMode.Boots && _player.EvaTestBootsBody() != null);

        // 400 px × 0.0025 рад/px = 1.0 рад yaw — заметно БОЛЬШЕ suit-лимита
        // 0.1 рад/с (за кадр дал бы 0.0017 рад): FPS-канал обязан съесть весь вброс.
        _player.EvaTestFeedBootsLook(new Vector2(400f, 0f));
        _player.EvaTestStep(FlightModel.FixedDt);
        Sync();
        // Старт Cam0 — Basis=Identity (лицом к +Z), поэтому эталон yaw —
        // стартовый Z, а не Forward; знак: положительный твист вокруг Up
        // вращает Z к +X.
        Vector3 bz = _player.GlobalTransform.Basis.Z;
        float yawRad = Mathf.Atan2(bz.X, bz.Z);
        Check("правка7: дельта мыши X повернула тело ≈1 рад вокруг нормали (FPS)",
              yawRad > 0.95f && yawRad < 1.05f);
        Check("правка7: после FPS-yaw тело перпендикулярно палубе (up·Y>0.999)",
              BodyUp.Dot(Vector3.Up) > 0.999f);
        Vector3 stand = new Vector3(0f, -0.55f + Player.CapsuleHeight * 0.5f
            + SuitModel.BootsStickOffset, 0f);
        Check("правка7: FPS-доворот не сдвинул игрока с места",
              BodyPos.DistanceTo(stand) < 0.05f);

        // Pitch (вброс по Y) уходит на CameraRig, НЕ наклоняет тело.
        _player.EvaTestFeedBootsLook(new Vector2(0f, 200f));   // 0.5 рад
        _player.EvaTestStep(FlightModel.FixedDt);
        Sync();
        Check("правка7: дельта мыши Y дала тангаж камеры ≈0.5 рад",
              Mathf.Abs(_player.EvaTestBootsCamPitch() - 0.5f) < 0.05f);
        Check("правка7: тангаж камеры НЕ наклонил тело (up всё ещё нормаль)",
              BodyUp.Dot(Vector3.Up) > 0.999f);

        // Обрезка потолком 70°.
        _player.EvaTestFeedBootsLook(new Vector2(0f, 100000f));
        _player.EvaTestStep(FlightModel.FixedDt);
        Check("правка7: тангаж обрезан потолком 70°",
              Mathf.Abs(_player.EvaTestBootsCamPitch() - Mathf.DegToRad(70f)) < 1e-3f);

        // Straight-модель: без нового вброса дельта нулевая (нет автоповтора
        // курсора, в отличие от позиционного канала джетпака).
        float pitchHeld = _player.EvaTestBootsCamPitch();
        _player.EvaTestStep(FlightModel.FixedDt);
        _player.EvaTestStep(FlightModel.FixedDt);
        Check("правка7: без движения мыши взгляд стоит (инерции курсора нет)",
              Mathf.Abs(_player.EvaTestBootsCamPitch() - pitchHeld) < 1e-6f);

        // Выход из ботинок снимает FPS-тангаж с камеры (в джетпаке — нейтраль).
        _player.EvaTestSetMode(EvaMode.Jetpack);
        Check("правка7: выход из ботинок обнулил FPS-тангаж",
              Mathf.Abs(_player.EvaTestBootsCamPitch()) < 1e-6f);
    }

    /// <summary>Тест прыжка Space из ботинок (правка 7-й части): just-pressed
    /// ThrustU ⇒ импульс вдоль нормали (3.5 м/с), в полёте притяжение назад
    /// (g=14 ⇒ вершина ≈0.44 м, полёт ≈0.5 с), приземление ⇒ стойка, ботинки
    /// НЕ гаснут; в полёту wish-тяги нет (приземляемся в точку старта).</summary>
    protected void Jump0_LeapAndLand()
    {
        RebuildWorld();
        // Замороженный донор-корабль перехватывает лучи ботинок СВОИМ DeckHull
        // (слой Station ∈ BootsMask): у Frozen RigidBody3D физическое тело
        // живёт своей позой в физсервере (перенос ноды его не двигает, а
        // QueueFree в стенде не исполняется — ForceSyncFrame пуст). Гасим
        // коллайдеры донора: формы выпадают из PhysicsDirectSpaceState.
        DisableShipColliders(_ship);
        _player.EvaTestStickTo(_deck, new Vector3(0f, -0.55f, 0f), Vector3.Up);
        for (int i = 0; i < 5; i++)
            _player.EvaTestStep(FlightModel.FixedDt);   // серво устаканило зазор
        Sync();
        Vector3 standPos = BodyPos;
        float yStand = BodyPos.Y;
        Check("правка7: прыжок-тест: старт стоя, фаза прыжка выключена",
              Mode == EvaMode.Boots && !_player.EvaTestBootsJumping());

        StepsMask(InputMask.ThrustU, 1, hold: false);   // just-pressed Space
        Check("правка7: Space запустил фазу прыжка", _player.EvaTestBootsJumping());
        Check("правка7: 1-й кадр прыжка — игрок уже вырос над палубой",
              BodyPos.Y > yStand + 0.01f);

        float peakY = BodyPos.Y;
        bool airborneLater = false;
        for (int i = 0; i < 25; i++)
        {
            _player.EvaTestStep(FlightModel.FixedDt);   // чистый полёт (ввод не держим)
            peakY = Mathf.Max(peakY, _player.GlobalPosition.Y);
            if (i < 15 && _player.EvaTestBootsJumping()) airborneLater = true;
        }
        Check("правка7: на 15-м кадре полёта фаза ещё активна (не щелчок туда-сюда)",
              airborneLater);
        Check("правка7: вершина прыжка в разумных пределах (0.2…0.75 м над стойкой)",
              peakY > yStand + 0.2f && peakY < yStand + 0.75f);

        bool landed = false;
        for (int i = 0; i < 40; i++)
        {
            _player.EvaTestStep(FlightModel.FixedDt);
            if (!_player.EvaTestBootsJumping()) { landed = true; break; }
        }
        Sync();
        Check("правка7: приземлился — фаза прыжка завершилась", landed);
        Check("правка7: приземление НЕ погасило ботинки",
              Mode == EvaMode.Boots && _player.EvaTestBootsBody() != null);
        Check("правка7: вернулся на держимый уровень (|Δy| < 0.1)",
              Mathf.Abs(BodyPos.Y - yStand) < 0.1f);
        Check("правка7: приземлился возле точки старта (wish-тяга в полёте мертва)",
              BodyPos.DistanceTo(standPos) < 0.3f);
    }

    /// <summary>Тест «вылетел за край в прыжке ⇒ ботинки гаснут, джетпак»
    /// (правка 7-й части): игрок прыгает С РАЗГОНОМ по плите; касательная
    /// скорость замораживается (3 м/с) и в полёту несёт его за край — магнитный
    /// луч опоры под центром капсулы гаснет ⇒ ботинкиoff + EvaMode.Jetpack.</summary>
    protected void Edge0_JumpOffExtinguishes()
    {
        RebuildWorld();
        // Замороженный донор-корабль перехватывает лучи ботинок СВОИМ DeckHull
        // (слой Station ∈ BootsMask): у Frozen RigidBody3D физическое тело
        // живёт своей позой в физсервере (перенос ноды его не двигает, а
        // QueueFree в стенде не исполняется — ForceSyncFrame пуст). Гасим
        // коллайдеры донора: формы выпадают из PhysicsDirectSpaceState.
        DisableShipColliders(_ship);
        // Большую домашнюю палубу УБИРАЕМ из физпространства гашением форм
        // (QueueFree отложен, а EvaTestStep не докручивает реальные кадры —
        // её free-space бокс весь сценарий попадался бы пробам у края).
        DisableShipColliders(_deck);
        // Плита 2.5×2.5: верх y=-0.55, центр (0,-0.75,0); нос тела по -Z ⇒
        // right = fwd×normal = +X, т.е. ThrustR гонит вдоль +X к краю x=+1.25.
        var slab = MakeSmallDeck(0f, -0.55f, 0f);
        _player.EvaTestStickTo(slab, new Vector3(0f, -0.55f, 0f), Vector3.Up);
        for (int i = 0; i < 3; i++)
            _player.EvaTestStep(FlightModel.FixedDt);

        // Ровно 10 кадров разгона (x≈0.5; проба о краю впереди ещё молчит) —
        // и на следующем кадре just-pressed Space.
        StepsMask(InputMask.ThrustR, 10);
        float xAtJump = _player.GlobalPosition.X;
        StepsMask(InputMask.ThrustR | InputMask.ThrustU, 1, hold: false);
        Check("правка7: прыжок с разгона к краю стартовал (фаза активна)",
              _player.EvaTestBootsBody() != null && _player.EvaTestBootsJumping());

        bool burnedOut = false;
        for (int i = 0; i < 45; i++)
        {
            _player.EvaTestStep(FlightModel.FixedDt);
            if (_player.Mode == EvaMode.Jetpack && _player.EvaTestBootsBody() == null)
            { burnedOut = true; break; }
        }
        Sync();
        Check("правка7: в полёту занесло за точку прыжка на 0.3+ м (carry tanVel)",
              _player.GlobalPosition.X > xAtJump + 0.3f || burnedOut);
        Check("правка7: вылетел за край в прыжке ⇒ ботинкипогасли, джетпак",
              burnedOut && Mode == EvaMode.Jetpack && !_player.EvaTestBootsJumping());
    }

    /// <summary>Тест крепления к краю (правка 7-й части): ходьба к краю гасится
    /// пробой-лучом — игрок упирается в край, НЕ выходит за него и не сваливается.</summary>
    protected void Edge1_WalkStopsAtEdge()
    {
        RebuildWorld();
        // Замороженный донор-корабль перехватывает лучи ботинок СВОИМ DeckHull
        // (слой Station ∈ BootsMask): у Frozen RigidBody3D физическое тело
        // живёт своей позой в физсервере (перенос ноды его не двигает, а
        // QueueFree в стенде не исполняется — ForceSyncFrame пуст). Гасим
        // коллайдеры донора: формы выпадают из PhysicsDirectSpaceState.
        DisableShipColliders(_ship);
        // Большую домашнюю палубу УБИРАЕМ из физпространства гашением форм
        // (QueueFree отложен, а EvaTestStep не докручивает реальные кадры —
        // её free-space бокс весь сценарий попадался бы пробам у края).
        DisableShipColliders(_deck);
        var slab = MakeSmallDeck(0f, -0.55f, 0f);   // нос по -Z, right=+X ⇒ край x=+1.25
        _player.EvaTestStickTo(slab, new Vector3(0f, -0.55f, 0f), Vector3.Up);
        for (int i = 0; i < 3; i++)
            _player.EvaTestStep(FlightModel.FixedDt);
        Sync();
        Check("правка7: край-тест: игрок в центре плиты",
              Mode == EvaMode.Boots && Mathf.Abs(BodyPos.X) < 0.2f);

        StepsMask(InputMask.ThrustR, 60);   // 1 с по +X: без пробы ушёл бы на 3 м
        Check("правка7: к краю дошёл (0.5 м из центра и больше)", BodyPos.X > 0.5f);
        // Проба смотрит на 0.4 м вперёд ⇒ центр останавливается, не доходя края.
        Check("правка7: за край НЕ вышел (центр x < 1.2)", BodyPos.X < 1.2f);
        Check("правка7: у края прилип сохранён (ботинки горят, не свалился)",
              _player.EvaTestBootsBody() != null && Mode == EvaMode.Boots
              && BodyPos.Y > -0.5f);

        StepsMask(InputMask.ThrustR, 60);   // ещё 1 с упора в край
        Check("правка7: у края стоит на месте (не соскальзывает за край)",
              BodyPos.X < 1.2f && _player.EvaTestBootsBody() != null
              && Mode == EvaMode.Boots && BodyPos.Y > -0.5f);
    }
}
