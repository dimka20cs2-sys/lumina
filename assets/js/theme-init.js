(function () {
  "use strict";
  try {
    var t = localStorage.getItem("lumina-theme");
    if (t === "emerald" || t === "indigo" || t === "dark" || t === "lumina") {
      document.documentElement.setAttribute("data-theme", t);
    }
  } catch (e) {}
})();
