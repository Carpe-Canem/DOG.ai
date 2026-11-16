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

// Settings search: bring the chosen setting into view and flash it. Its category is shown a moment after the click.
export function scrollToSetting(id) {
  const started = Date.now();
  const look = () => {
    const el = document.getElementById(id);
    if (el && el.offsetParent !== null) {
      const box = el.closest('label, .field-block, .label-like, section') ?? el;
      box.scrollIntoView({ block: 'center' });
      box.classList.add('found-flash');
      setTimeout(() => box.classList.remove('found-flash'), 2200);
      return;
    }
    if (Date.now() - started < 3000) setTimeout(look, 100);
  };
  look();
}

// A file dropped on the page would make the browser control try to open it (file:///…); the window refuses that, and
// the refused address, with the file's name, would be noted in the log. So a dropped file does nothing anywhere,
// except where a handler takes it first and stops the event (the chat box: attach.js). Put in place once, when the
// page starts (LinkGuard), so it holds on every screen, not only after a chat box was shown.
export function guardDrops() {
  if (document._dogDrops) return;
  document._dogDrops = true;
  const files = e => !!e.dataTransfer && Array.prototype.indexOf.call(e.dataTransfer.types || [], 'Files') >= 0;
  document.addEventListener('dragover', e => { if (files(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'none'; } });
  document.addEventListener('drop', e => { if (files(e)) e.preventDefault(); });
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

// Ctrl+K anywhere in DOG.ai opens the quick-jump box (JumpBox), and closes it again. While the box is open the
// arrow keys choose a result, so they must not also move the caret in its text field.
let jumpTarget = null;
let jumpCameFrom = null;
export function watchJumpKey(dotnet) {
  jumpTarget = dotnet;
  if (document._dogJumpKey) return;
  document._dogJumpKey = true;
  document.addEventListener('keydown', e => {
    if (!jumpTarget) return;
    if (e.target instanceof Element && e.target.id === 'jump-input' && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
      e.preventDefault();
      return;
    }
    if (!e.ctrlKey || e.altKey || e.shiftKey || e.metaKey || e.repeat) return;
    if (e.key !== 'k' && e.key !== 'K') return;
    e.preventDefault();
    e.stopPropagation();
    jumpTarget.invokeMethodAsync('Toggle');
  }, true);
}

export function unwatchJumpKey(dotnet) {
  if (jumpTarget === dotnet) jumpTarget = null;
}

// A dialog that is open (the picture maker, a confirmation) is answered first: the box does not open over it.
export function jumpAllowed() {
  return !document.querySelector('.modal-back:not(.jump-back)');
}

// Remembers where typing was when the box opened, and puts it back when the box is closed without a choice.
export function jumpOpened(input) {
  if (!(document.activeElement instanceof HTMLElement) || document.activeElement.id !== 'jump-input') jumpCameFrom = document.activeElement;
  input?.focus();
}

export function jumpClosed(restore) {
  const from = jumpCameFrom;
  jumpCameFrom = null;
  if (restore && from instanceof HTMLElement && from.isConnected) from.focus();
}

// Below 1050 px the side panel is a drawer over the page (AsideDrawer). Escape closes it, unless something opened
// over it or inside it (a dialog, a "⋯" menu, the downloads list) is there to take that Escape first; so does the
// window growing past 1050 px, where the side panel sits beside the page again.
let drawerTarget = null;
let drawerCameFrom = null;
export function drawerOpened(dotnet) {
  drawerTarget = dotnet;
  const active = document.activeElement;
  drawerCameFrom = active instanceof HTMLElement && active !== document.body && !active.closest('.aside')
    ? active : document.getElementById('aside-drawer');
  if (!document._dogDrawer) {
    document._dogDrawer = true;
    document.addEventListener('keydown', e => {
      if (!drawerTarget || e.key !== 'Escape' || e.defaultPrevented) return;
      if (document.querySelector('.modal-back, .menu-pop, .tray-pop')) return;
      drawerTarget.invokeMethodAsync('Close');
    });
    matchMedia('(max-width: 1050px)').addEventListener('change', e => {
      if (!e.matches && drawerTarget) drawerTarget.invokeMethodAsync('Close');
    });
  }
  requestAnimationFrame(() => document.getElementById('aside-close')?.focus());
}

export function drawerClosed() {
  drawerTarget = null;
  const from = drawerCameFrom;
  drawerCameFrom = null;
  const active = document.activeElement;
  const lost = !(active instanceof HTMLElement) || active === document.body || !active.isConnected || !!active.closest('.aside');
  if (lost && from instanceof HTMLElement && from.isConnected && from.offsetParent !== null) from.focus();
}

// A dialog (Modal) takes the focus when it opens, so its Escape works at once; unless something inside it already has
// the focus (a search box that focuses itself). Closing puts the focus back on what had it, when the focus was lost
// with the dialog. Dialogs opened from dialogs stack.
const modalStack = [];
export function modalOpened(box) {
  if (!(box instanceof HTMLElement)) return;
  const active = document.activeElement;
  modalStack.push({ box, from: active instanceof HTMLElement && active !== document.body && !box.contains(active) ? active : null });
  requestAnimationFrame(() => {
    if (box.isConnected && !box.contains(document.activeElement)) box.focus({ preventScroll: true });
  });
}

export function modalClosed(box) {
  const i = modalStack.findIndex(m => m.box === box);
  if (i < 0) return;
  const [m] = modalStack.splice(i, 1);
  const active = document.activeElement;
  const lost = !(active instanceof HTMLElement) || active === document.body || !active.isConnected || box.contains(active);
  if (lost && m.from instanceof HTMLElement && m.from.isConnected && m.from.offsetParent !== null) m.from.focus({ preventScroll: true });
}

export function showJumpChoice() {
  document.querySelector('.jump-item.on')?.scrollIntoView({ block: 'nearest' });
}

// A "⋯" menu opens below its button. When that would run past the bottom of the part of the page that scrolls (the
// last card of a list), and there is room above the button, it opens upwards instead.
export function placeMenu(pop) {
  if (!(pop instanceof HTMLElement)) return;
  pop.classList.remove('up');
  let box = pop.parentElement;
  while (box && box !== document.body && !/(auto|scroll)/.test(getComputedStyle(box).overflowY)) box = box.parentElement;
  const edge = box && box !== document.body ? box.getBoundingClientRect() : { top: 0, bottom: innerHeight };
  const bottom = Math.min(edge.bottom, innerHeight);
  const top = Math.max(edge.top, 0);
  const menu = pop.getBoundingClientRect();
  const button = pop.parentElement.getBoundingClientRect();
  if (menu.bottom > bottom - 4 && button.top - menu.height - 4 >= top) pop.classList.add('up');
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
