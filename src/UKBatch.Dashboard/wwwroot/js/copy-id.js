// Click-to-copy for elements carrying a `data-ukbatch-copy` attribute.
//
// The copy MUST run inside the native click gesture: a Blazor Server `@onclick` round-trips
// through the SignalR circuit first, by which time Safari considers the user activation spent
// and rejects BOTH the async Clipboard API and `document.execCommand('copy')`. A delegated DOM
// listener attached here runs synchronously in the gesture, so both engines accept the write.
//
// The synchronous hidden-textarea path is tried first (it completes inside the gesture on every
// engine and needs no secure context); the async Clipboard API is the fallback for engines that
// reject execCommand. Feedback (copy icon -> check) is applied by this module directly — a later
// Blazor re-render may reset the icon early, which is harmless for a 1.5s transient.
let initialized = false;

export function init() {
  if (initialized) return;
  initialized = true;
  document.addEventListener('click', (e) => {
    const btn = e.target instanceof Element ? e.target.closest('[data-ukbatch-copy]') : null;
    if (!btn) return;
    const text = btn.getAttribute('data-ukbatch-copy');
    if (!text) return;
    if (legacyCopy(text)) {
      showFeedback(btn);
    } else if (window.isSecureContext && navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).then(() => showFeedback(btn), () => { /* nothing copied */ });
    }
  });
}

function legacyCopy(text) {
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.left = '-9999px';
    document.body.appendChild(ta);
    ta.select();
    const ok = document.execCommand('copy');
    document.body.removeChild(ta);
    return ok;
  } catch {
    return false;
  }
}

function showFeedback(btn) {
  const icon = btn.querySelector('.material-symbols-outlined');
  if (!icon) return;
  icon.textContent = 'check';
  btn.classList.add('copyable-id__copy--copied');   // turns the check green (success colour)
  btn.setAttribute('title', 'Copied');
  setTimeout(() => {
    icon.textContent = 'content_copy';
    btn.classList.remove('copyable-id__copy--copied');
    btn.setAttribute('title', 'Copy');
  }, 1500);
}
