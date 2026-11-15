# PLAN: орудие как отдельная сцена, углы наводки, доворот ствола, прицельная точка на HUD, смена орудия

Дата: этап 6-расширение (после PLAN_stage6_combat). Журнал: DEVELOPMENT.md (раздел «Орудие: наводка»).

## ТЗ (своими словами)
1. К мешу оружия корабля добавить меш синего цвета; оружие вынести в отдельную сцену.
2. Локальному игроку ствол оружия доворачивается за прицельной точкой (курсором).
3. Прицельная точка оружия отображается на HUD.
4. У каждого оружия параметр «углы наводки» (AimLimitDeg): максимум поворота ствола
   от оси корабля. Если курсор вышел за угол — прицельная точка остаётся на линии
   «центр экрана → курсор», но на границе допустимого угла.
5. Два тестовых орудия: RedFiveDegTurret (5°, красный луч, красный меш) и
   GreenFifteenDegTurret (15°, зелёный луч, зелёный меш); остальные параметры — как у лазера.
6. Дебаг-кнопка «Switch Weapon» (сменить орудие): циклически перебирает орудие корабля —
   задел под будущую замену оснащения (EquipWeapon API).

## Ключевые файлы (что уже есть)
- `scripts/entities/WeaponTypes.cs` — enum WeaponType(byte, порядок=будущий байт реплики,
  только дописывать в конец), WeaponSpec (чистые данные), WeaponCatalog (Specs, Find,
  FindByByte, DefaultWeapon=LaserTurret).
- `scripts/entities/Ship.Combat.cs` (partial Ship):
  - `Weapon` (WeaponSpec), `ApplyWeaponSpec(spec)` (зеркалит RangeM→LaserRange,
    HullDamage→FireDamage, FireIntervalSec→FireCooldownSec).
  - гейты выстрела: `TryFire` (кулдаун+энергия, `aimPoint=AimPointWorld()`), клиент
    `SubmitFireToServer` → RPC `RpcRequestFire(shipNetId,tick,aimPoint)` → сервер
    `AcceptFireFrom` (сверка `aimPoint` с носом `-Basis.Z` в конусе `AimToleranceM`).
  - `AimPointWorld()` (:213): цель → рейкаст камеры по курсору (`_pilotInput.LastMousePx`,
    маска LaserMask) → `origin + aimDir.normalized()*LaserRange`; фолбэк нос.
  - `ProcessPendingFires()` (:489): хитскан от `BeamOriginGlobal()` (= `_muzzle`
    GlobalPosition; `_muzzle = GetNodeOrNull("Muzzle")` Ship.cs:501) к принятой aimPoint.
  - `ShowAimingBeam(hitscanPoint)` (:794): цилиндр BeamVisual, белый материал (~:845).
- `scenes/Ship.tscn`: `Muzzle` Node3D pos (0,0,-2) — дочка корабля. Ship.cs:166-169:
  при смене типа `muzzle.Position.z = -HullSize.Z*0.5`.
- HUD: `scripts/ui/AimReticle.cs` (ShowAim, центр=Size/2, курсор=GetGlobalMousePosition,
  _Draw примитивами), `scripts/ui/HudLayer.cs:304` `_reticle.ShowAim = myShip!=null &&
  myShip.IsLocalPilot && !myShip.IsInteractionMode`, `GetMyShip()` (:345).
- Дебаг: `scripts/entities/Ship.Debug.cs` `BuildDebugButtons()` (кнопки по ручкам,
  блок `if (IsLocalPilot)` ~:128: assist + ShipType:* по каталогу), `DebugInfoBus.RegisterDebugButton`.
- `scripts/core/PilotAim.cs` — нос доворачивается к курсору (центр экрана = нос).
- Тесты: `tests/Stage1Test.cs:134` проверяет `GetNodeOrNull("Muzzle") != null`;
  Stage3Test BeamVisual/`TestFireRequest`. Запуск: `./run-tests.sh [res://tests/X.tscn]`.

## Дизайн (решения)
1. **WeaponSpec** += `AimLimitDeg` (углы наводки, градусы; `AimLimitRad` свойство),
   `MeshColor` (меш орудия), `BeamColor` (трассер). Лазеру AimLimitDeg = 8.594°
   (≈ AimToleranceM 0.15 рад — совместимость с существующими тестами В17).
2. enum += `RedFiveDegTurret = 1`, `GreenFifteenDegTurret = 2` (в конец!).
   Параметры боя = копирка лазера (400 м, щит 10, корпус 20, энергия 10, 2/сек),
   5°/красный и 15°/зелёный.
3. **Новая сцена `scenes/WeaponTurret.tscn`** + `scripts/entities/WeaponTurret.cs`
   (Node3D-компонент): `Body` (MeshInstance3D, «башня», синий меш по умолчанию) →
   `Pivot` (Node3D, доворот ствола) → `Barrel` (MeshInstance3D, цилиндр вдоль -Z) +
   `Muzzle` (Node3D на срезе ствола). API:
   - `ApplyWeapon(WeaponSpec)` — покраска Body/Barrel в MeshColor (т.з.: синий меш
     оружия по умолчанию), хранение лимита;
   - `AimBarrel(Vector3 worldAimDir)` — ствол доворачивается на локальное направление
     прицела, ограниченное конусом `AimLimitRad` вокруг (0,0,-1);
   - `MuzzleGlobal` — мировая позиция среза ствола.
4. **Ship.tscn**: инстанс WeaponTurret.tscn как `Weapon` (вместо голого `Muzzle`),
   ствол торчит из носа. Ship.cs: `_weapon = GetNodeOrNull<WeaponTurret>("Weapon")`;
   `_muzzle = _weapon.Muzzle` (фолбэк legacy-узел "Muzzle"); при смене корпуса
   смещаем `Weapon.Position.z`, а не Muzzle.
5. **`scripts/core/WeaponAim.cs`** — чистая статика `ClampToCone(Vector3 fwd, Vector3 dir,
   float maxAngle)`: если угол(dir,fwd) > maxAngle — повернуть dir к fwd на maxAngle
   (поворот в плоскости fwd/dir; dir==fwd → dir). Крыть юнит-проверками в Stage6-стиле.
6. **Наводка**: `AimPointWorld()` ограничивает направление хитскана конусом орудия
   (`Weapon.AimLimitRad` вместо жёсткого носа+0.15) — точка «за угол» легально лежит на
   границе конуса на линии центр→курсор. Серверный гейт `AcceptFireFrom`: допуск =
   `Weapon.AimLimitRad` (ApplyWeaponSpec пишет в AimToleranceM — сервер обязан знать
   ровно тот же конус, что и клиент, иначе выстрелы за угол отклоняются В17 п.3).
   Ствол локального игрока доворачивается каждый физшаг (`_weapon.AimBarrel(направление
   из AimPointWorld)`; последнее направление кешируем в `_weaponAimDir`).
7. **Трассер** `ShowAimingBeam`: материал = `Weapon.BeamColor` (белый эмиссивный
   оставлен фолбэком цвета не будет). Хитскан уже летит в clamp-нутую aimPoint.
8. **HUD**: AimReticle += `HasWeaponAim`, `WeaponAimPx`, `WeaponAimColor` — рисует
   кольцо-марку прицельной точки орудия + отрезок «нос-марка» штрихом; HudLayer
   проецирует `myShip.WeaponAimPointWorld()` камерой в экранные пиксели
   (WorldPositionToScreen под камерой), только при ShowAim.
9. **Оснащение**: `Ship.EquipWeapon(WeaponType)` — единственная точка смены орудия
   (apply spec + перекраска меша + лимит; публичная, задел под репл-слот оснащения:
  =comment «сеть/слоты — следующий шаг»). `CurrentWeaponType` геттер. Дебаг-кнопка
   `Switch Weapon` в блоке IsLocalPilot (по ручке, цикл по WeaponCatalog.Types).
10. **Тесты**: Stage1Test «Muzzle» → «Weapon/Muzzle»; новый Stage-прогон не заводим,
    проверки наводки добавляем в существующий Stage6Test (clamp-математика +
    EquipWeapon + цвета) — минимум: юнит WeaponAim.ClampToCone и Ship.EquipWeapon.

## Шаги выполнения
1. [x] План-файл + раздел журнала.
2. [x] WeaponTypes: поле/цвета + 2 тестовых орудия.
3. [x] WeaponAim.cs (математика конуса) + тест-проверки.
4. [x] WeaponTurret.tscn/.cs + инстанс в Ship.tscn + правки Ship.cs (Muzzle→Weapon).
5. [x] Ship.Combat: clamp в AimPointWorld, AimToleranceM=Weapon.AimLimitRad, BeamColor,
   доворот ствола в _PhysicsProcess, EquipWeapon/CurrentWeaponType.
6. [x] HUD: WeaponAimPx в AimReticle + проекция в HudLayer.
7. [x] Дебаг-кнопка «Switch Weapon» (Ship.Debug.cs).
8. [x] Тесты: вместо правок Stage6Test заводим отдельный `tests/WeaponAimTest.tscn`
   (40 проверок: математика/каталог/турель/EquipWeapon); Stage1Test переведён
   с `Muzzle` на `Weapon/Muzzle`; швы `BeamOriginForTest`/`AimToleranceForTest`.
9. [x] Отметить шаги плана, обновить DEVELOPMENT.md (раздел «Этап 6.2»), финальный critic.

## Итог (факт)
- WeaponAimTest 47/0, Stage6Test 63/0, Stage2/3/5/7 зелёные, NetTest 127/1 (предсущ.).
- Stage1 135/3 (ωmax каталога) и краш Stage4 — предсуществующие, сверено с чистым HEAD.
- Правки по самокритике: ветка захвата (Tab) унифицирована с конусом углов наводки;
  раздельные материалы Body/Barrel турели; дальность марки HUD перенесена из
  константы корабля в `WeaponSpec.AimMarkerRangeM`; в тест добавлены проверки
  захвата (шов `TargetingService.TestSetTargetForTest`) — 40 → 47 проверок.
- Баг-фикс (после сдачи): марка HUD не совпадала с рейкастом/висела выше цели и
  следовала за курсором при захвате — `WeaponAimPointWorld()` теперь отдаёт
  кэшированную фактическую точку прицела `_weaponAimMarker` (попадание рейкаста /
  позиция цели; вне сектора — граница конуса на глубине цели), фикс. дистанция
  `AimMarkerRangeM` удалена; признак «внутри сектора» считается по исходному
  направлению, а не по схлопнутому. WeaponAimTest 47/0.
