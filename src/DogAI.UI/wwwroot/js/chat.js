// Small helpers the chat screen cannot do from C#: Enter-to-send, a growing text box,
// and keeping the thread scrolled to the newest text while a reply streams.

export function initComposer(textarea, dotnet) {
  if (!textarea || textarea._dogReady) return;
  textarea._dogReady = true;
  const resize = () => {
    textarea.style.height = 'auto';
    textarea.style.height = Math.min(textarea.scrollHeight, 220) + 'px';
  };
  textarea._dogResize = resize;
  textarea.addEventListener('input', resize);
  textarea.addEventListener('keydown', e => {
    // Hold Ctrl+Space to talk; letting go writes down what was said.
    if (e.code === 'Space' && e.ctrlKey) {
      e.preventDefault();
      if (!e.repeat && !textarea._dogTalking) {
        textarea._dogTalking = true;
        dotnet.invokeMethodAsync('PushToTalk', true);
      }
      return;
    }
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
    e.preventDefault();
    dotnet.invokeMethodAsync('SubmitFromKeyboard');
  });
  const release = e => {
    if (!textarea._dogTalking || (e.type === 'keyup' && e.code !== 'Space' && e.key !== 'Control')) return;
    textarea._dogTalking = false;
    dotnet.invokeMethodAsync('PushToTalk', false);
  };
  textarea.addEventListener('keyup', release);
  textarea.addEventListener('blur', release);
}

export function resizeComposer(textarea) {
  if (textarea && textarea._dogResize) textarea._dogResize();
}

function scroller() {
  const el = document.querySelector('.shell .main');
  if (el && !el._dogWatched) {
    el._dogWatched = true;
    el._dogPinned = true;
    el.addEventListener('scroll', () => {
      el._dogPinned = el.scrollHeight - el.scrollTop - el.clientHeight < 80;
    });
  }
  return el;
}

// Citation buttons ([2]) are drawn inside model text, where Blazor cannot attach handlers, so one listener on the page
// passes clicks to the thread on screen: the reply's id (data-msg) and the source number (data-cite).
let citeTarget = null;
export function watchCitations(dotnet) {
  citeTarget = dotnet;
  if (document._dogCites) return;
  document._dogCites = true;
  document.addEventListener('click', e => {
    const button = e.target instanceof Element ? e.target.closest('button.cite') : null;
    const message = button ? button.closest('[data-msg]') : null;
    if (!message || !citeTarget) return;
    e.preventDefault();
    citeTarget.invokeMethodAsync('OpenCitation', Number(message.dataset.msg), Number(button.dataset.cite));
  });
}

export function unwatchCitations(dotnet) {
  if (citeTarget === dotnet) citeTarget = null;
}

// Scrolls to the end if the reader is already there (or always, when forced).
export function followThread(force) {
  const el = scroller();
  if (el && (force || el._dogPinned)) {
    el.scrollTop = el.scrollHeight;
    el._dogPinned = true;
  }
}

// After a search result opens a chat, bring the message that matched into view and flash it. The chat page loads
// a moment after the navigation, so keep looking for a couple of seconds.
export function scrollToMessage(id) {
  const started = Date.now();
  const look = () => {
    const el = document.querySelector(`[data-mid="${id}"], .passage[data-id="${id}"]`);
    if (el) {
      el.scrollIntoView({ block: 'center' });
      el.classList.add('found-flash');
      setTimeout(() => el.classList.remove('found-flash'), 2200);
      return;
    }
    if (Date.now() - started < 3000) setTimeout(look, 120);
  };
  look();
}

// Web links in replies and model cards never open on their own: one listener on the page hands the click to the
// confirmation prompt (LinkGuard), which asks before opening the user's browser.
let linkTarget = null;
export function watchLinks(dotnet) {
  linkTarget = dotnet;
  if (document._dogLinks) return;
  document._dogLinks = true;
  document.addEventListener('click', e => {
    const a = e.target instanceof Element ? e.target.closest('a[href]') : null;
    if (!a || !linkTarget) return;
    const href = a.getAttribute('href') || '';
    if (!/^(https?:|mailto:)/i.test(href)) return;
    e.preventDefault();
    e.stopPropagation();
    linkTarget.invokeMethodAsync('Ask', a.href);
  }, true);
}

export function unwatchLinks(dotnet) {
  if (linkTarget === dotnet) linkTarget = null;
}

// Ctrl+L anywhere in DOG.ai locks it (only when the data lock is on; the C# side decides).
let lockTarget = null;
export function watchLockKey(dotnet) {
  lockTarget = dotnet;
  if (document._dogLockKey) return;
  document._dogLockKey = true;
  document.addEventListener('keydown', e => {
    if (!lockTarget || !e.ctrlKey || e.altKey || e.shiftKey || e.metaKey || e.repeat) return;
    if (e.key !== 'l' && e.key !== 'L') return;
    e.preventDefault();
    e.stopPropagation();
    lockTarget.invokeMethodAsync('LockNow');
  }, true);
}

export function unwatchLockKey(dotnet) {
  if (lockTarget === dotnet) lockTarget = null;
}

// Prints one element only (the recovery key): everything else is hidden while the print dialog is open.
export function printOnly(selector) {
  const target = document.querySelector(selector);
  if (!target) return;
  const path = [];
  for (let e = target.parentElement; e && e !== document.body; e = e.parentElement) { e.classList.add('print-path'); path.push(e); }
  target.classList.add('printing');
  document.documentElement.classList.add('print-one');
  const done = () => {
    target.classList.remove('printing');
    path.forEach(e => e.classList.remove('print-path'));
    document.documentElement.classList.remove('print-one');
    window.removeEventListener('afterprint', done);
  };
  window.addEventListener('afterprint', done);
  window.print();
}
