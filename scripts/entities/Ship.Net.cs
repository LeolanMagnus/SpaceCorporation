using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    /// <summary>Задержка интерполяции, сек. Больше 2×ReplicationInterval (1/30),
    /// чтобы кадр всегда попадал внутрь окна двух снимков. Это БАЗА/минимум:
    /// реальная задержка адаптивна (§3.9, долг §2.7) — см. `_interpDelay`.</summary>
    private const double InterpDelay = 0.1;

    // §3.9 (долг §2.7): адаптивная задержка интерполяции. При ≥3 кораблях
    // пропускная способность на корабль падает: период приёма снимков растёт до
    // ~0,25 с (на замере LATENCY=100 3 игрока). Фиксированный `InterpDelay = 0,1`
    // становится МЕНЬШЕ периода снимков → render-момент (`now - delay`) выезжает
    // за самый свежий снимок: картинка мёрзнет в последнем снимке, а когда приходит
    // следующий — прыгает назад в сегмент с α≈0,6 (скачок 1,0–1,9 м, критерий (в)
    // §2.6 провален). Лечение: задержка = 2×измеренного периода снимков (кадр
    // всегда внутри окна), буфер держим достаточно глубоким, чтобы окно накрывало
    // задержку. Период меряем EMA по моментам постановки снимков в `_interpBuf`.
    private double _interpDelay = InterpDelay;
    private double _snapPeriodEma = -1.0;   // <0 — период ещё не измерен
    // Пик-холд межприходного зазора: render-цель должна накрывать ХУДШИЙ недавний
    // зазор ( jitter/потеря пакета), иначе в «голодный» промежуток render=now-delay
    // выезжает за самый свежий снимок → картинка мёрзнет, а со следующим снимком
    // прыгает на весь пройденный за зазор путь (скачок больше шагового). Пик
    // держим с медленным спадом, чтобы единичный выброс не задрал задержку навсегда.
    private double _snapGapPeak = -1.0;
    private const double SnapPeriodMin = 1.0 / 30.0;  // физ. потолок частоты снимков
    private const double SnapPeriodMax = 1.0;         // защитный потолок (потеря пакетов)
    private const double AdaptiveDelayFactor = 2.0;   // delay = factor × период
    private const double AdaptiveDelayMax = 0.6;      // верхняя граница задержки
    // Максимум, на который задержка сдвигается за один принятый снимок: скачок
    // задержки сдвигает render-цель и сам даёт скачок hull (v×Δdelay), поэтому
    // адаптация идёт плавно, а не рывком (риск: лечение вносит новый артефакт).
    private const double MaxDelayStepPerSnap = 0.02;  // 20 мс за кадр (спад)
    private const double MaxDelayStepUpPerFrame = 0.06; // 60 мс за кадр (рост — лечить голодание сразу)
    // Мёртвая зона гистерезиса: пока |target-current| в этих пределах — задержку
    // не трогаем (дрожание EMA не двигает картинку). Вверх реагируем на 50 мс
    // раньше, чем вниз: голодание лечим быстро, перерасход снимаем осторожно.
    private const double DelayRampUpBand = 0.05;
    private const double DelayReleaseBand = 0.15;

    /// <summary>Текущая (возможно адаптивная) задержка интерполяции — для панели
    /// и автотеста (§3.9).</summary>
    public double InterpDelaySec => _interpDelay;

    /// <summary>Измеренный период приёма снимков, сек (<0 — ещё не измерялся).</summary>
    public double SnapPeriodSec => _snapPeriodEma;


    /// <summary>Включена ли интерполяция картинки не-авторитета. Выключена — hull
    /// ставится прямо в ПОСЛЕДНИЙ принятый снимок (без сдвига на `InterpDelay` и без
    /// смешивания двух): картинка прыгает дискретно, зато совпадает с сетью по времени.
    /// Переключается кнопкой `Toggle Interpolation` в дебаг-панели (stage2-plan §2.5,
    /// автотест п.13).</summary>
    private bool _interpolationEnabled = true;

    /// <summary>Состояние интерполяции (для панели и автотеста).</summary>
    public bool InterpolationEnabled => _interpolationEnabled;

    /// <summary>Включить/выключить интерполяцию картинки не-авторитета.</summary>
    public void SetInterpolation(bool enabled) => _interpolationEnabled = enabled;

    /// <summary>Переключить интерполяцию (для кнопки дебаг-панели).</summary>
    public void ToggleInterpolation() => _interpolationEnabled = !_interpolationEnabled;

    /// <summary>Шаг 4.9: переключить god mode. Идёт в единственный серверный путь
    /// записи (`ShipHealthState.SetGodModeServerOnly`), который сам гейтится по
    /// авторитету. Кнопка регистрируется только серверному процессу (см. _Ready),
    /// RPC нет вовсе — с клиента запрос не принимается (п.23/п.24).</summary>
    public void ToggleGodMode()
    {
        if (GetNodeOrNull<ShipHealthState>("ShipHealthState") is { } h)
            h.SetGodModeServerOnly(!h.GodMode);
    }

    /// <summary>Буфер снимков (кольцевой по времени): (момент приёма, позиция,
    /// поворот). Храним локальное время приёма, потому что снимки приходят
    /// неравномерно, а интерполировать надо по местным часам клиента.</summary>
    private readonly List<Snapshot> _interpBuf = new();
    private int _lastNetTickSeen = -1;

    private readonly struct Snapshot
    {
        public readonly double Time;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;
        public Snapshot(double time, Vector3 position, Quaternion rotation)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
        }
    }

    // ------------------------------------------------------------------
    // Пилотирование 6DoF (стадия 2, шаг 2.2)
    // ------------------------------------------------------------------

    /// <summary>Кадр ввода, собранный для текущего физического шага.</summary>
    private InputFrame _localInput;

    // Блок A (eva-plan, п.А1): сбор клавиш и курсора вынесен в `PilotInput`, чтобы
    // скафандр (Блок C) ел ТОТ ЖЕ кадр ввода, что и корабль (ТЗ п.9 «управление
    // идентично пилотированию»). Сюда переехали поля `_lookDelta` (аддитивный
    // пиксельный канал — его кормит только тестовый шов), `_lastMousePos` (луч
    // прицела для выстрела), `_pilotCursorPx`/`_hasPilotCursor` (прицел-курсор
    // режима 2.0). Инвариант: байт-в-байт те же числа на входе `FlightModel.Step`.
    private readonly PilotInput _pilotInput = new();

    /// <summary>Collector пилотского ввода этого корабля (Блок A). Нужен
    /// снаружи: HUD прицела и скафандр берут из него же позицию курсора
    /// (п.6 eva-plan: один источник луча «пиксель → мир», двух копий быть не
    /// должно).</summary>
    public PilotInput Pilot => _pilotInput;

    /// <summary>Счётчик физических шагов этого процесса = номер тика в кадрах
    /// ввода. Шаг 2.3 добавит серверную сверку тиков, здесь счётчик локальный.</summary>
    private uint _tick;

    // ------------------------------------------------------------------
    // Предсказание и реконсиляция на пилоте (стадия 2, шаг 2.4, §1.6)
    // ------------------------------------------------------------------
    //
    // Пилот-клиент заморожен (не имеет права двигать истину), но обязан вести
    // КАРТИНКУ и камеру по СОБСТВЕННОМУ предсказанию, иначе управление ощущается
    // с задержкой в один RTT. Схема (классический client-side prediction):
    //   (а) каждый физшаг пилот шлёт кадр ввода серверу И тут же интегрирует им
    //       своё предсказанное `FlightState`, а тело ставит в это предсказание
    //       (проба `tests/probes/probe_frozen_move.tscn`: запись в замороженное
    //       тело приживается, а камера и hull — дети тела, поэтому следуют за ним);
    //   (б) кадр кладётся в кольцевой буфер по индексу `tick & 255`, чтобы потом
    //       переиграть его, если серверный эталон покажет расхождение;
    //   (в) когда приходит реплика `NetState` (эталон с `AppliedTick`), пилот
    //       сверяет предсказание с эталоном: расхождение больше порога — откат к
    //       эталону и переигровка кадров с `Tick > AppliedTick`; меньше — только
    //       подтянуть скорости (soft-sync), без телепорта, чтобы не дёргалось.
    //
    // Эталон приходит НЕ отдельным RPC, а теми же репликами `NetState`, что
    // Synchronizer шлёт всем не-авторитетам (§1.5): пилот — не авторитет (В14=B),
    // поэтому получает их наравне с наблюдателями.

    /// <summary>Порог ЖЁСТКОГО отката, метры. Расхождение меньше — считаем шумом
    /// интегрирования/сети и не телепортируем картинку (soft-sync: подтягиваем
    /// только скорости). Порог ПРИРАВНЕН к критерию готовности §2.6 (а)
    /// `predErr < 0,3 м`: рассогласование в пределах допуска предсказания — это
    /// штатная работа, оно гасится мягкой синхронизацией скоростей и НЕ должно
    /// порождать телепорт. Жёсткий откат — только за подлинные расхождения сверх
    /// допуска (стартовый транзиент ~2 м, тесты п.9=5 м / п.11=1 м). Так критерии
    /// (а) и (б) связаны напрямую: пока честная ошибка держится ниже 0,3 м,
    /// жёстких откатов нет вовсе (кроме старта), и `hardRollbacks ≤ 1/сек`
    /// выполняется автоматически. Тесты п.9-11 задают пороги 0.04 (soft) и 1/5 м
    /// (hard) — при 0.3 они проходят как раньше.</summary>
    private const float ReconcileThreshold = 0.3f;

    /// <summary>Размер кольцевого буфера неподтверждённых вводов. 256 кадров =
    /// ~4.3 с на 60 Гц — с запасом перекрывает любой разумный RTT+джиттер.</summary>
    private const int PendingRingSize = 256;

    /// <summary>Кольцевой буфер вводов пилота: индекс = `tick & (PendingRingSize-1)`.
    /// Храним и сам кадр, и его тик, чтобы при переигровке отличить «свой» кадр от
    /// мусора после перезаписи кольца.</summary>
    private readonly InputFrame[] _pending = new InputFrame[PendingRingSize];

    /// <summary>История предсказанных позиций пилота по тикам: индекс = `tick &
    /// (PendingRingSize-1)`, рядом хранится сам тик, чтобы отличить «свою» запись
    /// от мусора после перезаписи кольца. Нужна для ЧЕСТНОЙ метрики предсказания
    /// (§2.6 критерий (а)): сверяя реплику с эталоном, сравниваем серверную позицию
    /// (она соответствует `AppliedTick`) с тем предсказанием, что пилот держал ровно
    /// на `AppliedTick`, а НЕ с текущим предсказанием — иначе ошибка завышается на
    /// «упреждение» скорость×RTT и даёт ложные жёсткие откаты каждый снимок.</summary>
    private readonly Vector3[] _predPosHistory = new Vector3[PendingRingSize];
    private readonly uint[] _predPosTick = new uint[PendingRingSize];

    /// <summary>Скорость предсказания пилота на том же тике, что и `_predPosHistory`
    /// (та же валидность по `_predPosTick`). Нужна для мягкой синхронизации: серверный
    /// снимок несёт скорость на `AppliedTick`, а `_predicted` ушёл вперёд на RTT/2
    /// кадров. Подставлять серверную скорость как АБСОЛЮТ — значит выбросить прирост
    /// скорости за эти кадры: пилот отстаёт, ошибка растает до порога и каждый снимок
    /// даёт жёсткий откат (зубчатый `hardRollbacks`). Поэтому в мягкой сверке берём
    /// РАЗНОСТЬ скоростей (сервер на AppliedTick минус пилот на AppliedTick) и
    /// докладываем её в текущую предсказанную скорость: при детерминизме разница ≈ 0
    /// (зубцов нет), а реальный дрейф (залипание кадра) корректируется.</summary>
    private readonly Vector3[] _predVelHistory = new Vector3[PendingRingSize];
    private readonly Vector3[] _predAngVelHistory = new Vector3[PendingRingSize];

    /// <summary>Предсказанное состояние пилота. Для offline/сервера не используется
    /// (истина ведётся телом через `_IntegrateForces`); значимо только пилоту-клиенту.</summary>
    private FlightState _predicted;

    /// <summary>Есть ли уже валидное предсказание (инициализировано из тела при
    /// входе пилота). До первого шага предсказывать нечего.</summary>
    private bool _hasPrediction;
    /// <summary>Последний `SimTick` эталона, по которому уже сверялись: чтобы не
    /// реконсилироваться по одной и той же реплике многократно.</summary>
    private int _lastReconcileTick = -1;

    /// <summary>Счётчик физшагов для гейта публикации `NetState` (шаг 2.6, метрика
    /// (г) §2.7). Сервер пишет реплику не каждый физшаг (60 Гц), а раз в 2 шага
    /// (30 Гц) — ровно как задумано в §2.9 («30 снимков/с репликации»). Причина:
    /// в Godot 4.7 OnChange-репликация синхронизатора НЕ уважает `replication_interval`
    /// (проверено: interval=10.0 в сцене, а доставка всё равно 60/с — дельты уходят
    /// каждый физшаг, пока меняется свойство). Единственный способ задать частоту
    /// снимков — менять сами свойства реже. Симуляция тела при этом идёт полные
    /// 60 Гц (через `_IntegrateForces`), реже публикуется только сетевой снимок.
    /// Реконсиляция не ломается: `SimTick`/`AppliedTick` в снимке всегда указывают
    /// точный физшаг, к которому он относится, а буфер ввода пилота хранит все
    /// 60-герцовые кадры — переигровка по `Tick > AppliedTick` остаётся точной.</summary>
    private int _netPublishPhase;
    private const int NetPublishEveryN = 2;   // 60 Гц физики / 2 = 30 снимков/с

    /// <summary>Расхождение предсказания с последней принятой репликой, метры —
    /// для дебаг-панели (`Prediction Error`, §1.6).</summary>
    public float PredictionError { get; private set; }

    /// <summary>Счётчик переигранных кадров на последней реконсиляции — для assert'а
    /// п.9 автотеста (переигран ровно N кадров).</summary>
    public int LastReplayedCount { get; private set; }

    // --------------------------------------------------------------- искусственная
    // задержка (шов `TestLatencyMs`, §2.6). Лаг вносится на уровне отправки/приёма
    // на клиенте, НЕ трогая ENet: кадры ввода пилота и снимки `NetState` кладутся в
    // буфер и «дозревают» через `TestLatencyMs` миллисекунд. Так при RTT ≈ 2×latency
    // измеряют `Prediction Error`, число откатов и дельту hull, не правя транспорт.
    // При `TestLatencyMs == 0` (дефолт) буферы пустеют в тот же кадр — поведение
    // идентично прежнему, автотесты п.9-13 не деградируют.

    /// <summary>Буфер исходящих кадров ввода пилота: (момент «доставки», кадр).</summary>
    private readonly Queue<(long DueMs, InputFrame Frame)> _sendBuf = new();

    /// <summary>Буфер входящих снимков `NetState`: (момент «доставки», снимок). Время
    /// в секундах (та же шкала, что `Time.GetTicksMsec()/1000.0` в `_Process`).</summary>
    private readonly Queue<(double DueSec, NetSnap Snap)> _recvBuf = new();

    /// <summary>Копия снимка `NetState` для буфера приёма: `NetState` перезаписывается
    /// репликой каждый кадр, поэтому задерживаемое состояние надо скопировать.</summary>
    private struct NetSnap
    {
        public int SimTick;
        public uint AppliedTick;
        public Vector3 Pos, Lin, Ang;
        public Quaternion Rot;
    }

    /// <summary>Число ЖЁСТКИХ откатов-телепортов (err > порога) за время жизни корабля
    /// — метрика критерия (б) §2.6 (откатов ≤ 1/сек при LATENCY=100).</summary>
    public int HardRollbackCount { get; private set; }

    /// <summary>Максимальная дельта hull наблюдателя между соседними кадрами, метры —
    /// метрика критерия (в) §2.6 (чужой корабль: дельта < 0.15 м, «не резинит»).</summary>
    public float MaxHullDelta { get; private set; }
    private Vector3 _lastHullPos;
    private bool _haveHullPos;

    /// <summary>Ожидаемое (после T) состояние ассиста на сервере; null = нет
    /// неподтверждённого переключения.</summary>
    private bool? _assistExpected;
    /// <summary>Тик, на котором локально применено неподтверждённое переключение.</summary>
    private uint _assistAppliedTick;
    /// <summary>Сколько шагов ещё НЕ класть кадры в очередь отправки после T
    /// (см. RPC-комментарий: RPC обязан догнать очередь кадров).</summary>
    private int _assistMuteFrames;
    private const int AssistMuteFrames = 3;
    /// <summary>Флаг проинициализирован репликой (первое чтение/событие).</summary>
    private bool _assistSeenInit;

    /// <summary>На каком флаге ассиста моделировать (предсказывать) шаг с тиком t:
    /// ожидаемое состояние, если переключение ещё не подтверждено и t сразу после
    /// него; иначе — локальный флаг (он = подтверждённая реплика).</summary>
    private bool AssistForTick(uint t)
        => _assistExpected != null && t > _assistAppliedTick &&
           t <= _assistAppliedTick + AssumeWindowTicks
            ? _assistExpected.Value : Loadout.PilotAssist;
    private const uint AssumeWindowTicks = 90; // 1.5 с — окно доверия ожидаемому состоянию

    /// <summary>Обработать серверное значение флага (событие реплики или опрос).
    /// Сверка с ожидаемым состоянием пилота: подтверждение → снимаем ожидание;
    /// отказ/чужое значение → жёсткий откат предсказания (редкий путь).</summary>
    private void OnAssistReplica(bool serverVal)
    {
        if (!_assistSeenInit) // спавн-синхронизация: принять значение без драмы
        {
            _assistSeenInit = true;
            if (serverVal != Loadout.PilotAssist)
                SetPilotAssist(serverVal);
            _assistExpected = null;
            return;
        }
        if (_assistExpected == serverVal)
        {
            // Подтверждение нашего T (или эхо собственного RPC у host): предсказание
            // уже шло с новым флагом — сверки и переигровки не требуются (п.13).
            _assistExpected = null;
            return;
        }
        if (_assistExpected != null)
        {
            // Сервер ответил ДРУГИМ против нашего ожидания: ожидаемое состояние
            // было ошибочным — откатываем локальный флаг и пересчитываем всю
            // неподтверждённую историю с серверным значением. Это и есть
            // документированный редкий путь «жёсткий откат при отказе сервера».
            _assistExpected = null;
            SetPilotAssist(serverVal);
            if (_hasPrediction && Net != null)
                ReplayInputsAfter((uint)Net.AppliedTick);
        }
        else if (serverVal != Loadout.PilotAssist)
        {
            // Смена флага без нашего запроса — переключение другого игрока
            // (или наше серверное эхо у host): принять как истину.
            SetPilotAssist(serverVal);
        }
    }

    /// <summary>Локально применить переключение ассиста (клавиша T / кнопка
    /// дебага). Пилот: инверсия сразу + пометка ожидаемого состояния + короткая
    /// пауза отправки кадров; сервер-авторитет: применить сразу и переиграть
    /// историю (свои кадры уже применены со старым флагом). Не-пилот-клиент:
    /// ничего (флаг — право пилота, см. отказ RPC).</summary>
    public void RequestTogglePilotAssist()
    {
        if (!IsLocalPilot)
        {
            GD.PrintErr("ship: переключение ассиста запросил НЕ пилот — игнор " +
                        $"(pilot = {PilotPeerId})");
            return;
        }
        bool next = !_assistExpected.GetValueOrDefault(Loadout.PilotAssist);
        if (HasAuthority)
        {
            // Сервер (offline/host): применить сразу и написать реплику.
            SetPilotAssist(next);
            if (Net != null) Net.PilotAssist = (byte)(next ? 1 : 0);
            _assistExpected = null;
            _assistSeenInit = true;
            ReplayAllApplied(); // сервер: история уже прожата со старым флагом
            return;
        }
        // Пилот-клиент: ожидаемое состояние + локальная инверсия предсказания.
        _assistExpected = next;
        _assistAppliedTick = _tick;
        _assistMuteFrames = AssistMuteFrames;
        SetPilotAssist(next); // Loadout + Flight сразу: следующее предсказание — новое
        RpcTogglePilotAssistRpc(next);
    }

    /// <summary>Сервер: переиграть ВСЮ применённую историю кадров заново с
    /// текущими `Flight` (смена флага/модуля посреди буфера).</summary>
    private void ReplayAllApplied()
    {
        if (_serverQueue.Count == 0 && _serverAppliedTick == 0)
            return; // очередь пуста: переигрывать нечего
        uint from = _serverAppliedTick > _serverQueue.Count
            ? _serverAppliedTick - (uint)_serverQueue.Count : 0;
        ReplayAppliedFramesFrom(from);
    }

    /// <summary>Сервер: переиграть уже извлечённые из очереди кадры с тиком &gt;
    /// `from` поверх текущего состояния. Кадровая история сервера хранится в
    /// кольце `_pending` (тот же механизм, что у клиентского `ReplayInputsAfter`):
    /// при смене флага/модуля посреди буфера состояние пересчитывается с новыми
    /// `Flight`, иначе серверные тики от `from` до `_serverAppliedTick` остались
    /// бы прожатыми со СТАРЫМ флагом, и реконсиляция пилота выдала бы ложный
    /// жёсткий откат сразу после T (п.13 требует обратного).</summary>
    private void ReplayAppliedFramesFrom(uint from)
    {
        // Кольцо `_pending` пишет каждый шаг и offline-пилот, и сервер: слот с
        // f.Tick == t содержит кадр, применённый на шаге тика t. Переигрываем
        // кадры (from, _serverAppliedTick] тем же StepPrediction, что и шаг.
        uint to = _serverAppliedTick;
        if (to <= from) return;
        if (to - from > PendingRingSize - 2)
            from = to - (uint)(PendingRingSize - 2); // за кольцом переигрывать нечем
        // `Ship.Rotation` — эйлеровы углы RigidBody3D, `FlightState.Rotation` —
        // кватернион: конвертируем через Basis (тем же путём, что и шаг модели).
        var st = new FlightState
        {
            Position = Position,
            Rotation = Basis.FromEuler(Rotation).GetRotationQuaternion().Normalized(),
            LinearVelocity = LinearVelocity,
            AngularVelocity = AngularVelocity,
        };
        for (uint t = from + 1; t <= to; t++)
        {
            InputFrame f = _pending[t & (PendingRingSize - 1)];
            if (f.Tick != t)
                continue; // дыра (кадр не применялся этим тиком) — пропускаем
            st = StepPrediction(st, f, FlightModel.FixedDt);
        }
        Position = st.Position;
        Rotation = new Basis(st.Rotation).GetEuler();
        LinearVelocity = st.LinearVelocity;
        AngularVelocity = st.AngularVelocity;
    }

    /// <summary>Серверный приём переключения ассиста от пилота (флаг T). Гейт:
    /// запрос принимает только ПИЛОТ этого корабля (иначе чужой piron менял бы
    /// физику чужого корабля — отказ, документированный путь «сервер сказал
    /// ДРУГОЕ» у клиента). Транспорт как у RpcRequestDocking: событие редкое,
    /// надёжный канал 1 — RPC обязан опередить ненадёжные кадры ввода.</summary>
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Reliable, TransferChannel = 1)]
    public void RpcTogglePilotAssistRpc(bool on)
    {
        int sender = Multiplayer.GetRemoteSenderId();
        if (sender != PilotPeerId)
        {
            GD.PrintErr($"ship: RpcTogglePilotAssist от пира {sender}, а пилот = " +
                        $"{PilotPeerId} — отказ (не пилот не переключает ассист)");
            return; // отказ: реплика не меняется, клиент откатится по OnAssistReplica
        }
        ApplyServerPilotAssist(on);
    }

    /// <summary>Серверная истина: применить переключение и опубликовать реплику.
    /// Шов автотеста (`TestRpcToggleAssistForTest`) зовёт прямо этот путь, как
    /// `TestSetPilotForTest` зовёт серверные гейты без копии логики.</summary>
    private void ApplyServerPilotAssist(bool on)
    {
        if (Net != null) Net.PilotAssist = (byte)(on ? 1 : 0);
        if (HasAuthority)
        {
            // Мы и есть сервер: флаг на нас уже подействовал бы через SetPilotAssist;
            // синхронизируем Loadout с репликой и переигрываем применённую историю.
            if (Loadout.PilotAssist != on)
            {
                SetPilotAssist(on);
                ReplayAllApplied();
            }
        }
        // У сервера с внешним пилотом флаг нужен СРАЗУ: кадры из очереди, которые
        // придут позже RPC, уже применяются с новым флагом (комментарий в шаге 4).
    }

    /// <summary>Шов теста: имитировать RPC-приём переключения от пира X без UDP.
    /// Возвращает true, если сервер принял (X — пилот).</summary>
    public bool TestRpcToggleAssistForTest(int senderPeer, bool on)
    {
        if (senderPeer != PilotPeerId)
            return false;
        ApplyServerPilotAssist(on);
        return true;
    }

    /// <summary>Тестовый шов (п.13, п.14): имитировать переключение ассиста на
    /// реальном корабле так, как его делает игрок (клавиша T). Возвращает новое
    /// значение флага. В offline это полный путь (сервер = пилот).</summary>
    public bool TestTogglePilotAssistForTest()
    {
        RequestTogglePilotAssist();
        return Loadout.PilotAssist;
    }

    /// <summary>Локальное состояние флага для дебага/тестов.</summary>
    public bool PilotAssistLocal => Loadout.PilotAssist;

    /// <summary>Последнее увиденное значение реплики `Net.PilotAssist` (255 =
    /// ещё не видел). Опрос каждый физшаг у не-авторитета: OnChange-дельта
    /// Synchronizer приходит «молча» (свойство меняется без события для нас), а
    /// сравнить байт — копейки, и это тот же паттерн, что у стража `SimTick`.</summary>
    private byte _lastAssistReplica = 255;

    /// <summary>Опросить реплику флага и, если она сдвинулась, провести сверку
    /// (`OnAssistReplica`). Вызывается у не-авторитета каждый физшаг.</summary>
    private void PollAssistReplica()
    {
        if (Net == null || Net.PilotAssist == _lastAssistReplica)
            return;
        _lastAssistReplica = Net.PilotAssist;
        OnAssistReplica(Net.PilotAssist != 0);
    }

    // ─────────────── Смена типа корабля (дебаг: три пресета корпуса) ─────────
    // Тот же путь, что у PilotAssist, но без кадровой предикции: смена типа —
    // редкое дебаг-событие, его последствия (масса, тяги, габариты) применяются
    // сервером сразу и приезжают репликой; локальный пилот-клиент предсказывает
    // мгновенно, а сверка по байту реплики откатит его в случае отказа.

    /// <summary>Ядро шага без счётика тика: мышь → ориентация, кадр → тяга.
    /// Вынесено из `AdvanceFlight`, чтобы реконсиляция (шаг 2.4) могла
    /// ПЕРЕИГРАТЬ уже отправленные кадры, не двигая локальный счётчик `_tick`
    /// (у переигрываемых кадров тик уже записан в самом кадре).</summary>
    private FlightState StepPrediction(FlightState s, InputFrame frame, float dt)
    {
        // Мышь: прямой доворот носа (не момент) — ДО шага модели, чтобы
        // ориентация, из которой модель берёт оси тяги, уже была «свежей».
        // Дельта приходит в кадре УЖЕ в радианах: offline-пилот запекает её в
        // `CollectInputFrame` из накопленных пикселей, а сервер с внешним
        // пилотом получает прямо из кадра ввода (`_serverInput`). Интегратор НЕ
        // читает `_lookDelta`: у сервера-авторитета своей мыши по этому кораблю
        // нет, и look прилетает только через кадр (шаг 2.3, §3.5 п.8).
        //
        // Шаг 5.х: в режиме следования (`AngularRiseTime > 0` и задан потолок)
        // прямой дозыписи НЕТ — look превращается в командную угловую скорость
        // в `FlightModel.Step` (§5a), и доворот разгоняется той же τ, что и
        // клавиши момента. Прямой канал здесь оставлен только для режима B
        // (параметры по умолчанию, где потолков нет): его побитовая копия нужна
        // тестам стадий 2-6 и симуляциям без каталога.
        bool lookIsTorque = Flight.AngularRiseTime > 0f && Flight.MaxAngularSpeed > 0f;
        if (!lookIsTorque && (frame.LookYaw != 0f || frame.LookPitch != 0f))
        {
            // Мышь не проходит через `AngularVelocity` (это прямой доворот носа),
            // поэтому её собственный угол тоже обязан быть под потолком: не более
            // `Flight.MaxAngularSpeed` радиан за шаг. Иначе рывок мыши повернул бы
            // нос быстрее, чем корабль умеет крутиться (и быстрее, чем это может
            // воспроизвести сервер/реплика по тем же кадрам ввода).
            // `MaxAngularSpeed <= 0` = без ограничения: дельта проходит как есть.
            Vector2 look = FlightModel.ClampLookRadians(
                new Vector2(frame.LookYaw, frame.LookPitch), Flight.MaxAngularSpeed, dt);
            s.Rotation = ApplyLookRadians(s.Rotation, look.X, look.Y);
        }

        return FlightModel.Step(s, Flight, frame, dt);
    }
    /// <summary>Серверная очередь вводов: последний принятый кадр пилота. Он
    /// применяется на каждом физическом шаге сервера. Держим ровно один кадр, а не
    /// историю: при `unreliable`-канале пропущенный кадр = один кадр без силы, и при
    /// 60 Гц ввода это незаметно (в отличие от потерянного снапшота состояния,
    /// stage2-plan §1.4). Залипание клавиш при этом невозможно: пилот шлёт кадр
    /// КАЖДЫЙ физшаг, даже с пустой маской.</summary>
    private InputFrame _serverInput;

    /// <summary>Серверная ОЧЕРЕДЬ принятых кадров пилота (§2.6, фикс расхождения
    /// предсказания при задержке). Пилот интегрирует ровно один кадр за физшаг;
    /// чтобы серверная истина совпадала с предсказанием пилота, сервер обязан
    /// применять кадры в том же порядке и тоже по одному за шаг. При RTT кадры
    /// приходят пачками — без очереди сервер «схлопывал» бы пачку в один залипший
    /// кадр и интегрировал бы его многократно, теряя остальные: последовательность
    /// сдвигов на сервере переставала совпадать с пилотской, и ошибка предсказания
    /// уходила в сотни метров. Очередь хранит кадры до их применения; если очередь
    /// пуста (пилот молчит/кадры теряются), шаг берёт последний принятый кадр
    /// (`_serverInput`) — залипание, как и раньше, чтобы тесты п.7/8 (один кадр на
    /// много шагов) не сломались.</summary>
    private readonly Queue<InputFrame> _serverQueue = new();

    /// <summary>Тик кадра, реально применённого сервером на последнем шаге
    /// (снимается из очереди). Именно он идёт в `Net.AppliedTick`: снимок
    /// соответствует состоянию «после применения кадра с этим тиком», и пилот,
    /// сверяя реплику со своим следом предсказаний на этом тике, получает честную
    /// ошибку предсказания (§2.6 (а)).</summary>
    private uint _serverAppliedTick;

    // Диагностика §2.6: сколько шагов сервер взял кадр из очереди (dequeue) и
    // сколько раз залип на последнем кадре (очередь пуста). Если sticky велико,
    // сервер интегрирует дубликаты кадров, которых пилот не слал → предсказание
    // расходится. Временная диагностика, убрать после разбора.
    private int _dbgDequeue, _dbgSticky;
    private int _dbgReconcile;

    /// <summary>Серверный гейт: от какого пилотского тика начали принимать кадры.
    /// При смене пилота (или его переподключении) счётчик тика нового пилота
    /// начинается с нуля, и старые кадры прежнего пилота обязаны отбрасываться —
    /// иначе «мёртвый» пир продолжал бы управлять кораблём опоздавшими пакетами
    /// (аналог гейта по SimTick из удалённого RpcReportNetStateToServer).</summary>
    private uint _minAcceptedPilotTick;

    /// <summary>Последний принятый сервером пилотский тик: кадры старше
    /// отбрасываются (дубликаты и переупорядоченные пакеты).</summary>
    private uint _lastAcceptedPilotTick;

    /// <summary>Состояние очереди ввода на сервере (для автотестов и панели
    /// отладки): последний принятый кадр и нижняя граница тика.</summary>
    public InputFrame ServerInputFrame => _serverInput;
    public uint ServerInputMinTick => _minAcceptedPilotTick;

    /// <summary>
    /// Пилот: отправить серверу кадр ввода за текущий физшаг. Отправка идёт
    /// КАЖДЫЙ шаг, даже при пустой маске, — иначе отпущенная клавиша залипает на
    /// сервере и корабль разгоняется без команды (§1.2 плана).
    ///
    /// Транспорт — `unreliable` на отдельном канале ввода: потеря кадра ввода
    /// дешевле, чем задержка, а надёжный канал при плохой сети собрал бы очередь
    /// из устаревших команд.
    /// </summary>
    private void SubmitInputToServer(InputFrame frame)
    {
        if (Net == null || Net.ShipNetId == 0)
            return; // адрес 0 → RPC уходят в никуда (§3.1 п.8)
        // BUG-011 (тот же класс, что и в SnapshotBroadcaster.ClientGapWatch): в
        // Offline-роли наш id = 1, и `RpcId(1, …)` — это RPC «сам себе», который
        // режим MultiplayerApi запрещает (ERROR в логе). Отправка при этом и не
        // нужна: сервер — это мы, а предсказание пилота уже интегрировано локально.
        if (NetworkManager.Instance is not { HasRealPeer: true })
            return;
        RpcId(1, nameof(RpcSubmitInput), Net.ShipNetId,
              frame.Tick, frame.Mask, frame.LookYaw, frame.LookPitch);
        // Бюджет ввода (В6 = C): кадр ввода считается отдельно от репликации,
        // чтобы метрика (г) «< 50 пакетов/с» читалась по репликации, а не по сумме.
        NetworkManager.Instance?.CountInputSent();
    }

    // ------------------------------------------------------- буферы задержки (§2.6)
    /// <summary>Поставить кадр ввода в буфер исходящих. Без задержки (`TestLatencyMs
    /// == 0`) отправляем сразу — поведение идентично прежнему. С задержкой кадр
    /// «дозревает» через N мс и уходит позже (имитация медленного аплинка).</summary>
    private void EnqueueInputForSend(InputFrame frame)
    {
        int lat = NetworkManager.TestLatencyMs;
        if (lat <= 0)
        {
            SubmitInputToServer(frame);
            return;
        }
        _sendBuf.Enqueue(((long)Time.GetTicksMsec() + lat, frame));
    }

    /// <summary>Отправить все кадры ввода, «дозревшие» к этому моменту.</summary>
    private void FlushSendBuffer()
    {
        long now = (long)Time.GetTicksMsec();
        while (_sendBuf.Count > 0 && _sendBuf.Peek().DueMs <= now)
            SubmitInputToServer(_sendBuf.Dequeue().Frame);
    }

    /// <summary>Скопировать текущий снимок `NetState` в буфер приёма с задержкой.
    /// `NetState` перезаписывается репликой каждый кадр, поэтому состояние копируем.
    /// Без задержки снимок обрабатывается сразу (дренаж в том же кадре).</summary>
    private void EnqueueSnapshotFromNet(double nowSec)
    {
        var snap = new NetSnap
        {
            SimTick = Net.SimTick,
            AppliedTick = (uint)Net.AppliedTick,
            Pos = Net.NetPosition,
            Rot = Net.NetRotation,
            Lin = Net.NetLinearVelocity,
            Ang = Net.NetAngularVelocity,
        };
        int lat = NetworkManager.TestLatencyMs;
        _recvBuf.Enqueue((nowSec + (lat > 0 ? lat / 1000.0 : 0.0), snap));
    }

    /// <summary>
    /// §2.4 (R4): запись принятого из батча снимка в `NetState`. Это ЕДИНСТВЕННЫЙ
    /// путь поставки эталона для кораблей, снятых с `MultiplayerSynchronizer`:
    /// батч (`SnapshotBroadcaster`) декодирует запись и вызывает этот метод, а
    /// дальнейшая доставка идёт по уже существующему конвейеру: страж
    /// `SimTick != _lastNetTickSeen` в `_Process` замечает сдвиг и вызывает
    /// `EnqueueSnapshotFromNet` — тот же код, что работал с репликами
    /// Synchronizer, поэтому реконсиляция/интерполяция не меняются.
    ///
    /// Монотонность: пишем только если снимок НОВЕЕ уже лежащего (`SimTick`
    /// строго больше). Нестабильный порядок unreliable-пакетов не должен
    /// откатывать состояние назад; просроченный снимок просто игнорируется.
    /// </summary>
    public void ApplyBatchRecord(in SpaceCorporation.Net.ShipSnapshotQ s)
    {
        if (HasAuthority)
            return; // авторитет сам пишет NetState каждый физический шаг (§1088)
        if (s.SimTick <= Net.SimTick)
            return; // дубликат/просрочка: порядок на wire не гарантирован

        Net.NetPosition = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
        Net.NetRotation = new Quaternion(s.Rotation.X, s.Rotation.Y, s.Rotation.Z, s.Rotation.W);
        if (!s.FarTier)
        {
            // Дальний тир скоростей не несёт (14 байт на запись, §2.1): оставляем
            // прежние — наблюдателю они нужны лишь для экстраполяции на ~0.25 с.
            Net.NetLinearVelocity = new Vector3(s.LinearVelocity.X, s.LinearVelocity.Y, s.LinearVelocity.Z);
            Net.NetAngularVelocity = new Vector3(s.AngularVelocity.X, s.AngularVelocity.Y, s.AngularVelocity.Z);
        }
        // §6.3/R8 (шаг 7): помечаем тир последнего принятого wire-снимка —
        // унос картинки захваченного корабля (сдвиг точки съёмки вдоль v_frame)
        // опирается на СКОРОСТИ, а far-запись их не несёт и оставляет прежние
        // (протухшие). В far — только поза без уноса.
        _lastWireFarTier = s.FarTier;
        if (s.ForPilot)
        {
            // Реконсиляция пилота: эталон обязан нести тик/маску применённого
            // ввода, иначе переигрывание стартует не оттуда, откуда надо (§1.5).
            Net.AppliedTick = s.AppliedTick;
            Net.AppliedMask = s.AppliedMask;
        }
        Net.SimTick = s.SimTick; // ПИШЕМ ПОСЛЕДНИМ: страж `_Process` читает его
        // и по сдвигу забирает снимок в буфер приёма; до этого момента поля
        // позы должны быть уже целыми (в одном потоке между кадрами — да).
    }

    /// <summary>Полётное состояние из задержанного снимка (для реконсиляции).</summary>
    private static FlightState StateFromSnap(NetSnap s) => new()
    {
        Position = s.Pos,
        Rotation = s.Rot,
        LinearVelocity = s.Lin,
        AngularVelocity = s.Ang,
    };

    /// <summary>Сервер: принять кадр ввода от пилота. Гейты (по образцу
    /// `Player.IsRequestFromOwner`, §2.3): принимать может только сервер; адрес
    /// кадра обязан совпасть с адресом этого корабля (RPC адресуются по
    /// ShipNetId, а не по пути, §4 В4 п.3); отправитель обязан быть пилотом этого
    /// корабля; тик обязан быть не ниже нижней границы и строго расти.</summary>
    // Шаг R8: кадр ввода идёт unreliable на канале 2 (тот же канал, что и
    // репликационные снимки). Причина: надёжный канал при потерях собрал бы
    // очередь устаревших команд — задержка важнее потерянного кадра, а следующий
    // кадр ввода перекрывает предыдущий (состояние корабля интегрируется по
    // тикам, потерянный кадр = один пропущенный шаг, не рассинхрон).
    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
         TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable, TransferChannel = 2)]
    public void RpcSubmitInput(int shipNetId, uint tick, uint mask, float lookYaw, float lookPitch)
    {
        // Реальный сетевой приём кадра ввода — в бюджет ввода (В6 = C), а не в
        // репликацию. Считаем до гейта: кадр дошёл по сети независимо от того,
        // примет ли его логика (отклонённый кадр всё равно стоил пакет).
        NetworkManager.Instance?.CountInputRecv();
        AcceptInputFrom(Multiplayer.GetRemoteSenderId(), shipNetId, tick, mask, lookYaw, lookPitch);
    }

    /// <summary>Шов автотеста (§3.9 п.15): «кадр пришёл от пира X» без UDP. В
    /// headless-тесте реального соединения нет, а `Multiplayer.GetRemoteSenderId()`
    /// вне RPC возвращает 0, поэтому тест зовёт тот же метод гейта, что и RPC, —
    /// проверяется боевой код, а не его копия (§3.8).</summary>
    public bool TestSubmitInput(int senderPeer, uint tick, uint mask,
                                float lookYaw = 0f, float lookPitch = 0f)
        => AcceptInputFrom(senderPeer, Net?.ShipNetId ?? 0, tick, mask, lookYaw, lookPitch);

    /// <summary>Серверные гейты кадра ввода + приём в очередь. Возвращает true,
    /// если кадр принят. Счётчики принявших/отклонённых кадров — для assert'ов
    /// п.7/п.8 автотеста.</summary>
    private bool AcceptInputFrom(int senderPeer, int shipNetId, uint tick, uint mask,
                                 float lookYaw, float lookPitch)
    {
        if (NetworkManager.Instance is not { IsServer: true })
            return false; // только сервер интегрирует корабль (В14=B)
        if (Net == null || Net.ShipNetId != shipNetId)
            return false; // кадр про другой корабль (адрес важнее пути)
        if (!HasAuthority)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: кадр ввода пришёл пиру, который не является " +
                        $"авторитетом корабля (auth = {GetMultiplayerAuthority()}) — отказ");
            RejectedInputCount++;
            return false;
        }

        if (senderPeer != PilotPeerId)
        {
            GD.PrintErr($"ship#{Net.ShipNetId}: кадр ввода от пира {senderPeer}, а пилот = " +
                        $"{PilotPeerId} — отказ (§3.5 п.8)");
            RejectedInputCount++;
            return false;
        }

        if (tick < _minAcceptedPilotTick || tick <= _lastAcceptedPilotTick)
        {
            RejectedInputCount++;
            return false; // устаревший/дублирующийся кадр прежнего пилота
        }

        _lastAcceptedPilotTick = tick;
        // В провод летят только tick+mask+мышь (16 байт, §1.3); оси обязан
        // восстановить сервер — ровно так же, как это делает клиент в
        // `CollectInputFrame` (через `FromMask`). Иначе `FlightModel.Step`,
        // читающий оси (AxisF/…), не увидел бы тягу: кадр с ThrustF дал бы
        // нулевое ускорение (§3.5 п.7).
        _serverInput = InputFrame.FromMask(tick, mask, lookYaw, lookPitch);
        // Кладём кадр в очередь: сервер применит его ровно один раз на своём шаге
        // (в порядке тиков), как пилот применил бы на своём. Так последовательность
        // сдвигов сервера совпадает с предсказанием пилота при любой задержке.
        _serverQueue.Enqueue(_serverInput);
        AcceptedInputCount++;
        LastAcceptedInputTick = (int)tick;
        return true;
    }

    /// <summary>Сколько кадров ввода принял и отклонил сервер (автотесты п.7-п.8).</summary>
    public int AcceptedInputCount { get; private set; }
    public int RejectedInputCount { get; private set; }

    /// <summary>Тик последнего принятого кадра (для панели отладки: по нему видно,
    /// сколько кадров потеряно на unreliable-канале).</summary>
    public int LastAcceptedInputTick { get; private set; }

    /// <summary>Кадр ввода для очередного шага модели: сервер берёт его из очереди
    /// пилота, offline-пилот — читает клавиши и мышь напрямую. Разделение нужно
    /// потому, что сервер физически НЕ имеет доступа к чужой клавиатуре, а
    /// offline-сервер (он же пилот) не должен гонять ввод через RPC-никуда.</summary>
    private InputFrame FrameForStep()
    {
        // Тестовый шов (TestSetInputForTest): кадр из теста имеет приоритет над
        // клавишами и очередью — шов обязан работать в обеих ролях, иначе
        // задокументированный «ввод без клавиатуры» молча подменяется реальным
        // (в headless это пустота) и тесты п.12/Stage5BlockC п.21 меряют не то.
        if (_testInput)
            return CacheFrameMask(_testFrame);
        // Offline: сервер = локальный пир, ввод читается с реальных клавиш.
        if (IsLocalPilot)
            return CacheFrameMask(CollectInputFrame());
        // Сервер с внешним пилотом: применяем кадры пилота по одному за шаг, в
        // порядке тиков (очередь) — так серверная последовательность сдвигов
        // совпадает с предсказанием пилота при любой задержке (§2.6 фикс (а)).
        // Если очередь пуста (пилот молчит/кадры теряются) — залипаем на последнем
        // принятом кадре, чтобы молчание не обнуляло тягу мгновенно и тесты п.7/8
        // (один кадр на много шагов) работали как раньше.
        if (_serverQueue.Count > 0)
        {
            _serverInput = _serverQueue.Dequeue();
            _serverAppliedTick = _serverInput.Tick;
            _dbgDequeue++;
        }
        else
            _dbgSticky++;
        return CacheFrameMask(_serverInput);
    }

    /// <summary>Сервер: начать принимать кадры пилота с его текущего тика.
    /// Вызывается при назначении пилота, чтобы счётчик нового пилота не «отсекся»
    /// гейтом и чтобы кадры прежнего пилота перестали применяться.</summary>
    private void ResetInputQueue()
    {
        _serverInput = default;
        _serverQueue.Clear();
        _serverAppliedTick = 0;
        _lastAcceptedPilotTick = 0;
        // Нижняя граница: тик серверного корабля. Пилотский счётчик в общем случае
        // не совпадает с серверным (у пилота свои физкадры), поэтому шаг 2.4
        // введет единую шкалу тиков; на шаге 2.3 принимаем кадры с любого тика,
        // кроме заведомо старых (0 = кадр до назначения пилота).
        _minAcceptedPilotTick = 1;
    }


    private static void ApplyHull(Node3D hull, Snapshot s)
    {
        hull.GlobalTransform = new Transform3D(new Basis(s.Rotation), s.Position);
    }

    private static void ApplyHullCarried(Node3D hull, Snapshot s, Vector3 carry)
    {
        hull.GlobalTransform = new Transform3D(new Basis(s.Rotation), s.Position + carry);
    }

}
