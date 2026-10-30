using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace SpaceCorporation.Net;

/// <summary>
/// Серверная пространственная сетка для расчёта интереса (AOI) — шаг R2 плана
/// net-refactor-plan.md (§2.3). Чистая, без Godot: работает на <see cref="Vec3"/>,
/// поэтому тестируется без движка (tools/SpatialIndexTest).
///
/// Идея: корабли раскладываются в грид <see cref="CellSize"/> м. Запрос тиров
/// обходит только ячейки, попадающие в сферу дальнего тира, и для каждого тела
/// применяет гистерезис ±10 % по радиусу, чтобы тело на границе тира не «мигало»
/// (то шлём, то нет → артефакт интерполяции, см. риск-таблицу плана).
///
/// Принадлежность тира хранится НА КЛИЕНТА (<see cref="InterestState"/>): у каждого
/// игрока свой центр интереса (корабль/камера), поэтому один и тот же корабль может
/// быть near у одного и far у другого. Сам грид общий и перестраивается раз в
/// 0.25 с (<see cref="Rebuild"/>), а не каждый тик.
/// </summary>
public sealed class SpatialIndex
{
    /// <summary>Ребро ячейки грида, м (чанк 400 м по плану).</summary>
    public const float CellSize = 400f;

    // Позиции слотов (slot → позиция). slot — плотный индекс 0..Count-1.
    private Vec3[] _pos = new Vec3[512];
    public int Count { get; private set; }

    // Грид: ключ ячейки → список слотов в ней. Списки переиспользуются между
    // Rebuild (Clear, а не new), чтобы перестройка 4 Гц не аллоцировала.
    private readonly Dictionary<long, List<int>> _cells = new(1024);
    private readonly List<long> _usedKeys = new(1024);

    // -------------------------------------------------------------- Rebuild

    /// <summary>
    /// Перестроить грид по позициям. Вызывается редко (раз в 0.25 с), поэтому
    /// допустима умеренная работа; списки ячеек переиспользуются.
    /// </summary>
    public void Rebuild(ReadOnlySpan<Vec3> positions)
    {
        // Очистить прошлые ячейки, сохранив сами списки для переиспользования.
        for (int i = 0; i < _usedKeys.Count; i++)
            _cells[_usedKeys[i]].Clear();
        _usedKeys.Clear();

        Count = positions.Length;
        if (positions.Length > _pos.Length)
            Array.Resize(ref _pos, positions.Length);

        for (int slot = 0; slot < positions.Length; slot++)
        {
            Vec3 p = positions[slot];
            _pos[slot] = p;
            long key = CellKey(p);
            if (!_cells.TryGetValue(key, out List<int>? list))
            {
                list = new List<int>(4);
                _cells[key] = list;
            }
            if (list.Count == 0)
                _usedKeys.Add(key);
            list.Add(slot);
        }
    }

    // -------------------------------------------------------------- Query

    /// <summary>
    /// Разложить тела по трём тирам интереса относительно <paramref name="origin"/>.
    /// <paramref name="state"/> хранит последний тир каждого слота (гистерезис).
    /// Буферы <paramref name="near"/>/<paramref name="mid"/>/<paramref name="far"/>
    /// принадлежат вызывающему и переиспользуются: внутри только Clear + Add,
    /// аллокаций на запрос нет (кроме роста буфера при первом обращении).
    /// </summary>
    public void QueryTiers(Vec3 origin, float nearR, float midR, float farR,
                           InterestState state,
                           List<int> near, List<int> mid, List<int> far)
    {
        near.Clear(); mid.Clear(); far.Clear();
        state.EnsureCapacity(Count);
        // Свежий тир этого запроса: обнуляем тир всех тел, опубликованных в
        // ПРОШЛОМ запросе (шаг R7). Тело, вышедшее из сферы farR, сюда не
        // заходит (обходим только ячейки в радиусе) и потому не будет опубликовано
        // — его CurTier останется None, и гейтинг событий попаданий его не отдаст.
        // LastTier (гистерезис) при этом НЕ трогаем: он память «где тело было»,
        // нужна порогу удержания ±10 %.
        state.BeginQuery();

        // Квадраты порогов входа/выхода (гистерезис ±10 %).
        float nearIn = nearR * 0.9f, nearOut = nearR * 1.1f;
        float midIn = midR * 0.9f, midOut = midR * 1.1f;
        float farIn = farR * 0.9f, farOut = farR * 1.1f;
        float nearIn2 = nearIn * nearIn, nearOut2 = nearOut * nearOut;
        float midIn2 = midIn * midIn, midOut2 = midOut * midOut;
        float farIn2 = farIn * farIn, farOut2 = farOut * farOut;

        // Диапазон ячеек, накрывающий сферу farR вокруг origin.
        int cx0 = FloorCell(origin.X - farR), cx1 = FloorCell(origin.X + farR);
        int cy0 = FloorCell(origin.Y - farR), cy1 = FloorCell(origin.Y + farR);
        int cz0 = FloorCell(origin.Z - farR), cz1 = FloorCell(origin.Z + farR);

        for (int cx = cx0; cx <= cx1; cx++)
        for (int cy = cy0; cy <= cy1; cy++)
        for (int cz = cz0; cz <= cz1; cz++)
        {
            long key = PackCell(cx, cy, cz);
            if (!_cells.TryGetValue(key, out List<int>? list)) continue;

            for (int k = 0; k < list.Count; k++)
            {
                int slot = list[k];
                Vec3 d = _pos[slot] - origin;
                float r2 = d.LengthSquared;
                int last = state.LastTier[slot];

                // Внутренний тир проверяем первым: тело попадает в самый
                // близкий из подходящих. Гистерезис: вход по 0.9R, удержание
                // по 1.1R, если тело уже было в этом (или более близком) тире.
                int tier;
                if (r2 <= nearIn2 || (last == TierNear && r2 <= nearOut2))
                    tier = TierNear;
                else if (r2 <= midIn2 || (last <= TierMid && r2 <= midOut2))
                    tier = TierMid;
                else if (r2 <= farIn2 || (last <= TierFar && r2 <= farOut2))
                    tier = TierFar;
                else
                    tier = TierNone;

                state.LastTier[slot] = (byte)tier;
                // Свежий тир этого запроса (шаг R7): публикуем для гейтинга
                // событий попаданий. Тело вне farR сюда не доходит и остаётся None.
                state.Publish(slot, tier);
                switch (tier)
                {
                    case TierNear: near.Add(slot); break;
                    case TierMid: mid.Add(slot); break;
                    case TierFar: far.Add(slot); break;
                }
            }
        }
    }

    /// <summary>
    /// Гейтинг события попаданий по интересу (шаг R7). Событие (выстрел/попадание
    /// между двумя кораблями) шлём пиру, только если ХОТЬ ОДНА из сторон события
    /// сейчас в поле зрения этого пира (его свежий тир тела != None). Иначе клиент
    /// получил бы визуальный/звуковой эффект про корабль, который он не видит, —
    /// ровно тот перерасход, который R7 устраняет.
    ///
    /// slotA/slotB — слоты тел в этом индексе (−1 = тела нет в индексе, например
    /// корабль вне мира: тогда сторона не даёт интереса). Чистая функция по свежему
    /// CurTier последнего QueryTiers пира — тестируется без Godot.
    /// </summary>
    public static bool PeerWantsEvent(InterestState st, int slotA, int slotB)
    {
        bool a = slotA >= 0 && st.TierOf(slotA) != TierNone;
        bool b = slotB >= 0 && st.TierOf(slotB) != TierNone;
        return a || b;
    }

    // -------------------------------------------------------------- helpers

    public const int TierNear = 0;
    public const int TierMid = 1;
    public const int TierFar = 2;
    public const int TierNone = 3;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorCell(float v) => (int)MathF.Floor(v / CellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CellKey(Vec3 p)
        => PackCell(FloorCell(p.X), FloorCell(p.Y), FloorCell(p.Z));

    /// <summary>
    /// Упаковка координат ячейки в long без коллизий. Диапазон ±1023 ячеек
    /// (±409 км при чанке 400 м) покрывает поле 13 км с огромным запасом.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackCell(int cx, int cy, int cz)
        => ((long)(cx + 1024) << 22) | ((long)(cy + 1024) << 11) | (long)(cz + 1024);
}

/// <summary>
/// Пер-клиентское состояние интереса: последний присвоенный тир каждого слота.
/// Нужен для гистерезиса — решение «слать/не слать» принимается с учётом того,
/// где тело было в прошлом пересчёте.
/// </summary>
public sealed class InterestState
{
    /// <summary>Последний тир слота (0 near, 1 mid, 2 far, 3 none). Память для
    /// гистерезиса: где тело было в прошлом пересчёте. Для ушедшего из farR тела
    /// устаревает (его не обходим) — поэтому для гейтинга событий не используется.</summary>
    public byte[] LastTier;

    /// <summary>Тир слота на ЭТОМ запросе (шаг R7, гейтинг событий попаданий).
    /// Вне радиуса farR тело не обходится и потому остаётся TierNone — в отличие
    /// от LastTier. Гейтинг читает именно CurTier (свежий), а не LastTier.</summary>
    public byte[] CurTier;

    // Слоты, опубликованные в текущем запросе (для дешёвой очистки CurTier на
    // следующем BeginQuery вместо обнуления всего массива).
    private readonly List<int> _curList = new(512);

    public InterestState()
    {
        // Важно: «свежий» слот = нигде не был (TierNone), а не 0 (= TierNear).
        // Иначе по умолчанию все слоты считались бы «были near» и гистерезис
        // удержания (last==near && r<=1.1R) затягивал бы дальние тела в near.
        LastTier = new byte[512];
        Array.Fill(LastTier, (byte)SpatialIndex.TierNone);
        CurTier = new byte[512];
        Array.Fill(CurTier, (byte)SpatialIndex.TierNone);
    }

    public void EnsureCapacity(int n)
    {
        if (n > LastTier.Length)
        {
            // Новый массив заполняем TierNone: раньше тела нигде не были.
            var bigger = new byte[n];
            Array.Fill(bigger, (byte)SpatialIndex.TierNone);
            Array.Copy(LastTier, bigger, LastTier.Length);
            LastTier = bigger;
            var biggerCur = new byte[n];
            Array.Fill(biggerCur, (byte)SpatialIndex.TierNone);
            Array.Copy(CurTier, biggerCur, CurTier.Length);
            CurTier = biggerCur;
        }
    }

    /// <summary>Начать новый запрос: обнулить тир тел, опубликованных в прошлом
    /// (шаг R7). Вызывается в начале QueryTiers. LastTier (гистерезис) не трогаем.</summary>
    public void BeginQuery()
    {
        for (int i = 0; i < _curList.Count; i++)
            CurTier[_curList[i]] = (byte)SpatialIndex.TierNone;
        _curList.Clear();
    }

    /// <summary>Опубликовать свежий тир тела этого запроса (шаг R7).</summary>
    public void Publish(int slot, int tier)
    {
        CurTier[slot] = (byte)tier;
        if (tier != SpatialIndex.TierNone) _curList.Add(slot);
    }

    /// <summary>Свежий тир тела по последнему QueryTiers (шаг R7). Вне диапазона
    /// или вне радиуса farR — TierNone (тела нет в поле зрения клиента).</summary>
    public int TierOf(int slot)
        => (uint)slot < (uint)CurTier.Length ? CurTier[slot] : SpatialIndex.TierNone;

    /// <summary>Сбросить всю историю в «нигде не был» (например, при спавне).</summary>
    public void Reset()
    {
        Array.Fill(LastTier, (byte)SpatialIndex.TierNone);
        Array.Fill(CurTier, (byte)SpatialIndex.TierNone);
        _curList.Clear();
    }
}
