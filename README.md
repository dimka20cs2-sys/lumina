<div align="center">
  <img src="assets/img/lumina-512.png" alt="Lumina Logo" width="112" height="112" />
  <h1>Lumina — Minecraft: Java Edition Launcher</h1>
  <p><strong>Быстрый, лёгкий и безопасный десктопный лаунчер Minecraft для Windows от AkeeTech</strong></p>
  <p>
    <a href="https://lumina-mauve-delta.vercel.app"><strong>Официальный сайт</strong></a> ·
    <a href="https://github.com/dimka20cs2-sys/lumina/releases/latest"><strong>Скачать установщик v1.0.0 (.EXE)</strong></a> ·
    <a href="SECURITY.md"><strong>Архитектура безопасности</strong></a>
  </p>
</div>

---

## О проекте

**Lumina** — нативный лаунчер для **Minecraft: Java Edition** (`1.20+`) под Windows 10/11, написанный на C# (`.NET Framework 4.x` + `WebView2`) без тяжёлых фреймворков вроде Electron, без рекламы, телеметрии и фоновых служб. Вес установщика — всего **1.7 МБ**.

### Ключевые возможности
- **Все актуальные загрузчики модов в 1 клик**: `Vanilla`, `Fabric`, `Quilt`, `Forge`, `NeoForge` + автоматическая докачка нужной версии `Java Runtime` с серверов Mojang.
- **ModNest — встроенный каталог Modrinth**: поиск и установка модов, шейдеров, ресурспаков, датапаков и модпаков (`.mrpack`) с автоматическим контролем версий, зависимостей и обновлений.
- **Lumina Boost Pack и автонастройка графики**: установка `Sodium`, `Lithium`, `FerriteCore` и готовых профилей графики (*Максимум FPS / Баланс / Качество*) под конкретную видеокарту, ОЗУ и герцовку монитора.
- **Игровой режим (Game Mode)**: принудительный запуск `javaw.exe` на дискретной GPU, переключение схемы питания Windows и освобождение памяти лаунчера на время игры.
- **Скины, Серверы и Миры**:
  - 3D/2D предпросмотр скинов и плащей с загрузкой на аккаунт Minecraft в один клик.
  - Мониторинг серверов (живой пинг, онлайн, MOTD, импорт из `servers.dat`) и прямой вход в выбранной сборке.
  - Менеджер миров, резервных копий (бэкап/восстановление без перезаписи) и галерея скриншотов.
- **4 темы оформления**: *Закат Lumina*, *Зелёный градиент*, *Индиго* и *Тёмный градиент* с динамической сменой иконки.

---

## Безопасность и приватность (`Defense-in-Depth`)

Подробная техническая документация описана в **[SECURITY.md](SECURITY.md)**:
1. **OAuth 2.0 Device Flow (`src/MicrosoftAuth.cs`)**: лаунчер никогда не запрашивает и не видит ваш пароль — вход выполняется в системном браузере исключительно на официальном домене `login.microsoftonline.com`.
2. **Шифрование токенов через Windows DPAPI (`src/AccountStore.cs`)**: файл `accounts.dat` шифруется системным криптопровайдером `ProtectedData (CurrentUser)` с уникальной энтропией. Расшифровать его на другом ПК невозможно.
3. **Криптографическая проверка загрузок (`src/Downloader.cs`, `src/ModNest.cs`)**: каждый файл игры и Java сверяется по `SHA-1`, а контент из Modrinth — по `SHA-512` до установки.
4. **Строгий сетевой белый список (`AllowedHosts` / `PackHosts`) и защита от `Zip-Slip`**: соединения разрешены только по `TLS 1.2 / 1.3` со списком официальных доменов Microsoft, Mojang, Fabric, Quilt, Forge, NeoForge и Modrinth.

### Контрольные суммы релиза `v1.0.0` (`SHA-256`)

| Файл | Описание | `SHA-256` |
| :--- | :--- | :--- |
| [`Lumina-Setup-1.0.0.exe`](downloads/Lumina-Setup-1.0.0.exe) | Графический установщик Windows (`asInvoker`) | `4eafb81f742bc5416a715dbc5f837e30452ca3c2a716c00e8f67e10188a786f3` |
| [`Lumina-1.0.0.zip`](downloads/Lumina-1.0.0.zip) | Portable-архив лаунчера (без установки) | `e596632ed4664827bb9f1937001605c85f34cd7b1ac4ae0b8adfa20742f4e07b` |
| `Lumina.exe` | Основной исполняемый файл лаунчера | `c5a9fca40fc94529c6cf36df384e192e31fadeb1d3d12efc1e4cfa7d7b56b507` |

Точные значения `SHA-256` (также в [`downloads/SHA256SUMS.txt`](downloads/SHA256SUMS.txt)):
```text
4eafb81f742bc5416a715dbc5f837e30452ca3c2a716c00e8f67e10188a786f3  Lumina-Setup-1.0.0.exe
e596632ed4664827bb9f1937001605c85f34cd7b1ac4ae0b8adfa20742f4e07b  Lumina-1.0.0.zip
c5a9fca40fc94529c6cf36df384e192e31fadeb1d3d12efc1e4cfa7d7b56b507  Lumina.exe
```

Проверка в PowerShell:
```powershell
Get-FileHash .\Lumina-Setup-1.0.0.exe -Algorithm SHA256
```

---

## Структура репозитория

- [`src/`](src/) — исходный код ядра лаунчера на C# (авторизация, загрузчик файлов, ModNest, скины, серверы, миры, автонастройка графики).
- [`ui/`](ui/) — клиентский интерфейс лаунчера (`HTML5 / CSS3 / Vanilla JS`).
- [`installer/`](installer/) — исходный код нативного графического установщика Windows (`LuminaSetup.cs`, `app.manifest`).
- [`downloads/`](downloads/) — официальные сборки `Lumina-Setup-1.0.0.exe`, `Lumina-1.0.0.zip` и `SHA256SUMS.txt`.
- [`index.html`](index.html), [`assets/`](assets/), [`vercel.json`](vercel.json) — официальный веб-сайт проекта с браузерным `Web Crypto SHA-256` валидатором.
- [`build.bat`](build.bat) — скрипт сборки лаунчера стандартным компилятором Windows (`csc.exe` из `.NET Framework 4.x`).

---

## Лицензия

Проект распространяется под лицензией [MIT License](LICENSE).  
© 2026 **AkeeTech**. Не является официальным продуктом Minecraft и не связан с Mojang Studios или Microsoft.
