// Video clips in the page (M20 step 4). A clip is held in C# memory as WebM bytes; to play it the page makes a blob:
// address from those bytes and lets it go again as soon as another clip is shown or the page closes (the page's
// content rules refuse data: addresses for video). Nothing is written to a file.

// Every address made, by its element. A Map (not a WeakMap) so that releaseGone can find addresses whose element left
// the page before it was told to let go (a dialog closed by leaving the page).
const addresses = new Map();

function forget(el) {
  const url = addresses.get(el);
  if (!url) return;
  addresses.delete(el);
  try {
    el.pause();
    el.removeAttribute('src');
    el.load();
  } catch { }
  URL.revokeObjectURL(url);
}

// Shows a clip in a <video> element and starts it (muted: a clip has no sound, and only a muted video may start by itself).
export function play(el, bytes) {
  if (!el) return;
  forget(el);
  const url = URL.createObjectURL(new Blob([bytes], { type: 'video/webm' }));
  addresses.set(el, url);
  el.muted = true;
  el.src = url;
  el.play().catch(() => { });
}

export function pause(el) {
  if (el && !el.paused) el.pause();
}

// Starts a paused clip again (after a clip that was to take its place was cancelled or refused).
export function resume(el) {
  if (el && el.paused && addresses.has(el)) el.play().catch(() => { });
}

// Lets the clip shown in the element go.
export function release(el) {
  if (el) forget(el);
}

// Lets go of every clip whose element is no longer on the page. Called as a component goes; the page may take the
// element away a moment after, so it looks again a second later.
export function releaseGone() {
  const sweep = () => { for (const el of [...addresses.keys()]) if (!el.isConnected) forget(el); };
  sweep();
  setTimeout(sweep, 1000);
}

// The still a clip is listed with: its first frame as a small WebP of the clip's own shape (PicturePreview's rules).
// The canvas writes the picture alone, with no metadata; C# checks that before keeping it.
export async function still(bytes, width, height) {
  const url = URL.createObjectURL(new Blob([bytes], { type: 'video/webm' }));
  const v = document.createElement('video');
  v.muted = true;
  v.preload = 'auto';
  try {
    await new Promise((resolve, reject) => {
      v.onloadeddata = resolve;
      v.onerror = () => reject(new Error('The video could not be read.'));
      v.src = url;
    });
    if (v.currentTime !== 0) {
      await new Promise(resolve => { v.onseeked = resolve; v.currentTime = 0; });
    }
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const g = canvas.getContext('2d', { alpha: false });
    g.imageSmoothingQuality = 'high';
    g.drawImage(v, 0, 0, width, height);
    const blob = await new Promise((resolve, reject) =>
      canvas.toBlob(b => (b ? resolve(b) : reject(new Error('The still could not be made.'))), 'image/webp', 0.82));
    return new Uint8Array(await blob.arrayBuffer());
  } finally {
    v.removeAttribute('src');
    v.load();
    URL.revokeObjectURL(url);
  }
}

// Continue the clip (M21): the clip's last frame as a JPEG at the clip's own size (width and height come from C#, never
// from the element), for the next clip's first frame. The time is set to the clip's length: every frame of the engine's
// files is a key frame of its own, so the seek lands on the last one. lastTime is the last frame's time, used only when
// the file does not say its length. Nothing is written to a file; the address is let go at once.
export async function lastFrame(bytes, width, height, quality, lastTime) {
  const url = URL.createObjectURL(new Blob([bytes], { type: 'video/webm' }));
  const v = document.createElement('video');
  v.muted = true;
  v.preload = 'auto';
  try {
    await new Promise((resolve, reject) => {
      v.onloadeddata = resolve;
      v.onerror = () => reject(new Error('The video could not be read.'));
      v.src = url;
    });
    const end = Number.isFinite(v.duration) && v.duration > 0 ? v.duration : lastTime;
    // A player that is already there gets no "seeked" (a clip of one frame): nothing to wait for then.
    if (Number.isFinite(end) && Math.abs(v.currentTime - end) > 1e-6) {
      await new Promise((resolve, reject) => {
        v.onseeked = resolve;
        v.onerror = () => reject(new Error('The video could not be read.'));
        v.currentTime = end;
      });
    }
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const g = canvas.getContext('2d', { alpha: false });
    g.imageSmoothingQuality = 'high';
    g.drawImage(v, 0, 0, width, height);
    const blob = await new Promise((resolve, reject) =>
      canvas.toBlob(b => (b ? resolve(b) : reject(new Error('The last frame could not be made.'))), 'image/jpeg', quality / 100));
    return new Uint8Array(await blob.arrayBuffer());
  } finally {
    v.removeAttribute('src');
    v.load();
    URL.revokeObjectURL(url);
  }
}
