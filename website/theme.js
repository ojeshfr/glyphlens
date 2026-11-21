// Applies the saved appearance before the page paints (no flash). "system" sets no attribute: the page then follows the OS.
(function () {
  try {
    var saved = localStorage.getItem("glyphlens-site-theme");
    if (saved === "light" || saved === "dark") {
      document.documentElement.setAttribute("data-theme", saved);
    }
  } catch (e) { /* storage blocked: follow the system */ }
})();
