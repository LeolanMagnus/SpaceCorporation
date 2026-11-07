using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    /// <summary>
    /// Физический шаг. С шага 2.3 метод работает для двух ролей (§3.5 п.8):
    ///   - авторитет (сервер): снимает состояние тела в `NetState`, откуда
    ///     `MultiplayerSynchronizer` рассылает снапшоты (30 Гц); саму физику тела
    ///     ведёт `_IntegrateForces`;
    ///   - пилот-клиент: НЕ трогает тело (оно заморожено) — собирает кадр ввода за
    ///     свой физшаг, отправляет его серверу и ведёт собственный счётчик тика.
    ///     Предсказание движения на клиенте — шаг 2.4.
    /// Offline: сервер = локальный пир, поэтому работает только первая ветка.
    /// </summary>
    public override void _PhysicsProcess(double delta)
    {
        if (Net == null)
            return;

        if (HasAuthority)
        {
            // Шаг 3.5: принятые запросы выстрела исполняются здесь, а не в RPC
            // (риск №3 плана): `_PhysicsProcess` идёт после шага физики, поэтому
            // `IntersectRay` видит актуальные тела, а не позиции прошлого кадра.
            ProcessPendingFires();
            // Шаг 5.6: окно Approved гаснет само, если захват не начался
            // (п.16) — здесь, на авторитете, потому что клиентский AbortDock
            // был бы подделкой (гейт SetDockStateServerOnly его всё равно отверг бы).
            TickDockWindow();
            // Шаг 5.8/5.10: машина фаз захвата (Capturing→Latched→Docked) —
            // тоже только на авторитете, ПОСЛЕ шага физики (видит актуальные
            // позы, как ProcessPendingFires в шаге 3.5).
            TickDockPhases();
            // frame-plan шаг 3: правило захвата — тем же порядком, что фазы
            // докинга (после шага модели, на авторитете; частота — раз в
            // CaptureTuning.CheckEveryN физшагов внутри метода).
            TickCaptureRule();
            // Шаг 2.6, метрика (г): публикуем сетевой снимок раз в NetPublishEveryN
            // физшагов (30 Гц), а не каждый шаг (60 Гц). OnChange-репликация уходит
            // только когда свойство реально меняется, поэтому «реже пишем» = «реже
            // шлём». Симуляция тела при этом полные 60 Гц (в `_IntegrateForces`),
            // реже публикуется лишь сетевой слепок. См. `_netPublishPhase`.
            if (++_netPublishPhase % NetPublishEveryN == 0)
            {
                Net.NetPosition = GlobalPosition;
                // В буфере лежит Quaternion: Basis.Quat() (в C# GlobalRotation — это эйлеровы
                // углы Vector3, кватернион берётся из базиса глобального трансформ).
                Net.NetRotation = new Quaternion(GlobalTransform.Basis);
                Net.NetLinearVelocity = LinearVelocity;
                Net.NetAngularVelocity = AngularVelocity;
                Net.SimTick = (int)Engine.GetPhysicsFrames();

                // Шаг 2.4, §1.5: пилоту (не-авторитету, которому Synchronizer шлёт
                // реплики) мало знать, ГДЕ корабль, — надо знать, какой кадр ввода
                // сервер применил, чтобы получить это состояние. Иначе реконсиляция
                // не поймёт, какие кадры из буфера предсказания уже учтены эталоном,
                // а какие ещё надо переиграть (§1.6).
                // §2.6 фикс: `_PhysicsProcess` выполняется ДО `_IntegrateForces` этого
                // шага, значит позиция тела в снимке уже включает все кадры, снятые из
                // очереди на ПРЕДЫДУЩИХ шагах. Поэтому AppliedTick = `_serverAppliedTick`
                // (тик последнего реально проинтегрированного кадра), а не
                // `_lastAcceptedPilotTick` (тик последнего ПРИНЯТОГО, но ещё не применённого).
                // Пилот, глянув в свой след предсказаний на этом тике, сравнивает
                // однородные состояния — честная ошибка предсказания без упреждения.
                Net.AppliedTick = (int)_serverAppliedTick;
                Net.AppliedMask = _serverInput.Mask;
            }

            // Диагностика §2.6: dequeue vs sticky на сервере (временная).
            if (NetworkManager.IsHeadlessCli())
            {
                _metricsTimer += (float)delta;
                if (_metricsTimer >= 1f)
                {
                    _metricsTimer = 0f;
                    GD.Print($"[srv-metrics ship#{Net.ShipNetId}] dequeue={_dbgDequeue} sticky={_dbgSticky} " +
                             $"q={_serverQueue.Count} accepted={AcceptedInputCount} appliedTick={_serverAppliedTick}");
                }
            }

            // Шаг 2.3 (В14=B): репорт «позиция снизу вверх» удалён. Сервер — единственный
            // интегратор корабля, поэтому принимать чужую позицию ему не от кого: от
            // пилота приходят кадры ВВОДА (`RpcSubmitInput`), а не состояние (§3.5 п.8).
            return;
        }

        // Этап 6: сверка реплики флага ассиста у ЛЮБОГО не-авторитета (пилот —
        // предсказание, наблюдатель — дебаг-строка). OnChange-дельта меняет
        // свойство без callback — сравниваем байт, как страж сравнивает SimTick.
        PollAssistReplica();

        // Тип корабля (дебаг-пресеты корпуса): тот же опрос байта реплики. При
        // смене пересобираются масса тела, габариты/цвет корпуса и _baseFlight —
        // следующие предсказания пилота пойдут уже с новыми характеристиками.
        PollShipTypeReplica();

        // Пилот-клиент (не-авторитет, В14=B): каждый физшаг собирает кадр ввода,
        // кладёт его в кольцевой буфер, шлёт серверу И тут же интегрирует им своё
        // предсказание, а тело (вместе с картинкой и камерой — они дети тела)
        // ставит в это предсказание. Тело заморожено (`Freeze = Static`), поэтому
        // физически оно никуда не «летит» само: позиция — только то, что мы сами
        // в него запишем (проба `tests/probes/probe_frozen_move.tscn`: запись в
        // замороженное тело приживается). Пустая маска тоже отправляется (иначе
        // отпущенная клавиша залипнет на сервере, §1.2 плана).
        if (IsLocalPilot)
        {
            // Шаг 5.9 (В20): на dock-фазе (Capturing/Latched/Docked) клиент-пилот
            // НЕ отправляет кадры ввода и НЕ применяет локальное предсказание —
            // пружину ведёт сервер (prototype-plan:113), а предсказание пилота
            // разошлось бы с серверной истиной почти на 100% и рисовало бы
            // постоянные hardRollbacks (риск №5). Поза картинке придёт из
            // интерполяции батча в _Process (ветка наблюдателя). Возврат
            // предсказания — при снятии фазы (AbortDock): _hasPrediction=false
            // в _Process, предсказание стартует от актуальной реплики.
            if (Net != null && (DockState)Net.DockStateValue >= DockState.Capturing)
            {
                _clientDockPhaseActive = true;
                return;
            }

            // frame-plan §6.2 (шаг 6): фаза ЗАХВАТА — предсказание и реконсиляция
            // ВЫКЛЮЧЕНЫ, как на dock-фазе: переигрываемые кадры кольца не знают
            // v_frame родителя на своём тике (полной истории родителя нет), и
            // предсказание пилота на пузыре давало бы почти 100% расхождений и
            // постоянные hardRollbacks. Отличие от dock: кадры ВВОДА продолжают
            // собираться и отправляться — в пузыре (в отличие от пружины с
            // игнорируемой тягой, п.25) авторитет интегрирует тягу пилота в свою
            // причину; не предсказывается только ПОЗА (картинка ведётся
            // интерполяцией батча в _Process, ветка перехвата). Разрыв фазы
            // (вход/выход) — допустимый один жёсткий снап к снимку авторитета:
            // предсказание обнуляется и стартует от актуальной реплики.
            if (Net != null && Net.CaptureParentNetId != 0)
            {
                _clientCapturePhaseActive = true;
                InputFrame fcap = CollectInputFrame();
                if (_assistMuteFrames > 0)
                    _assistMuteFrames--;
                else
                    EnqueueInputForSend(fcap);
                FlushSendBuffer();
                // AdvanceFlight здесь НЕ вызывается (предсказания нет), а счётчик
                // кадров обязан расти: сервер отсек бы повторяющиеся тики гейтом
                // «только свежие кадры» (тот же инкремент, что делает AdvanceFlight).
                _tick++;
                return;
            }

            // Фиксированный шаг, как у сервера (`sd.GetStep()` = 1/60), а НЕ
            // переменный `delta`: предсказание обязано сходиться с серверной
            // истиной побитово при одинаковом вводе (риск №1 плана: при нулевом
            // лаге Prediction Error ≈ 0). Переменный delta (просадки кадров,
            // первый кадр) ломал бы детерминизм и давал бы метры ложной ошибки.
            float dt = FlightModel.FixedDt;
            InputFrame frame = CollectInputFrame();

            // Первое вхождение в пилотство: предсказание стартует из текущего
            // положения тела (истины, которую сервер уже нарисовал репликой).
            if (!_hasPrediction)
            {
                _predicted = FlightState.FromNode(this, LinearVelocity, AngularVelocity);
                _hasPrediction = true;
            }

            // Кадр в кольцо по индексу tick — чтобы переиграть его при расхождении.
            _pending[frame.Tick & (PendingRingSize - 1)] = frame;

            // Отправка ввода серверу. С швом `TestLatencyMs` (§2.6) кадр кладётся в
            // буфер исходящих и «дозревает» через N мс — так измеряется поведение
            // при RTT ≈ 2×N, не трогая ENet. При N=0 буфер пустеет в тот же шаг,
            // поведение идентично прежнему.
            //
            // Этап 6: сразу после T (переключение ассиста) новые кадры НЕ уходят
            // серверу несколько шагов: надёжный RPC переключения (канал 1) обязан
            // догнать очередь ненадёжных кадров ввода — иначе сервер применил бы
            // кадр с тиком ≥ T со СТАРЫМ флагом, а предсказание пилота шло бы уже
            // с новым, и сверка выдала бы ложный жёсткий откат. Кольцо и локальное
            // предсказание при этом двигаются как обычно.
            if (_assistMuteFrames > 0)
                _assistMuteFrames--;
            else
                EnqueueInputForSend(frame);
            // Отправить всё, что «дозрело» к этому физшагу (при N=0 — текущий кадр).
            FlushSendBuffer();

            // Локальное предсказание тем же `FlightModel`, что и сервер (один и тот
            // же код = сходимость при одинаковом вводе). `AdvanceFlight` двигает и
            // локальный счётчик `_tick` (кадр уже отправлен с прежним тиком).
            _predicted = AdvanceFlight(_predicted, frame, dt);

            // Запоминаем предсказанную позицию ПИЛОТА на тике этого кадра ввода.
            // Сервер, применив кадр с тиком T, вернёт снимок с AppliedTick = T и
            // позицией «после применения T» — ровно то состояние, что и наше
            // предсказание сразу после `AdvanceFlight(frame T)`. Сверка по этому
            // следу даёт истинную ошибку предсказания (§2.6 (а)), без упреждения.
            _predPosHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.Position;
            _predPosTick[frame.Tick & (PendingRingSize - 1)] = frame.Tick + 1; // +1 = «слот занят» (тик 0 не путать с пустым)
            _predVelHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.LinearVelocity;
            _predAngVelHistory[frame.Tick & (PendingRingSize - 1)] = _predicted.AngularVelocity;

            // Картинка и камера — за предсказанием.
            SetBodyToPrediction();
        }
    }

    /// <summary>Поставить тело (а с ним картинку и камеру — они дети тела) в
    /// предсказанное состояние пилота. Только позиция/ориентация: скорость у
    /// замороженного тела не используется, а писать её было бы ложью (истину
    /// считает сервер).</summary>
    private void SetBodyToPrediction()
    {
        GlobalTransform = new Transform3D(new Basis(_predicted.Rotation), _predicted.Position);
    }

    /// <summary>Не-авторитет: интерполяция картинки `ShipHull` по снимкам `NetState`
    /// (ring buffer, §2.5/§3.3). Тело заморожено, поэтому рисуем только картинку.
    /// Снимки приходят от Synchronizer'а (или через серверский репорт) в `NetState`;
    /// здесь мы складываем их в буфер по локальному времени и рисуем картинку на
    /// `InterpDelay` секунд в прошлом — между двумя ближайшими снимками.</summary>
    public override void _Process(double delta)
    {
        if (Net == null || HasAuthority)
            return; // авторитет рисует картинку телом, интерполяция не нужна

        double now = Time.GetTicksMsec() / 1000.0;

        // Метрики §2.6 в headless-стенде: Prediction Error и число жёстких откатов
        // (пилот), макс дельта hull (наблюдатель) — раз в секунду в stdout, чтобы
        // прогон `run-net-demo.sh` читался в логе так же, как дебаг-панель в окне.
        if (NetworkManager.IsHeadlessCli())
        {
            _metricsTimer += (float)delta;
            if (_metricsTimer >= 1f)
            {
                _metricsTimer = 0f;
                GD.Print($"[metrics ship#{Net.ShipNetId}] role={(IsLocalPilot ? "pilot" : "observer")} " +
                         $"predErr={PredictionError:F3} hardRollbacks={HardRollbackCount} " +
                         $"maxHullDelta={MaxHullDelta:F3} interpBuf={_interpBuf.Count} " +
                         $"interpDelay={_interpDelay:F3} snapPeriod={_snapPeriodEma:F3}");
                // Окно метрики (в): за секунду — максимум дельт hull за ЭТУ секунду,
                // дальше сбрасываем, чтобы разовый стартовый скачок не висел в
                // каждой следующей строке (сессия-макс не убывает). hardRollbacks
                // НЕ сбрасываем — это накопительный счётчик, по нему смотрим
                // среднюю частоту откатов за прогон.
                MaxHullDelta = 0f;
            }
        }

        // Пилот-клиент: картинка и камера идут за ПРЕДСКАЗАНИЕМ (тело ставит в
        // него `_PhysicsProcess`), поэтому интерполяция ему НЕ нужна — она бы
        // дралась с предсказанием (двигала бы hull назад, в прошлое). Вместо неё —
        // реконсиляция: как только пришла новая реплика эталона (`SimTick` сдвинулся),
        // сверить предсказание с эталоном и при необходимости откатить/переиграть.
        // Снимок идёт через буфер приёма (`TestLatencyMs`), поэтому сверка происходит
        // в момент «доставки», а не в момент, когда Synchronizer принёс реплику.
        if (IsLocalPilot)
        {
            // Страж «последний поставленный в буфер SimTick» — не `_lastReconcileTick`
            // (тот двигается в момент ДОСТАВКИ, а не постановки, иначе с задержкой
            // один и тот же SimTick попадал бы в буфер каждый кадр).
            if (Net.SimTick != _lastNetTickSeen)
            {
                _lastNetTickSeen = Net.SimTick;
                // Один сдвиг SimTick = один логический репликационный снимок этого
                // корабля (порог (г) §2.7 меряется по ним, а не по wire-пакетам ENet).
                NetworkManager.Instance?.CountReplicationRecv(Net.ShipNetId);
                EnqueueSnapshotFromNet(now);
            }

            // Шаг 5.9 (В20): на dock-фазе пилот-клиент НЕ реконсилирует и НЕ
            // предсказывает — позу ведёт серверная пружина. Картинка идёт по
            // интерполяции батча, как у наблюдателя (InterpDelay=0.1), а тело
            // следует за картинкой: чейз-камера — ребёнок тела, кабина обязана
            // ехать на изображении (строка 24 этого файла).
            if ((DockState)Net.DockStateValue >= DockState.Capturing)
            {
                _clientDockPhaseActive = true;
                DrainRecvBufferForObserver(now);
                InterpolateHull(now);
                var hullDock = GetNodeOrNull<Node3D>("ShipHull");
                if (hullDock != null)
                    GlobalTransform = hullDock.GlobalTransform;
                return;
            }
            if (_clientDockPhaseActive)
            {
                // Фаза снята (AbortDock / разрыв): предсказание обнуляем — оно
                // рестартует из актуальной реплики (проверка _hasPrediction в
                // _PhysicsProcess), иначе пилота откатит старый _predicted.
                _clientDockPhaseActive = false;
                _hasPrediction = false;
            }

            // frame-plan §6.2 (шаг 6): тот же режим для фазы ЗАХВАТА: предсказание
            // выключено (_PhysicsProcess шлёт только кадры ввода), позу ведёт
            // авторитет, картинка — интерполяцией батча, тело едет за картинкой
            // (чейз-камера — ребёнок тела). Смена реплики CaptureParentNetId
            // (вход в пузырь / выход) — разрыв фазы: одно обнуление предсказания,
            // старт от актуальной реплики. Унос картинки вдоль v_frame (§6.3) —
            // внутри InterpolateHull, он привязан к фазе и к ramp-состоянию.
            if (Net != null && Net.CaptureParentNetId != 0)
            {
                _clientCapturePhaseActive = true;
                DrainRecvBufferForObserver(now);
                InterpolateHull(now);
                var hullCap = GetNodeOrNull<Node3D>("ShipHull");
                if (hullCap != null)
                    GlobalTransform = hullCap.GlobalTransform;
                return;
            }
            if (_clientCapturePhaseActive)
            {
                _clientCapturePhaseActive = false;
                _hasPrediction = false;
            }
            DrainRecvBufferForPilot();
            return;
        }

        // Наблюдатель (не пилот): интерполяция картинки по снимкам.
        // Новый снимок (SimTick сдвинулся — Synchronizer принёс обновление)? Кладём
        // его в буфер приёма; в `_interpBuf` он попадёт в момент «доставки».
        if (Net.SimTick != _lastNetTickSeen)
        {
            _lastNetTickSeen = Net.SimTick;
            NetworkManager.Instance?.CountReplicationRecv(Net.ShipNetId);
            EnqueueSnapshotFromNet(now);
        }
        DrainRecvBufferForObserver(now);

        InterpolateHull(now);

        // Метрика критерия (в) §2.6: максимальный скачок hull наблюдателя между
        // соседними кадрами. Если картинка «резинит» (дельта > 0.15 м) — интерполяция
        // не справляется с лагом/потерями.
        //
        // Мерим ТОЛЬКО установившийся полёт, не стартовый снимок: пока буфер
        // интерполяции не наполнился хотя бы двумя снимками, hull делает ОДИН
        // скачок из точки спавна в первую «прогретую» позицию сервера (на стенде
        // при RTT≈200 мс это ~10 м). Это не «резинг», а появление корабля там, где
        // его видит сервер; критерий (в) про плавность В ПОЛЁТЕ. Поэтому счёт
        // дельт включаем, когда `_interpBuf.Count >= 2` (интерполяция уже работает).
        // Значение за секунду — окно, сбрасываемое при печати метрик (см.
        // `_metricsTimer`), чтобы разовый стартовый скачок не «засветился» на всех
        // последующих секундах (сессия-макс никогда не убывает).
        var hullNode = GetNodeOrNull<Node3D>("ShipHull");
        if (hullNode != null && _interpBuf.Count >= 2)
        {
            var hp = hullNode.GlobalPosition;
            if (_haveHullPos)
            {
                float d = hp.DistanceTo(_lastHullPos);
                if (d > MaxHullDelta)
                    MaxHullDelta = d;
            }
            _lastHullPos = hp;
            _haveHullPos = true;
        }
    }

    /// <summary>Дренаж буфера приёма для пилота: каждый «дозревший» снимок — кадр
    /// реконсиляции (откат/переигровка). Снимки, пришедшие задним числом (несколько
    /// дозрели за кадр), обрабатываются по порядку SimTick.</summary>
    private void DrainRecvBufferForPilot()
    {
        double nowSec = Time.GetTicksMsec() / 1000.0;
        while (_recvBuf.Count > 0 && _recvBuf.Peek().DueSec <= nowSec)
        {
            var s = _recvBuf.Dequeue().Snap;
            if (s.SimTick == _lastReconcileTick)
                continue; // дубликат того же тика — сверяться нечем
            _lastReconcileTick = s.SimTick;
            ReconcileWith(StateFromSnap(s), s.AppliedTick);
        }
    }

    /// <summary>Дренаж буфера приёма для наблюдателя: «дозревшие» снимки попадают в
    /// буфер интерполяции по местному времени приёма; старьё за окном режется.</summary>
    private void DrainRecvBufferForObserver(double now)
    {
        while (_recvBuf.Count > 0 && _recvBuf.Peek().DueSec <= now)
        {
            var entry = _recvBuf.Dequeue();
            var s = entry.Snap;
            // §3.9 (ключ): штампим снимок его ЖУРНАЛЬНЫМ временем доставки
            // (`DueSec`), а не текущим `now`. Если после сетевой заминки в одном
            // кадре «дозревает» пачка снимков, при штампе `now` все они получают
            // одну и ту же отметку → вырожденные сегменты (нулевой интервал) и
            // hull прыгает на весь путь пачки за кадр. DueSec сохраняет ровный
            // шаг приёма, каким бы ни был джиттер доставки.
            double stamp = entry.DueSec;
            _interpBuf.Add(new Snapshot(stamp, s.Pos, s.Rot));
            _adaptPending = true; // пересчёт адаптивной задержки ниже, один раз за кадр
        }

        // §3.9: адаптивная задержка — один пересчёт за кадр (не за снимок!),
        // иначе пачка «дозревших» снимков даёт несколько шагов задержки в одном
        // кадре и сама рисует скачок (тот же артефакт, что лечим). Замеряем
        // ровный шаг между отметками снимков в буфере интерполяции.
        AdaptInterpDelay(now);

        // Окно буфера — достаточно глубокое, чтобы накрывать адаптивную
        // задержку: render = now - _interpDelay должен остаться внутри окна.
        // Держим минимум 2 снимка (иначе нечего интерполировать — «ждать»).
        double window = System.Math.Max(InterpDelay, _interpDelay) * 2.5;
        while (_interpBuf.Count > 2 && now - _interpBuf[0].Time > window)
            _interpBuf.RemoveAt(0);
    }

    // Осталось ли что пересчитать за этот кадр (выставляется при дренаже).
    private bool _adaptPending;
    // Первый валидный замер периода — ставим задержку сразу в цель (без рампы),
    // чтобы наблюдатель не оставался голодным несколько кадров на старте.
    private bool _bootstrapDone;
    private int _periodSamples;            // сколько оценок периода уже сделано
    /// <summary>§3.9: пересчёт адаптивной задержки интерполяции (один раз за
    /// кадр). Период меряем по шагу между отметками снимков (`DueSec`), с
    /// пик-холдом по худшему недавнему зазору; задержка = factor × пик периода
    /// (2 снимка в окне), плавно сходится к цели с быстрым ростом и медленным
    /// спадом. Критерий (в) §2.6: render=now-delay всегда внутри окна → нет
    /// скачков hull на наблюдателе при ≥3 кораблях.</summary>
    private void AdaptInterpDelay(double now)
    {
        if (!_adaptPending) return;
        _adaptPending = false;

        // Период — шаг между двумя последними добавленными снимками (по DueSec,
        // ровный). Считаем только валидный положительный шаг.
        if (_interpBuf.Count >= 2)
        {
            double period = _interpBuf[^1].Time - _interpBuf[^2].Time;
            if (period > 0.0 && period < SnapPeriodMax)
            {
                if (_snapPeriodEma < 0.0) _snapPeriodEma = period;
                else _snapPeriodEma = _snapPeriodEma * 0.6 + period * 0.4;

                // Пик-холд почти «липкий»: держим худший недавний зазор очень
                // долго (полупериод спада ~десятки секунд на частоте снимков).
                // Иначе единичный быстрый зазор роняет задержку, render=now-delay
                // снова выезжает за самый свежий снимок → мёрзнет/прыгает —
                // самоподдерживающаяся осцилляция. Держим запас по голоданию.
                if (period > _snapGapPeak || _snapGapPeak < 0.0)
                    _snapGapPeak = period;
                else
                    _snapGapPeak = _snapGapPeak * 0.995 + period * 0.005;

                double worst = System.Math.Max(_snapPeriodEma, _snapGapPeak);
                double target = Mathf.Clamp(
                    (float)(AdaptiveDelayFactor * worst),
                    (float)InterpDelay, (float)AdaptiveDelayMax);

                // Первый замер периода — bootstrap: ставим задержку прямо в цель
                // (без рампы). Рампа нужна только для плавного слежения за
                // ДЖИТТЕРОМ в установившемся режиме; на старте буфер ещё не
                // заполнен и картинка не может «прыгнуть», зато рампа оставляет
                // наблюдателя голодным на несколько кадров (скачок hull при
                // догоняющем render=now-delay). NetTest-шов сюда не заходит
                // (TestPushSnapshot минует дренаж), его математика не тронута.
                if (!_bootstrapDone && ++_periodSamples >= 3)
                {
                    _interpDelay = target;
                    _bootstrapDone = true;
                }
                else if (_bootstrapDone)
                {
                    // ГИСТЕРЕЗИС с мёртвой зоной: держим задержку ПОСТОЯННОЙ против
                    // дрожания EMA. Собственный дрейф задержки сам рисует скачок
                    // hull на v×Δdelay (при 50 м/с Δdelay=0,02 → скачок 1 м, т.е.
                    // лечение вносит ровно тот артефакт, что лечит). Двигаем
                    // задержку только за пределами широкой мёртвой зоны: вверх —
                    // если реальное голодание выросло (недобрли окно), вниз — если
                    // явно переборщили. Внутри зоны — не трогаем вовсе.
                    double delta = target - _interpDelay;
                    if (delta > DelayRampUpBand)
                    {
                        double step = System.Math.Min(delta, MaxDelayStepUpPerFrame);
                        _interpDelay += step;
                    }
                    else if (delta < -DelayReleaseBand)
                    {
                        double step = System.Math.Max(delta, -MaxDelayStepPerSnap);
                        _interpDelay += step;
                    }
                }
            }
        }
    }

    /// <summary>Ядро реконсиляции против переданного эталона (`server`) и тика,
    /// который сервер применил (`appliedTick`). Вынесено из `Reconcile`, чтобы
    /// автотесты п.9-11 могли подать эталон напрямую, без настоящего
    /// `NetState`/Synchronizer (шов `TestReconcile`).</summary>
    private void ReconcileWith(FlightState server, uint appliedTick)
    {
        // Предсказания ещё нет (первая реплика раньше первого физшага) — принять
        // эталон как истину и стартовать от него.
        if (!_hasPrediction)
        {
            _predicted = server;
            _hasPrediction = true;
            SetBodyToPrediction();
            return;
        }

        // ЧЕСТНАЯ метрика предсказания (§2.6 критерий (а), < 0.3 м). Серверный снимок
        // соответствует состоянию «после применения кадра с AppliedTick». Сравнивать
        // его с ТЕКУЩИМ предсказанием пилота (которое ушло вперёд на скорость×RTT) —
        // значит мерить «упреждение», а не ошибку: при 100 мс RTT и десятках м/с это
        // сотни метров ложной ошибки и жёсткий откат на каждом снимке. Поэтому сверяем
        // эталон с тем предсказанием, что пилот ДЕРЖАЛ ровно на AppliedTick (след
        // _predPosHistory). Если такого следа нет (тесты п.9-11 сеют предсказание
        // напрямую, без истории) — откатываемся к прежней мере |текущее − эталон|.
        float err;
        bool hit = TryGetPredictedAt(appliedTick, out Vector3 predAtTick);
        if (hit)
            err = predAtTick.DistanceTo(server.Position);
        else
            err = _predicted.Position.DistanceTo(server.Position);
        PredictionError = err;

        // Диагностика §2.6 (временная): пишем только крупные расхождения (>0.3),
        // чтобы поймать природу пиков в установившемся режиме.
        if (NetworkManager.IsHeadlessCli() && err > 0.3f && _dbgReconcile++ < 30)
            GD.Print($"[recon ship#{Net.ShipNetId}] appliedTick={appliedTick} pilotTick={_tick} " +
                     $"hit={hit} err={err:F3} serverPos={server.Position} curPred={_predicted.Position} " +
                     $"predAtTick={(hit ? predAtTick : Vector3.Zero)}");

        if (err > ReconcileThreshold)
        {
            // Жёсткий откат к эталону + переигровка неподтверждённых кадров.
            _predicted = server;
            ReplayInputsAfter(appliedTick);
            // Метрика критерия (б) §2.6: считаем каждый жёсткий откат-телепорт.
            HardRollbackCount++;
        }
        else
        {
            // Мягкая синхронизация: позиция остаётся предсказанной (она уже в
            // пределах порога от эталона). Скорости снимка относятся к AppliedTick,
            // а _predicted ушёл вперёд на RTT/2 кадров — подставлять серверную
            // скорость как АБСОЛЮТ нельзя: это выбрасывает прирост скорости за эти
            // кадры, пилот отстаёт, ошибка нарастает до порога и каждый снимок даёт
            // жёсткий откат (зубчатый hardRollbacks). Берём РАЗНОСТЬ скоростей на
            // AppliedTick (сервер минус пилот) и докладываем в текущую предсказанную
            // скорость: при детерминизме разница ≈ 0 (зубцов нет), реальный дрейф
            // корректируется. Если следа скорости на AppliedTick нет (тесты п.9-11
            // сеют только позицию) — падаемся на абсолют, как раньше.
            if (hit && TryGetPredictedVelAt(appliedTick, out Vector3 predVelAtTick, out Vector3 predAngAtTick))
            {
                _predicted.LinearVelocity += server.LinearVelocity - predVelAtTick;
                _predicted.AngularVelocity += server.AngularVelocity - predAngAtTick;
            }
            else
            {
                _predicted.LinearVelocity = server.LinearVelocity;
                _predicted.AngularVelocity = server.AngularVelocity;
            }
            // Кадры не переигрывались — сбрасываем счётчик, иначе он остался бы
            // от прошлой жёсткой сверки и автотест п.10 прочитал бы мусор.
            LastReplayedCount = 0;
        }

        SetBodyToPrediction();
    }

    /// <summary>Найти предсказанную позицию пилота на тике `tick` (след истории).
    /// В кольце хранится `tick+1` как признак валидности (0 = слот пуст), чтобы
    /// тик 0 не совпадал с пустым слотом. Возвращает false, если следа нет (кольцо
    /// перемололось или предсказание на этом тике не строилось) — тогда вызывающий
    /// мерит ошибку по-старому.</summary>
    private bool TryGetPredictedAt(uint tick, out Vector3 pos)
    {
        int idx = (int)(tick & (uint)(PendingRingSize - 1));
        if (_predPosTick[idx] == tick + 1)
        {
            pos = _predPosHistory[idx];
            return true;
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>То же, что `TryGetPredictedAt`, но возвращает линейную и угловую
    /// скорости предсказания пилота на тике `tick` (след `_predVelHistory` /
    /// `_predAngVelHistory`, валидность по `_predPosTick`). Нужен для мягкой
    /// синхронизации по РАЗНОСТИ скоростей.</summary>
    private bool TryGetPredictedVelAt(uint tick, out Vector3 vel, out Vector3 angVel)
    {
        int idx = (int)(tick & (uint)(PendingRingSize - 1));
        if (_predPosTick[idx] == tick + 1)
        {
            vel = _predVelHistory[idx];
            angVel = _predAngVelHistory[idx];
            return true;
        }
        vel = Vector3.Zero;
        angVel = Vector3.Zero;
        return false;
    }

    /// <summary>Собрать `FlightState` из принятой реплики `NetState` (эталона).</summary>
    private FlightState SnapshotToFlightState() => new()
    {
        Position = Net.NetPosition,
        Rotation = Net.NetRotation,
        LinearVelocity = Net.NetLinearVelocity,
        AngularVelocity = Net.NetAngularVelocity,
    };

    /// <summary>Переиграть из кольца все кадры с `Tick > appliedTick` и `Tick <
    /// _tick` (последний отправленный кадр = `_tick - 1`). Каждый переигранный кадр
    /// двигает `_predicted` тем же `StepPrediction`, что и обычный шаг. Пустые или
    /// перезаписанные кольцом слоты пропускаются (`f.Tick != t`) — потерянный на
    /// unreliable-канале кадр = дыра, её нечем переиграть, и это нормально (§1.4).</summary>
    private void ReplayInputsAfter(uint appliedTick)
    {
        LastReplayedCount = 0;
        // `_tick` после шага = следующий тик; последний отправленный кадр = _tick-1.
        for (uint t = appliedTick + 1; t < _tick; t++)
        {
            InputFrame f = _pending[t & (PendingRingSize - 1)];
            if (f.Tick != t)
                continue; // слот пуст или перезаписан кольцом — дыра, пропускаем
            _predicted = StepPrediction(_predicted, f, FlightModel.FixedDt);
            // ОБЯЗАТЕЛЬНО обновляем след истории на переигранном тике: он теперь
            // отражает ИСПРАВЛЕННОЕ предсказание (после отката к эталону). Без этого
            // кольцо хранит СТАРОЕ (дооткатное) предсказание, и следующий снимок
            // сверяется с ним → ложная большая ошибка → ещё один жёсткий откат →
            // автоколебание `hardRollbacks` (зубцы ~10/сек при RTT≈0).
            int idx = (int)(t & (uint)(PendingRingSize - 1));
            _predPosHistory[idx] = _predicted.Position;
            _predPosTick[idx] = t + 1;
            _predVelHistory[idx] = _predicted.LinearVelocity;
            _predAngVelHistory[idx] = _predicted.AngularVelocity;
            LastReplayedCount++;
        }
    }

    /// <summary>Поставить `ShipHull` в интерполированное глобальное состояние на
    /// моменте `now - InterpDelay`. Вызывается и из _Process, и из автотеста
    /// (шов без UDP). Отдельный метод — чтобы тест мог задать буфер и проверить
    /// результат без гонки по времени.</summary>
    public void InterpolateHull(double now)
    {
        var hull = GetNodeOrNull<Node3D>("ShipHull");
        if (hull == null || _interpBuf.Count == 0)
            return;

        // §6.3 (шаг 7): сдвиг точки съёмки для захваченного корабля. Поза
        // интерполируется как обычно (между снимков, без экстраполяции), но к
        // результату добавляется унос v_frame × _interpDelay: картинка A
        // ставится туда, где пилот родителя B видит A сейчас (B у него идёт
        // «живым» предсказанием, а снимки A — на delay в прошлом). Сдвиг
        // slew-лимитирован (_capShiftCur разгоняется/гасится не быстрее
        // CapShiftSlewPerSec), поэтому включение фазы не рисует скачок.
        Vector3 carry = CaptureCarryShift(now);

        // Интерполяция выключена (кнопка `Toggle Interpolation`): картинка идёт
        // прямо в ПОСЛЕДНИЙ принятый снимок, без сдвига на InterpDelay и без
        // смешивания. Дискретно, но совпадает с сетью по времени (автотест п.13).
        // Унос при этом НЕ применяется: у выключенной интерполяции нет и
        // «задержки съёмки», которую он компенсирует.
        if (!_interpolationEnabled)
        {
            ApplyHull(hull, _interpBuf[^1]);
            return;
        }

        double render = now - _interpDelay;

        // Один снимок или кадр раньше самого старого — рисуем самый старый.
        if (_interpBuf.Count == 1 || render <= _interpBuf[0].Time)
        {
            ApplyHullCarried(hull, _interpBuf[0], carry);
            return;
        }

        // Кадр позже самого свежего — не экстраполируем (иначе картинка обгоняет
        // сеть и дёргается при потере пакета): рисуем самый свежий.
        if (render >= _interpBuf[^1].Time)
        {
            ApplyHullCarried(hull, _interpBuf[^1], carry);
            return;
        }

        // Ищем два снимка, между которыми попал render-момент.
        for (int i = 0; i < _interpBuf.Count - 1; i++)
        {
            Snapshot a = _interpBuf[i], b = _interpBuf[i + 1];
            if (render >= a.Time && render <= b.Time)
            {
                double span = b.Time - a.Time;
                double alpha = span > 1e-6 ? (render - a.Time) / span : 0.0;
                var pos = a.Position.Lerp(b.Position, (float)alpha);
                var rot = a.Rotation.Slerp(b.Rotation, (float)alpha);
                hull.GlobalTransform = new Transform3D(new Basis(rot), pos + carry);
                return;
            }
        }
    }

}
