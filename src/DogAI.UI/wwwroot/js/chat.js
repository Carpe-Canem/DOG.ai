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
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
    e.preventDefault();
    dotnet.invokeMethodAsync('SubmitFromKeyboard');
  });
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

// Scrolls to the end if the reader is already there (or always, when forced).
export function followThread(force) {
  const el = scroller();
  if (el && (force || el._dogPinned)) {
    el.scrollTop = el.scrollHeight;
    el._dogPinned = true;
  }
}
