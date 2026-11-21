(function () {
  var root = document.documentElement;
  var themeButtons = Array.prototype.slice.call(document.querySelectorAll("[data-theme-choice]"));
  var shotButtons = Array.prototype.slice.call(document.querySelectorAll("[data-shot]"));
  var shots = Array.prototype.slice.call(document.querySelectorAll("img[data-src]"));

  function saved() {
    try { var v = localStorage.getItem("glyphlens-site-theme"); return v === "light" || v === "dark" ? v : "system"; } catch (e) { return "system"; }
  }
  function isDark() {
    var attr = root.getAttribute("data-theme");
    if (attr) { return attr === "dark"; }
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
  }
  function showShots(mode) {
    shots.forEach(function (img) { img.src = "assets/" + mode + "-" + img.getAttribute("data-src") + ".avif"; });
    shotButtons.forEach(function (b) { b.setAttribute("aria-checked", String(b.getAttribute("data-shot") === mode)); });
  }
  function choose(choice) {
    if (choice === "system") { root.removeAttribute("data-theme"); } else { root.setAttribute("data-theme", choice); }
    try {
      if (choice === "system") { localStorage.removeItem("glyphlens-site-theme"); } else { localStorage.setItem("glyphlens-site-theme", choice); }
    } catch (e) { /* not remembered */ }
    themeButtons.forEach(function (b) { b.setAttribute("aria-checked", String(b.getAttribute("data-theme-choice") === choice)); });
    showShots(isDark() ? "dark" : "light");
  }

  themeButtons.forEach(function (b) { b.addEventListener("click", function () { choose(b.getAttribute("data-theme-choice")); }); });
  shotButtons.forEach(function (b) { b.addEventListener("click", function () { showShots(b.getAttribute("data-shot")); }); });
  if (window.matchMedia) {
    var query = window.matchMedia("(prefers-color-scheme: dark)");
    var onChange = function () { if (saved() === "system") { showShots(isDark() ? "dark" : "light"); } };
    if (query.addEventListener) { query.addEventListener("change", onChange); }
  }
  choose(saved());
})();
