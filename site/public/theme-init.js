// Applies the remembered appearance (System, Light or Dark) before the first paint, so the page never flashes the wrong theme.
// Kept as an external file because the Content-Security-Policy forbids inline scripts.
(function () {
  var root = document.documentElement;
  var choice = "system";
  try {
    var saved = window.localStorage.getItem("glyphlens-site-theme");
    if (saved === "light" || saved === "dark") choice = saved;
  } catch {
    // Storage blocked: follow the system.
  }
  var prefersDark = !!(window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
  var dark = choice === "dark" || (choice === "system" && prefersDark);
  root.classList.remove(dark ? "light" : "dark");
  root.classList.add(dark ? "dark" : "light");
  root.setAttribute("data-theme-choice", choice);
})();
