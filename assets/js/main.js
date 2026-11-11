// Lumina — сайт. Навигация, переключение тем и иконок, появление при прокрутке и интерактивные демо-блоки.
(function () {
  "use strict";

  // 0. Переключение тем оформления (Классика, Зелёный градиент, Индиго, Тёмный градиент) и смена иконки
  var themeLogos = {
    lumina: "assets/img/lumina-512.png",
    emerald: "assets/img/lumina-emerald.png",
    indigo: "assets/img/lumina-indigo.png",
    dark: "assets/img/lumina-dark.png"
  };

  function applyTheme(theme) {
    if (!themeLogos[theme]) theme = "lumina";
    document.documentElement.setAttribute("data-theme", theme);
    try { localStorage.setItem("lumina-theme", theme); } catch (e) {}

    var iconSrc = themeLogos[theme];
    document.querySelectorAll("[data-theme-logo]").forEach(function (img) {
      img.setAttribute("src", iconSrc);
    });
    var fav = document.getElementById("site-favicon");
    var apple = document.getElementById("site-apple-icon");
    if (fav) fav.setAttribute("href", iconSrc);
    if (apple) apple.setAttribute("href", iconSrc);

    document.querySelectorAll("[data-set-theme]").forEach(function (btn) {
      btn.classList.toggle("active", btn.getAttribute("data-set-theme") === theme);
    });
  }

  var initialTheme = document.documentElement.getAttribute("data-theme") || "lumina";
  try {
    var saved = localStorage.getItem("lumina-theme");
    if (saved && themeLogos[saved]) initialTheme = saved;
  } catch (e) {}
  applyTheme(initialTheme);

  document.querySelectorAll("[data-set-theme]").forEach(function (btn) {
    btn.addEventListener("click", function () {
      applyTheme(btn.getAttribute("data-set-theme"));
    });
  });

  // 1. Навбар: фон при прокрутке, мобильное меню и подсветка активного раздела
  var nav = document.getElementById("navbar");
  var toggle = document.getElementById("nav-toggle");
  var navLinks = Array.prototype.slice.call(nav.querySelectorAll(".nav-link[href^='#']"));
  var sections = navLinks.map(function (a) {
    return document.querySelector(a.getAttribute("href"));
  }).filter(Boolean);

  function onScroll() {
    var y = window.scrollY;
    nav.classList.toggle("scrolled", y > 40);
    var activeId = "";
    for (var i = 0; i < sections.length; i++) {
      var rect = sections[i].getBoundingClientRect();
      if (rect.top <= 160 && rect.bottom >= 140) {
        activeId = "#" + sections[i].id;
        break;
      }
    }
    navLinks.forEach(function (a) {
      a.classList.toggle("active", a.getAttribute("href") === activeId);
    });
  }
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  if (toggle) {
    toggle.addEventListener("click", function () {
      var open = nav.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
  }
  nav.querySelectorAll(".navbar-collapse a").forEach(function (a) {
    a.addEventListener("click", function () {
      nav.classList.remove("open");
      if (toggle) toggle.setAttribute("aria-expanded", "false");
    });
  });

  // 2. Появление блоков и счётчики
  function countUp(el) {
    var target = parseInt(el.getAttribute("data-count"), 10);
    if (!target) return;
    var cur = 0;
    var timer = setInterval(function () {
      cur += 1;
      el.textContent = cur;
      if (cur >= target) clearInterval(timer);
    }, Math.max(40, Math.floor(500 / target)));
  }
  var items = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (!e.isIntersecting) return;
        e.target.classList.add("visible");
        e.target.querySelectorAll("[data-count]").forEach(countUp);
        io.unobserve(e.target);
      });
    }, { threshold: 0.15, rootMargin: "0px 0px -40px 0px" });
    items.forEach(function (el) { io.observe(el); });
  } else {
    items.forEach(function (el) { el.classList.add("visible"); });
  }

  // 3. Макет в герое: переключение вкладок, сборок и имитация запуска
  var mockup = document.getElementById("hero-mockup");
  if (mockup) {
    var mButtons = mockup.querySelectorAll("[data-m-view]");
    var mPanels = mockup.querySelectorAll("[data-m-panel]");
    mButtons.forEach(function (btn) {
      btn.addEventListener("click", function () {
        var target = btn.getAttribute("data-m-view");
        mButtons.forEach(function (b) { b.classList.toggle("active", b === btn); });
        mPanels.forEach(function (p) {
          p.classList.toggle("active", p.getAttribute("data-m-panel") === target);
        });
      });
    });

    var heroInstances = [
      { name: "Выживание с друзьями", icon: "assets/img/icons/diamond.svg", sub: "Fabric 1.21.1 · 34 мода · Оптимизация включена" },
      { name: "Тест шейдеров Iris", icon: "assets/img/icons/nether.svg", sub: "Fabric 1.21.4 · Iris + Sodium · 60+ FPS" },
      { name: "Техно-сборка Create", icon: "assets/img/icons/crafting.svg", sub: "NeoForge 1.20.4 · 68 модов · 8 ГБ ОЗУ" },
      { name: "PvP и мини-игры", icon: "assets/img/icons/sword.svg", sub: "Quilt 1.21.1 · Быстрый вход на сервер" }
    ];
    var hIdx = 0;
    var instCycleBtn = document.getElementById("m-instance-cycle");
    var instIcon = document.getElementById("m-inst-icon");
    var instName = document.getElementById("m-inst-name");
    var instSub = document.getElementById("m-sub");
    if (instCycleBtn) {
      instCycleBtn.addEventListener("click", function () {
        hIdx = (hIdx + 1) % heroInstances.length;
        var cur = heroInstances[hIdx];
        instIcon.src = cur.icon;
        instName.textContent = cur.name;
        instSub.textContent = cur.sub;
      });
    }
  }

  var bar = document.getElementById("m-bar");
  var label = document.getElementById("m-label");
  var text = document.getElementById("m-text");
  var playBtn = document.getElementById("m-play-btn");
  var stages = [
    { name: "Java 21", total: 210, unit: "файлов" },
    { name: "Ресурсы игры", total: 3900, unit: "файлов" },
    { name: "Fabric и моды", total: 34, unit: "модов" }
  ];
  var s = 0, p = 0, tickTimer = null;
  function tick() {
    var st = stages[s];
    p = Math.min(p + 0.04 + Math.random() * 0.05, 1);
    bar.style.width = (p * 100).toFixed(1) + "%";
    label.textContent = st.name;
    text.textContent = Math.round(st.total * p) + " / " + st.total + " " + st.unit + " · " + (14 + Math.round(Math.random() * 14)) + " МБ/с";
    if (p >= 1) {
      s = (s + 1) % stages.length;
      p = 0;
      if (s === 0) {
        label.textContent = "Готово";
        text.textContent = "Игра запущена";
        if (playBtn) playBtn.textContent = "В игре";
        tickTimer = setTimeout(function () {
          if (playBtn) playBtn.textContent = "Играть";
          tick();
        }, 2400);
        return;
      }
      tickTimer = setTimeout(tick, 450);
      return;
    }
    tickTimer = setTimeout(tick, 150);
  }
  if (bar) tickTimer = setTimeout(tick, 800);
  if (playBtn && bar) {
    playBtn.addEventListener("click", function () {
      clearTimeout(tickTimer);
      s = 0;
      p = 0;
      playBtn.textContent = "Запуск…";
      tick();
    });
  }

  // 4. Секция «Инсталляции»: интерактивный выбор иконки и загрузчика
  var iconsBoard = document.getElementById("icons-board");
  var loaderPills = document.getElementById("loader-pills");
  var ipIcon = document.getElementById("ip-icon");
  var ipTitle = document.getElementById("ip-title");
  var ipLoader = document.getElementById("ip-loader");

  if (iconsBoard && ipIcon && ipTitle) {
    iconsBoard.addEventListener("click", function (e) {
      var tile = e.target.closest(".tile");
      if (!tile) return;
      iconsBoard.querySelectorAll(".tile").forEach(function (t) { t.classList.toggle("active", t === tile); });
      ipIcon.src = tile.getAttribute("data-icon");
      ipTitle.textContent = tile.getAttribute("data-title");
    });
  }
  if (loaderPills && ipLoader) {
    loaderPills.addEventListener("click", function (e) {
      var pill = e.target.closest(".loader-pill");
      if (!pill) return;
      loaderPills.querySelectorAll(".loader-pill").forEach(function (p) { p.classList.toggle("active", p === pill); });
      ipLoader.textContent = pill.getAttribute("data-loader");
    });
  }

  // 5. Каталог ModNest: категории, живой фильтр и установка
  var catalogData = {
    mods: [
      { letter: "S", bg: "linear-gradient(135deg,#2dce89,#11cdef)", name: "Sodium", desc: "Современный движок рендера · 60 млн загрузок", installed: true },
      { letter: "I", bg: "linear-gradient(135deg,#5b2a9c,#f0607a)", name: "Iris Shaders", desc: "Шейдеры вместе с Sodium · 40 млн загрузок", installed: false },
      { letter: "L", bg: "linear-gradient(135deg,#f0607a,#f7a35c)", name: "Lithium", desc: "Оптимизация логики и физики · 30 млн загрузок", installed: true },
      { letter: "J", bg: "linear-gradient(135deg,#172b4d,#6f3fd6)", name: "Just Enough Items", desc: "Рецепты крафта и предметы · 200 млн загрузок", installed: false }
    ],
    resourcepacks: [
      { letter: "F", bg: "linear-gradient(135deg,#34d399,#059669)", name: "Faithful 64x", desc: "Чёткие текстуры в стиле оригинала · 18 млн загрузок", installed: true },
      { letter: "A", bg: "linear-gradient(135deg,#f7a35c,#f0607a)", name: "Fresh Animations", desc: "Плавные живые анимации мобов · 25 млн загрузок", installed: false },
      { letter: "D", bg: "linear-gradient(135deg,#6366f1,#8b5cf6)", name: "Default Dark Mode", desc: "Тёмный интерфейс инвентаря и меню · 9 млн загрузок", installed: false },
      { letter: "B", bg: "linear-gradient(135deg,#ec4899,#8b5cf6)", name: "Better Leaves", desc: "Пышная объёмная листва деревьев · 11 млн загрузок", installed: false }
    ],
    shaders: [
      { letter: "C", bg: "linear-gradient(135deg,#5b2a9c,#f7a35c)", name: "Complementary Reimagined", desc: "Атмосферный свет и отражения · 35 млн загрузок", installed: true },
      { letter: "B", bg: "linear-gradient(135deg,#0ea5e9,#6366f1)", name: "BSL Shaders", desc: "Мягкое киношное освещение и вода · 28 млн загрузок", installed: false },
      { letter: "M", bg: "linear-gradient(135deg,#10b981,#3b82f6)", name: "MakeUp — Ultra Fast", desc: "Красивый шейдер для слабых ПК · 14 млн загрузок", installed: false },
      { letter: "S", bg: "linear-gradient(135deg,#f43f5e,#fb923c)", name: "Solas Shader", desc: "Объёмные облака и цветное свечение · 6 млн загрузок", installed: false }
    ],
    datapacks: [
      { letter: "T", bg: "linear-gradient(135deg,#14b8a6,#0f766e)", name: "Terralith", desc: "Более 95 новых биомов верхнего мира · 16 млн загрузок", installed: false },
      { letter: "N", bg: "linear-gradient(135deg,#7c3aed,#db2777)", name: "Nullscape", desc: "Преображение измерения Энда · 8 млн загрузок", installed: false },
      { letter: "I", bg: "linear-gradient(135deg,#ea580c,#dc2626)", name: "Incendium", desc: "Новые биомы и крепости Незера · 12 млн загрузок", installed: true },
      { letter: "D", bg: "linear-gradient(135deg,#3b82f6,#1d4ed8)", name: "Dungeons and Taverns", desc: "Сотни новых данжей и построек · 7 млн загрузок", installed: false }
    ],
    modpacks: [
      { letter: "F", bg: "linear-gradient(135deg,#8b5cf6,#ec4899)", name: "Fabulously Optimized", desc: "Максимальный FPS и функции OptiFine · 15 млн загрузок", installed: true },
      { letter: "C", bg: "linear-gradient(135deg,#f59e0b,#ef4444)", name: "Cobblemon Official", desc: "Приключения и покемоны в мире Minecraft · 9 млн загрузок", installed: false },
      { letter: "P", bg: "linear-gradient(135deg,#06b6d4,#3b82f6)", name: "Prominence II RPG", desc: "Боссы, магия, прокачка и квесты · 6 млн загрузок", installed: false },
      { letter: "A", bg: "linear-gradient(135deg,#10b981,#047857)", name: "All of Fabric", desc: "Большая техно-магическая сборка · 5 млн загрузок", installed: false }
    ]
  };

  var currentCat = "mods";
  var catalogList = document.getElementById("catalog-list");
  var catalogFilter = document.getElementById("catalog-filter");
  var modnestDemo = document.getElementById("modnest-demo");

  function renderCatalog() {
    if (!catalogList) return;
    var q = (catalogFilter ? catalogFilter.value : "").trim().toLowerCase();
    var rows = (catalogData[currentCat] || []).filter(function (item) {
      return !q || item.name.toLowerCase().indexOf(q) !== -1 || item.desc.toLowerCase().indexOf(q) !== -1;
    });
    if (!rows.length) {
      catalogList.innerHTML = '<li class="catalog-empty">По запросу «' + q.replace(/[<>&"]/g, "") + '» ничего не найдено</li>';
      return;
    }
    catalogList.innerHTML = rows.map(function (item) {
      var btnClass = item.installed ? "btn btn-sm btn-installed" : "btn btn-sm btn-primary";
      var btnText = item.installed ? "Установлен" : "Установить";
      return (
        '<li class="catalog-item">' +
          '<span class="ci-icon" style="background:' + item.bg + '">' + item.letter + '</span>' +
          '<span class="ci-text"><strong>' + item.name + '</strong><span>' + item.desc + '</span></span>' +
          '<button type="button" class="' + btnClass + '" data-item-name="' + item.name + '">' + btnText + '</button>' +
        '</li>'
      );
    }).join("");
  }

  if (modnestDemo) {
    modnestDemo.querySelectorAll(".catalog-tab").forEach(function (tab) {
      tab.addEventListener("click", function () {
        currentCat = tab.getAttribute("data-cat") || "mods";
        modnestDemo.querySelectorAll(".catalog-tab").forEach(function (t) { t.classList.toggle("active", t === tab); });
        renderCatalog();
      });
    });
    if (catalogFilter) {
      catalogFilter.addEventListener("input", renderCatalog);
    }
    if (catalogList) {
      catalogList.addEventListener("click", function (e) {
        var btn = e.target.closest("button[data-item-name]");
        if (!btn) return;
        var name = btn.getAttribute("data-item-name");
        var list = catalogData[currentCat] || [];
        var entry = null;
        for (var i = 0; i < list.length; i++) {
          if (list[i].name === name) { entry = list[i]; break; }
        }
        if (!entry) return;
        if (!entry.installed) {
          btn.textContent = "Установка…";
          btn.disabled = true;
          setTimeout(function () {
            entry.installed = true;
            renderCatalog();
          }, 320);
        } else {
          entry.installed = false;
          renderCatalog();
        }
      });
    }
    renderCatalog();
  }

  // 6. Стенд автонастройки графики
  var gfxShowcase = document.getElementById("gfx-showcase");
  var gfxPresets = {
    fps: {
      chunks: "10 / 6 чанков",
      chunksSub: "Без просадок при генерации мира",
      light: "Минимум · Быстрые",
      lightSub: "Отключены тяжёлые тени сущностей",
      sodium: "Culling + Deferral ВКЛ",
      sodiumSub: "Отсечение невидимых граней и чанков",
      fps: "до +140% FPS",
      fpsSub: "Идеально для 144+ Гц и ноутбуков"
    },
    balance: {
      chunks: "14 / 10 чанков",
      chunksSub: "Оптимальная дальность обзора",
      light: "Мягкое · Детальные",
      lightSub: "Плавный свет без потери отклика",
      sodium: "Умный буфер чанков",
      sodiumSub: "Быстрая подгрузка без фризов",
      fps: "до +85% FPS",
      fpsSub: "Красивая картинка при стабильных кадрах"
    },
    quality: {
      chunks: "20 / 12 чанков",
      chunksSub: "Дальний горизонт и живой мир",
      light: "Максимум · Красивые",
      lightSub: "Полные тени, частицы и биом-переходы",
      sodium: "Iris + Sodium Ultra",
      sodiumSub: "Готовность к шейдерам в высоком разрешении",
      fps: "Стабильные 60+ FPS",
      fpsSub: "Максимум красоты без лагов ванильного движка"
    }
  };

  if (gfxShowcase) {
    gfxShowcase.querySelectorAll(".gfx-preset-btn").forEach(function (btn) {
      btn.addEventListener("click", function () {
        var key = btn.getAttribute("data-preset");
        var data = gfxPresets[key];
        if (!data) return;
        gfxShowcase.querySelectorAll(".gfx-preset-btn").forEach(function (b) { b.classList.toggle("active", b === btn); });
        document.getElementById("gfx-val-chunks").textContent = data.chunks;
        document.getElementById("gfx-sub-chunks").textContent = data.chunksSub;
        document.getElementById("gfx-val-light").textContent = data.light;
        document.getElementById("gfx-sub-light").textContent = data.lightSub;
        document.getElementById("gfx-val-sodium").textContent = data.sodium;
        document.getElementById("gfx-sub-sodium").textContent = data.sodiumSub;
        document.getElementById("gfx-val-fps").textContent = data.fps;
        document.getElementById("gfx-sub-fps").textContent = data.fpsSub;
      });
    });
  }

  var y = document.getElementById("year");
  if (y) y.textContent = new Date().getFullYear();

  document.querySelectorAll(".copy-hash-btn").forEach(function (btn) {
    btn.addEventListener("click", function () {
      var targetId = btn.getAttribute("data-copy-target");
      var el = targetId ? document.getElementById(targetId) : null;
      if (!el || !navigator.clipboard) return;
      navigator.clipboard.writeText(el.textContent.trim()).then(function () {
        var orig = btn.textContent;
        btn.textContent = "Скопировано ✓";
        setTimeout(function () { btn.textContent = orig; }, 1800);
      });
    });
  });
})();
