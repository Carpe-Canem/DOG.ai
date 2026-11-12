// Dragging the square in the avatar cropper. Blazor owns the square's size and position;
// this only moves it while the pointer is down and reports where it ended up.

export function initCrop(square, image, dotnet) {
  if (!square || square._dogCrop) return;
  square._dogCrop = true;
  let start = null;
  const clamp = (v, lo, hi) => Math.min(Math.max(v, lo), hi);

  square.addEventListener('pointerdown', e => {
    const r = image.getBoundingClientRect();
    start = { x: e.clientX, y: e.clientY, left: square.offsetLeft, top: square.offsetTop, w: r.width, h: r.height };
    try { square.setPointerCapture(e.pointerId); } catch { /* not a real pointer; dragging still works inside the square */ }
    e.preventDefault();
  });
  square.addEventListener('pointermove', e => {
    if (!start) return;
    const left = clamp(start.left + e.clientX - start.x, 0, start.w - square.offsetWidth);
    const top = clamp(start.top + e.clientY - start.y, 0, start.h - square.offsetHeight);
    square.style.left = (left / start.w * 100) + '%';
    square.style.top = (top / start.h * 100) + '%';
  });
  const end = () => {
    if (!start) return;
    const fx = square.offsetLeft / start.w, fy = square.offsetTop / start.h;
    start = null;
    dotnet.invokeMethodAsync('Moved', fx, fy);
  };
  square.addEventListener('pointerup', end);
  square.addEventListener('pointercancel', end);
}
