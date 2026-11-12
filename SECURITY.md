# Политика безопасности и архитектура защиты Lumina (Security Architecture)

**Lumina** (`v1.0.0`) — десктопный лаунчер Minecraft: Java Edition для Windows. Проект разработан по принципу **Zero Telemetry & Defense-in-Depth**: минимальная поверхность атаки, отсутствие сторонних сборок и аналитики, криптографическая верификация каждого скачиваемого файла и защита учётных данных на уровне ядра Windows.

---

## 1. Контрольные суммы официального релиза `v1.0.0` (SHA-256)

Перед установкой или отправкой файлов на проверку в антивирусные лаборатории (Microsoft Defender SmartScreen / VirusTotal) сверьте контрольные суммы `SHA-256`:

| Артефакт | Назначение | `SHA-256` |
| :--- | :--- | :--- |
| `Lumina-Setup-Full-1.0.0.exe` | Полный офлайн-установщик Windows (`426 МБ`: Лаунчер + Java 17/21/25 + WebView2 Runtime) | `99184d2c46662984c4bd097724348802b3f511962e7a17a2a5d46a010e2b6708` |
| `Lumina-Setup-1.0.0.exe` | Лёгкий графический установщик Windows (`1.7 МБ`, `asInvoker`, без запроса UAC) | `4eafb81f742bc5416a715dbc5f837e30452ca3c2a716c00e8f67e10188a786f3` |
| `Lumina-1.0.0.zip` | Portable-архив лаунчера (без установки) | `e596632ed4664827bb9f1937001605c85f34cd7b1ac4ae0b8adfa20742f4e07b` |
| `Lumina.exe` | Основной исполняемый файл лаунчера | `c5a9fca40fc94529c6cf36df384e192e31fadeb1d3d12efc1e4cfa7d7b56b507` |

Команда для проверки в PowerShell:
```powershell
Get-FileHash .\Lumina-Setup-1.0.0.exe -Algorithm SHA256
```

---

## 2. Защита учётной записи Microsoft (OAuth 2.0 + Windows DPAPI)

1. **Никакого ввода пароля в лаунчере (`src/MicrosoftAuth.cs`)**:
   - Используется официальный протокол **OAuth 2.0 Device Authorization Grant** (`https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode`).
   - Пользователь вводит пароль исключительно в своём системном браузере на домене Microsoft. Лаунчер никогда не имеет доступа к логину, паролю или кодам двухфакторной аутентификации.
2. **Аппаратно-привязанное шифрование токенов (`src/AccountStore.cs`)**:
   - Токены обновления и сессии Minecraft сохраняются в `%APPDATA%\Lumina\accounts.dat` в зашифрованном виде через системный криптопровайдер **Windows DPAPI** (`ProtectedData.Protect` с областью `DataProtectionScope.CurrentUser` и дополнительной энтропией `"Lumina/accounts/v1"`).
   - Даже если злоумышленник скопирует файл `accounts.dat`, расшифровать его на другом компьютере или под другим пользователем Windows криптографически невозможно.
3. **Изоляция токенов от UI (`AccountStore.Public`)**:
   - В слой интерфейса (`WebView2`) передаются только публичные метаданные профиля (`id`, `username`, `uuid`, `active`). Секретные токены (`accessToken`, `refreshToken`) никогда не покидают C#-процесс.

---

## 3. Проверка целостности загрузок и сетевой белый список

1. **Криптографический контроль всех файлов (`src/Downloader.cs`, `src/ModNest.cs`)**:
   - Все файлы игры, библиотеки, ресурсы и компоненты Java Runtime от Mojang проверяются по **SHA-1** и размеру файла до перемещения из временного `.part`-файла.
   - Все моды, шейдеры, ресурспаки, датапаки и файлы сборок `.mrpack` из каталога Modrinth проверяются по **SHA-512** (и **SHA-1**). При несовпадении хеша файл немедленно удаляется.
   - Установщик `Lumina-Setup-1.0.0.exe` при распаковке автоматически вычисляет `SHA-256` извлечённого `Lumina.exe` и сверяет его с эталонным значением `c5a9fca40fc94529c6cf36df384e192e31fadeb1d3d12efc1e4cfa7d7b56b507`.
2. **Принудительный TLS 1.2 / TLS 1.3 (`src/Http.cs`)**:
   - Устаревшие протоколы SSLv3, TLS 1.0 и TLS 1.1 отключены.
3. **Жёсткий белый список доменов (`AllowedHosts` и `PackHosts`)**:
   - Лаунчер разрешает сетевые загрузки исключительно со следующих официальных доменов:
     - `login.microsoftonline.com`, `user.auth.xboxlive.com`, `xsts.auth.xboxlive.com`, `api.minecraftservices.com`
     - `piston-meta.mojang.com`, `piston-data.mojang.com`, `launchermeta.mojang.com`, `launcher.mojang.com`, `libraries.minecraft.net`, `resources.download.minecraft.net`, `textures.minecraft.net`
     - `meta.fabricmc.net`, `maven.fabricmc.net`, `meta.quiltmc.org`, `maven.quiltmc.org`, `maven.neoforged.net`, `maven.minecraftforge.net`
     - `api.modrinth.com`, `cdn.modrinth.com`

---

## 4. Защита файловой системы и изоляция WebView2

- **Защита от Zip-Slip и Path Traversal (`src/ModNest.cs`, `src/HostCommands.cs`, `src/WorldManager.cs`)**:
  - Все пути внутри архивов `.mrpack`, `.zip` и папок инсталляций проходят каноническую проверку `Path.GetFullPath` (`SafeContentPath`, `SafeInside`). Любая попытка записи за пределы целевого каталога (`..`, абсолютные пути, симлинки) блокируется.
  - При установке модпаков `.mrpack` внешние ссылки проверяются по белому списку `PackHosts`, а файлы внутри папки `overrides/` с исполняемыми расширениями блокируются.
- **Песочница интерфейса (`src/AppWindow.cs`, `ui/index.html`)**:
  - Интерфейс работает исключительно с локального виртуального хоста `https://app.local/`.
  - Любая навигация WebView2 на внешние URL и открытие новых окон перехватываются и блокируются (`NavigationStarting`, `NewWindowRequested`).
  - Включена строгая политика `Content-Security-Policy` (запрет внешних скриптов, `object-src 'none'`, `frame-ancestors 'none'`).
- **Безопасное удаление в Корзину Windows (`src/RecycleBin.cs`)**:
  - При удалении модов, миров, резервных копий или скриншотов файлы перемещаются в системную Корзину Windows (`SHFileOperation` с `FOF_ALLOWUNDO`), исключая случайную безвозвратную потерю данных.

---

## 5. Сообщение об уязвимостях (Responsible Disclosure)

Если вы обнаружили проблему безопасности в лаунчере или на сайте Lumina, пожалуйста, создайте обращение через:
- https://github.com/dimka20cs2-sys/lumina/issues
