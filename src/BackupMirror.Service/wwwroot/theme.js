// Applied before first paint so the page never flashes the wrong theme.
(function () {
  var choice = 'system';
  try { choice = localStorage.getItem('bm-theme') || 'system'; } catch (e) { /* storage blocked */ }
  if (choice === 'dark' || choice === 'light') document.documentElement.setAttribute('data-theme', choice);
})();
