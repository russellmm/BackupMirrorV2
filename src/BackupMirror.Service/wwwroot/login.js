'use strict';
document.getElementById('login').addEventListener('submit', async (e) => {
  e.preventDefault();
  const err = document.getElementById('err');
  err.textContent = '';
  try {
    const res = await fetch('/api/login', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-BackupMirror': '1' },
      body: JSON.stringify({ key: document.getElementById('key').value }),
      credentials: 'same-origin',
    });
    if (res.ok) { location.href = '/'; return; }
    const data = await res.json().catch(() => null);
    err.textContent = (data && data.error) || 'Sign-in failed.';
  } catch (ex) {
    err.textContent = 'Cannot reach the service.';
  }
});
