using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    /// <summary>Дистанция камеры-преследования за кормой (шаг 2.2.3).</summary>
    [Export] public float CameraDistance = 12f;

    /// <summary>Подъём камеры над кормой, чтобы корпус не перекрывал вид.</summary>
    [Export] public float CameraHeight = 3f;

    /// <summary>Таймер печати метрик §2.6 в headless-стенде (раз в секунду).</summary>
    private float _metricsTimer;

    /// <summary>Камера-преследование, которую корабль создаёт себе сам, когда его
    /// пилотирует локальный пир. Ссылка нужна, чтобы погасить её при выходе пилота:
    /// иначе в world-viewport останется вторая current-камера.</summary>
    private Camera3D? _chaseCam;

    /// <summary>Чужую камеру (обычно камеру игрока) глушим при пилотировании и
    /// возвращаем при освобождении камеры корабля.</summary>
    private Camera3D? _silencedCam;

    /// <summary>Собрать кадр ввода с реальных клавиш (только пилоту, см.
    /// `IsLocalPilot`). Дискретная модель: каждое действие — бит, поэтому
    /// `Input.IsActionPressed`, а не аналоговые оси.</summary>
    private InputFrame CollectInputFrame()
    {
        uint mask = 0;
        if (Input.IsActionPressed("thrust_f")) mask |= InputMask.ThrustF;
        if (Input.IsActionPressed("thrust_b")) mask |= InputMask.ThrustB;
        if (Input.IsActionPressed("thrust_l")) mask |= InputMask.ThrustL;
        if (Input.IsActionPressed("thrust_r")) mask |= InputMask.ThrustR;
        if (Input.IsActionPressed("thrust_u")) mask |= InputMask.ThrustU;
        if (Input.IsActionPressed("thrust_d")) mask |= InputMask.ThrustD;
        // Режим взаимодействия: корабль перестаёт управляться по тангажу/рысканью/крену
        // (и мышью — см. `HandleMouseMotion`, и клавишами Z/X/C/V/Q/E — здесь). Тяга
        // (WASD/boost) остаётся: игрок освобождает мышь для интерфейса, а не глушит
        // двигатели.
        if (!IsInteractionMode)
        {
            if (Input.IsActionPressed("torque_pitch_up")) mask |= InputMask.TorquePitchUp;
            if (Input.IsActionPressed("torque_pitch_down")) mask |= InputMask.TorquePitchDown;
            if (Input.IsActionPressed("torque_yaw_left")) mask |= InputMask.TorqueYawLeft;
            if (Input.IsActionPressed("torque_yaw_right")) mask |= InputMask.TorqueYawRight;
            if (Input.IsActionPressed("torque_roll_left")) mask |= InputMask.TorqueRollLeft;
            if (Input.IsActionPressed("torque_roll_right")) mask |= InputMask.TorqueRollRight;
        }
        if (Input.IsActionPressed("boost")) mask |= InputMask.Boost;

        // Автопилот headless-стенда (§2.6): в --headless никто не жмёт WASD, поэтому
        // пилот сам гонит тягу и периодически доворачивает рысканием — иначе
        // `Prediction Error` и число откатов в логе всегда нулевые и метрики §2.6
        // нечем измерить.
        //
        // ВАЖНО: в космосе нет трения, поэтому «просто жми тягу вперёд» разгоняет
        // корабль до сотен м/с (стенд улетал на 13 000 м за 16 с, ~190 м/с). При
        // такой скорости даже субкадровый рассинхрон тиков пилота и сервера даёт
        // доли метра (1 кадр на 190 м/с = 3,2 м), и критерий §2.6 (а) `predErr <
        // 0,3 м` становится недостижимым физически, а не из-за бага. Поэтому
        // автопилот держит ЦЕЛЕВУЮ СКОРОСТЬ: тяга вперёд, пока медленнее цели, и
        // торможение назад, когда быстрее. Скорость ограничена ~25 м/с → 1 кадр =
        // 0,42 м, субкадровый дрейф = сантиметры, метрика осмысленна.
        //
        // Автопилот считает решение по СКОРОСТИ ПИЛОТА (_predicted.LinearVelocity):
        // он работает только на пилоте (сервер-авторитет для чужого корабля читает
        // кадры из очереди, а не крутит автопилот), решение запекается в маску кадра
        // и сервер интегрирует ровно те же кадры — детерминизм сохраняется.
        // ГАТ ПО РОЛИ (фикс OBS-002): автопилот — это стендовый пилот, он
        // имеет право крутить тягу ТОЛЬКО там, где локальный пир реально ведёт
        // предсказание и шлёт кадры (Offline, Host, Client). На dedicated-
        // сервере локальный пир (SelfId=1) никогда не является целевым пилотом:
        // у нового корабля PilotPeerId=1 по умолчанию (строка 42), поэтому до
        // пристёгивания клиента `IsLocalPilot` ложно == true, и без этого гейта
        // сервер гонял `ThrustF` каждый физшаг ~8 с (в космосе трения нет) —
        // корабль улетал на ~950 м к моменту attach, первая же реконсиляция
        // давала `err ≈ 955 м` → жёсткий откат и резонанс bang-bang-регулятора
        // с частотой снимков (hardRollbacks 21…79/22 с, метрики §2.6 нерабочи).
        // Сервер-авторитет обязан брать ввод из очереди кадров пилота
        // (`FrameForStep`), а не из автопилота. См. bugs.md OBS-002.
        if (NetworkManager.Autopilot &&
            NetworkManager.Instance?.Role != NetworkManager.NetRole.Dedicated)
        {
            // Цель 6 м/с. Две планки связывают выбор скорости:
            //  • критерий (в) §2.6 `maxHullDelta < 0,15 м` меряет смещение hull
            //    наблюдателя между СОСЕДНИМИ кадрами отрисовки. У плавно летящего
            //    корабля это просто скорость/частота_отрисовки. Замер в headless-
            //    демке дал ~50 к/с (не 60): при 8 м/с это 0,16 м — ПОРОГ ПРОБИТ,
            //    метрика теряет смысл (дёргается даже без «резинга»). Значит
            //    крейсерская скорость стенда обязана быть < 0,15×50 = 7,5 м/с;
            //    берём 6 м/с с запасом: 0,12 м при 50 к/с, 0,10 м при 60 к/с.
            //  • критерии (а)/(б): чем медленнее, тем меньше субкадровый дрейф
            //    тиков пилот↔сервер (1 кадр = 0,10 м) — заведомо под порогом.
            // Мёртвая зона
            // (coast) между «тяга» и «тормоз» гасит чаттер bang-bang: без неё на
            // границе скорости пилот каждый кадр переключает тягу/тормоз, а это
            // разрывы ускорения, которые накладываются на фазовый сдвиг тиков и
            // дают ошибки у порога (зубцы hardRollbacks). Тянем, пока медленнее
            // цели; тормозим, только когда быстрее цели × 1,6; в промежутке — накат.
            const float TargetSpeed = 6f;      // м/с — крейсерский ход стенда (см. планку (в) выше)
            const float BrakeBand = 1.6f;      // тормозим, когда скорость > цели × 1,6
            float speed = _predicted.LinearVelocity.Length();
            if (speed < TargetSpeed)
                mask |= InputMask.ThrustF;
            else if (speed > TargetSpeed * BrakeBand)
                mask |= InputMask.ThrustB;
            // Редкие довороты рысканием — чтобы в метриках жил и вращательный канал
            // (угловая скорость в снимке, реконсиляция ориентации). Фаза каждые 90
            // тиков, поворот только на нечётных фазах, чтобы не мешать удержанию курса.
            int phase = (int)(_tick / 90) % 4;
            if (phase == 1) mask |= InputMask.TorqueYawRight;
            else if (phase == 3) mask |= InputMask.TorqueYawLeft;
        }

        // Оси восстанавливает FromMask: в провод уходит только tick+mask+мышь
        // (16 байт, §1.3), а сервер, прочитав маску, обязан получить ровно тот
        // же кадр, что интегрировал клиент, — иначе предсказание (шаг 2.4)
        // разойдётся с серверной истиной на ровном месте.
        //
        // Режим пилотирования 2.0: НОС доворачивает на ПОЗИЦИЮ курсора-прицела
        // (центр экрана = точка направления носа), а не на накопленную дельту
        // мыши. Скорость доворота пропорциональна удалению курсора от центра:
        // 0 внутри слепой зоны (PilotAim.DeadzoneRadiusPx), 100% на окружности
        // радиусом ¼ высоты экрана (PilotAim.RadiusFraction). Аддитивный
        // `_lookDelta` остаётся отдельным каналом (его кормит тестовый шов
        // TestAddLookDelta; игровая мышь его больше НЕ наполняет — см.
        // HandleMouseMotion).
        Vector2 lookPos = PilotLookFromCursor();
        InputFrame f = InputFrame.FromMask(_tick, mask,
            yaw: _lookDelta.X * MouseSensitivity + lookPos.X,
            pitch: _lookDelta.Y * MouseSensitivity + lookPos.Y);
        // Накопленную мышиную дельту съедаем ровно один раз: иначе следующий
        // физкадр повторно довернул бы нос теми же пикселями (шаг 2.3: look
        // теперь применяется из кадра, а не из `_lookDelta` напрямую).
        _lookDelta = Vector2.Zero;
        return f;
    }

    /// <summary>
    /// Позиционная команда доворота носа (рад за шаг) на курсор-прицел в режиме
    /// пилотирования 2.0. Ноль, если: мышь ещё не «видели» (headless-детерминизм
    /// — без мыши позиция (0,0)=угол окна сама бы крутила корабль), не пилот,
    /// режим взаимодействия (мышь кликает по UI), либо автопилот-стенд (его
    /// поворот живёт в маске Torque*, а не в look-канале).
    /// Центр экрана и радиус «полной скорости» берутся из актуального размера
    /// вьюпорта, поэтому схема работает при любом разрешении окна.
    /// </summary>
    private Vector2 PilotLookFromCursor()
    {
        if (!_hasPilotCursor || !IsLocalPilot || IsInteractionMode)
            return Vector2.Zero;
        if (NetworkManager.Autopilot)
            return Vector2.Zero;
        Vector2 view = GetViewport().GetVisibleRect().Size;
        if (view.X <= 0f || view.Y <= 0f)
            return Vector2.Zero;
        // Полный радиус — по ВЫСОТЕ окна (ТЗ: ¼ вертикального размера);
        // слепая зона 100px — константа PilotAim.
        return PilotAim.LookRadiansPerStep(
            _pilotCursorPx,
            PilotAim.ScreenCenter(view),
            PilotAim.FullSpeedRadiusPx(view.Y),
            MaxAngularSpeed,
            FlightModel.FixedDt);
    }


    /// <summary>
    /// Прямой доворот носа мышью (шаг 2.2.2). Мышь — НЕ момент: угловая
    /// скорость от неё не появляется (иначе демпфирование «догоняло» бы нос, и
    /// прицел разъезжался бы с курсором). Поворот — локальные оси: тангаж вокруг
    /// X, рыскание вокруг Y (крен не управляем: мышь в кабине не кренит).
    ///
    /// Шаг 2.3: ядро переведено в радианы (`ApplyLookRadians`), потому что в
    /// кадре ввода мышь летит уже в радианах — offline-пилот запёк пиксели в
    /// `CollectInputFrame`, а сервер с внешним пилотом получает радианы прямо
    /// из `RpcSubmitInput`. Пиксельная обёртка осталась для тестов-швов.
    /// </summary>
    private Quaternion ApplyLookRadians(Quaternion rot, float yawRad, float pitchRad)
    {
        if (yawRad == 0f && pitchRad == 0f)
            return rot;

        // Знак по Y инвертирован: движение мыши ВВЕРХ (relative.Y < 0) — нос
        // вверх (положительный тангаж вокруг локальной X).
        //
        // Порядок множителей: сначала мировой поворот (рыскание вокруг мировой Y),
        // затем локальный тангаж (вокруг X уже повёрнутого корпуса) — так мышь
        // не даёт крена при сводом в вертикаль взгляде (классический gimbal-фикс
        // для кабели: yaw мировой, pitch локальный).
        var q = new Basis(Vector3.Up, -yawRad) * new Basis(Vector3.Right, -pitchRad);
        return (new Basis(rot) * q).GetRotationQuaternion().Normalized();
    }

    /// <summary>Пиксельная обёртка над `ApplyLookRadians`: переводит дельту
    /// курсора в радианы через `MouseSensitivity`. Нужна швам теста
    /// (`TestAddLookDelta`), которые мыслят пикселями, как и реальный ввод.</summary>
    private Quaternion ApplyLook(Quaternion rot, Vector2 deltaPixels)
    {
        Vector2 rad = deltaPixels * MouseSensitivity;
        return ApplyLookRadians(rot, rad.X, rad.Y);
    }

    // ==================================================================
    // Шаг 2.3: кадры ввода от пилота → серверная симуляция (§3.5 п.8, В14=B)
    // ==================================================================

    /// <summary>
    /// Камера от третьего лица за кормой (шаг 2.2.3). Камера обязана быть
    /// ДОЧЕРНЕЙ узлом тела: тогда её позиция и ориентация берутся из трансформа
    /// тела автоматически, и писать её каждый кадр не нужно (а писать пришлось
    /// бы, потому что картинка `ShipHull` для не-авторитета интерполируется, а
    /// камера обязана смотреть туда, куда летит ИСТИНА).
    ///
    /// Чужую камеру (обычно `Player/Camera3D`) глушим: две current-камеры в одном
    /// viewport дают случайный вид, а не «последняя победила».
    /// </summary>
    private void EnsureChaseCamera()
    {
        // Запоминаем текущую камеру ДО момента, когда chase-камера войдёт в дерево:
        // в Godot присвоение Current = true в момент AddChild переключает вьюпорт на
        // неё, и afterward GetCamera3D() вернёт уже её — освобождать было бы нечего
        // (bug found 2026-08 при переходе на посадку по V: после отстёжки в вьюпорте
        // не оставалось активной камеры — камера игрока «гаслась» вьюпортом
        // автоматически и обратно уже не включалась).
        if (_chaseCam == null && (_silencedCam == null || !GodotObject.IsInstanceValid(_silencedCam)))
            _silencedCam = GetViewport()?.GetCamera3D();

        if (_chaseCam == null)
        {
            _chaseCam = new Camera3D
            {
                Name = "ChaseCamera",
                // Смещение назад по -Z корпуса = за кормой (вперёд у Godot = -Z).
                Position = new Vector3(0f, CameraHeight, CameraDistance),
                Current = true,
                // Дальность видимости: без неё корабль улетает за far plane и
                // «исчезает» — выглядит как падение физики.
                Far = 4000f,
            };
            AddChild(_chaseCam);
        }
        else
        {
            _chaseCam.Position = new Vector3(0f, CameraHeight, CameraDistance);
            _chaseCam.Current = true;
        }

        // Камера игрока: гасим, чтобы она не спорила с камерой корабля.
        if (_silencedCam == _chaseCam)
            _silencedCam = null;
        if (_silencedCam != null && GodotObject.IsInstanceValid(_silencedCam))
            _silencedCam.Current = false;
    }

    /// <summary>Отдать камеру обратно: у корабля больше нет локального пилота либо
    /// пилот перешёл в вид «из глаз» (V, added 2026-08).</summary>
    private void ReleaseChaseCamera()
    {
        if (_chaseCam != null && GodotObject.IsInstanceValid(_chaseCam))
        {
            _chaseCam.Current = false;
            _chaseCam.QueueFree();
            _chaseCam = null;
        }
        if (_silencedCam != null && GodotObject.IsInstanceValid(_silencedCam))
            _silencedCam.Current = true;
        _silencedCam = null;
    }

    // ─── Вид пилота: «из глаз» ⇄ chase (V, added 2026-08) ────────────────────

    /// <summary>Локальный режим вида этого пилота (не сетевой: у каждого пира он
    /// свой, как и сама chase-камера). false = «из глаз» (камера игрока, дефолт
    /// пристёгнутого пилота), true = chase за кормой.</summary>
    private bool _pilotChaseView;

    /// <summary>Текущий вид локального пилота: true = chase за кормой,
    /// false = «из глаз». Читает HUD (статус-бар, иконка CAM), пишет только V.</summary>
    public bool PilotChaseView => _pilotChaseView;

    /// <summary>Клавиша V локально: переключить вид пилота «из глаз» ⇄ chase.
    /// Вызывается только локальным игроком (Player) по действию
    /// `toggle_pilot_camera` — по сети ничего не летит.</summary>
    public void TogglePilotCamera()
    {
        if (!IsLocalPilot)
            return;
        _pilotChaseView = !_pilotChaseView;
        ApplyPilotCamera();
        GD.Print($"ship#{Net?.ShipNetId ?? 0}: вид пилота → " +
                 (_pilotChaseView ? "chase" : "из глаз"));
    }

    /// <summary>Привести вьюпорт к выбранному виду: chase — создать камеру и
    /// погасить чужую; «из глаз» — убрать chase и вернуть чужую (камеру игрока).</summary>
    private void ApplyPilotCamera()
    {
        if (_pilotChaseView)
            EnsureChaseCamera();
        else
            ReleaseChaseCamera();
    }

    /// <summary>Завести ввод пилота: сборщик мыши + свободный курсор-прицел +
    /// режим Pilot. Курсор в пилотировании 2.0 НЕ захватывается: он свободен
    /// (`MouseModeEnum.Visible`) и служит прицелом (нос доворачивает на его
    /// позицию). Прежний режим мыши возвращаем при отпускании.</summary>
    private void EnsurePilotInput()
    {
        EnsureMouseCatcher();
        if (!_capturedInput)
        {
            _capturedInput = true;
            _prevMouseMode = Input.MouseMode;
            // Свежая посадка в пилотское кресло — всегда режим пилотирования,
            // даже если в прошлой сессии игрок ушёл в взаимодействие.
            Mode = ControlMode.Pilot;
            // Курсор свободен и виден: это и есть прицел-маркер, а не «захват».
            Input.MouseMode = Input.MouseModeEnum.Visible;
            // Курсор стартует в центре экрана — точка-маркер направления носа.
            // Иначе нос рванул бы к месту, где мышь осталась после посадки, а
            // прицельная линия прыгнула бы с последней UI-точки.
            WarpPilotCursorToCenter();
        }
    }

    /// <summary>Вернуть курсор-прицел в центр экрана (и отметить его как
    /// актуальный), чтобы доворот стартовал «на нуле» и без рывка.</summary>
    private void WarpPilotCursorToCenter()
    {
        Vector2 view = GetViewport().GetVisibleRect().Size;
        if (view.X <= 0f || view.Y <= 0f)
            return;
        _pilotCursorPx = view * 0.5f;
        _hasPilotCursor = true;
        // Warp безвреден и в headless (просто сдвиг ОS-курсора в центр); в
        // безголовом режиме он ничего не делает, но `_pilotCursorPx` выше уже
        // задаёт корректную стартовую точку прицела. `Input.WarpMouse` — рабочий
        // биндинг Godot 4.7 (у Window метода WarpMousePosition нет).
        Input.WarpMouse(view * 0.5f);
    }

    /// <summary>Отпустить ввод пилота: корабль перестал пилотироваться локально
    /// (отстёжка F, выход, смена авторитета, удаление узла).</summary>
    private void ReleasePilotInput()
    {
        ReleaseMouseCatcher();
        if (_capturedInput)
        {
            _capturedInput = false;
            Input.MouseMode = _prevMouseMode;
        }
        _lookDelta = Vector2.Zero;
        // Прицел-курсор больше не актуален: без пилота позиция курсора не должна
        // никуда доворачивать (и не «зависает» с прошлой сессии пилотирования).
        _hasPilotCursor = false;
    }


    // ==================================================================
    // Шаг 2.2.2: сборщик мыши пилота
    // ==================================================================

    /// <summary>
    /// Завести узел-сборщик мыши (шаг 2.2.2). Он нужен потому, что `Ship` — это
    /// `RigidBody3D`, то есть `CollisionObject3D`: у таких узлов метод `_input`
    /// вызывается только когда вьюпорт пикирует именно это тело, и мышиные
    /// события в живой прогон к кораблю не приходят (проба
    /// `tests/probes/ProbeInputHeadless.tscn`: событие дошло до корня сцены и до
    /// дочернего `Node3D`, но не до корабля, даже при `input_ray_pickable = true`).
    /// Обычный `Node3D`-потомок мышь получает всегда, поэтому ввод мыши собирает
    /// он и передаёт кораблю напрямую.
    ///
    /// Узел создаётся один раз и живёт, пока корабль пилотируется; при освобождении
    /// удаляется, чтобы чужой пир не копил дельты.
    /// </summary>
    private ShipMouseCatcher? _mouseCatcher;

    private void EnsureMouseCatcher()
    {
        if (_mouseCatcher != null && GodotObject.IsInstanceValid(_mouseCatcher))
            return;
        _mouseCatcher = new ShipMouseCatcher(this) { Name = "MouseCatcher" };
        AddChild(_mouseCatcher);
    }

    /// <summary>Убрать сборщик мыши (корабль перестал пилотироваться локально).</summary>
    private void ReleaseMouseCatcher()
    {
        if (_mouseCatcher != null && GodotObject.IsInstanceValid(_mouseCatcher))
            _mouseCatcher.QueueFree();
        _mouseCatcher = null;
        _lookDelta = Vector2.Zero;
    }

    /// <summary>
    /// Держатель мыши пилота: обычный `Node3D`, чей `_input` вьюпорт вызывает без
    /// пикинга. Отдаёт дельту кораблю и ловит переключатель режимов (правая кнопка
    /// мыши, действие `toggle_interaction`).
    /// </summary>
    private partial class ShipMouseCatcher : Node3D
    {
        private readonly Ship _ship;

        public ShipMouseCatcher(Ship ship) => _ship = ship;

        public override void _Input(InputEvent @event)
        {
            // Правая кнопка мыши: пилотирование ↔ взаимодействие. Ловим именно
            // здесь, а не в `Ship._Input`: `Ship` — `RigidBody3D`, и его `_input`
            // вьюпорт вызывает только при пикинге тела (проба
            // `tests/probes/ProbeInputHeadless.tscn`), а этот узел получает всё.
            if (@event.IsActionPressed("toggle_interaction"))
            {
                _ship.ToggleInteractionMode();
                return;
            }
            if (@event is InputEventMouseMotion m)
            {
                _ship.HandleMouseMotion(m);
                return;
            }
            // Шаг 3.3: ЛКМ (действие `fire`) — запрос выстрела. Гейты
            // (IsLocalPilot && !Interaction && AimOnShip) внутри RequestFire;
            // ловушка существует только у локального пилота (EnsureMouseCatcher),
            // но гейт на всякий случай продублирован внутри — шов теста дёргает
            // RequestFire напрямую, минуя мышь.
            if (@event.IsActionPressed("fire"))
                _ship.RequestFire();
        }
    }

}
