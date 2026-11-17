// Pictures put on a chat message (M17 step 4): the attach button, paste and drop. A file from outside is opened only
// here, by the browser control: it is reduced to the longest side given and written again as a JPEG, white behind
// anything see-through, so what DOG.ai keeps is the picture alone (no camera, place or date). Nothing is written to a
// file. What C# receives is that JPEG, which Core checks (AttachedPictures.Problem) before keeping it.

import { guardDrops } from './chat.js';

// The kinds the page opens, told by the file's first bytes (never by its name or type): PNG, JPEG, GIF, BMP, WebP.
// Anything else is refused, SVG included.
function kindOf(b) {
  if (b.length >= 8 && b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4e && b[3] === 0x47 && b[4] === 0x0d && b[5] === 0x0a && b[6] === 0x1a && b[7] === 0x0a) return 'png';
  if (b.length >= 3 && b[0] === 0xff && b[1] === 0xd8 && b[2] === 0xff) return 'jpeg';
  if (b.length >= 6 && b[0] === 0x47 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x38 && (b[4] === 0x37 || b[4] === 0x39) && b[5] === 0x61) return 'gif';
  if (b.length >= 2 && b[0] === 0x42 && b[1] === 0x4d) return 'bmp';
  if (b.length >= 12 && b[0] === 0x52 && b[1] === 0x49 && b[2] === 0x46 && b[3] === 0x46 && b[8] === 0x57 && b[9] === 0x45 && b[10] === 0x42 && b[11] === 0x50) return 'webp';
  return null;
}

// One file → the JPEG bytes, or null when it is not a picture the page takes.
async function toJpeg(file, maxSide, quality, maxBytes) {
  if (!(file instanceof Blob) || file.size < 12 || file.size > maxBytes) return null;
  const head = new Uint8Array(await file.slice(0, 12).arrayBuffer());
  if (!kindOf(head)) return null;
  let bitmap;
  try {
    // A phone's photo is turned upright here, since its rotation note is not kept. A GIF gives its first frame.
    bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
  } catch {
    return null;
  }
  try {
    if (!bitmap.width || !bitmap.height) return null;
    const scale = Math.min(1, maxSide / Math.max(bitmap.width, bitmap.height));
    const width = Math.min(maxSide, Math.max(1, Math.round(bitmap.width * scale)));
    const height = Math.min(maxSide, Math.max(1, Math.round(bitmap.height * scale)));
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const g = canvas.getContext('2d', { alpha: false });
    // A JPEG has no see-through parts: without white first they would come out black.
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, width, height);
    g.imageSmoothingEnabled = true;
    g.imageSmoothingQuality = 'high';
    g.drawImage(bitmap, 0, 0, width, height);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality / 100));
    return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
  } finally {
    bitmap.close();
  }
}

function hasFiles(e) {
  const types = e.dataTransfer && e.dataTransfer.types;
  return !!types && Array.prototype.indexOf.call(types, 'Files') >= 0;
}

// A file dropped anywhere else on the page does nothing: chat.js guardDrops, put in place when DOG.ai's page starts
// (LinkGuard), so it holds on every screen. The chat box's own handlers below stop the event before it gets there.

// root: the chat box with its pictures row (drop target); textarea: where pasting happens; input: the hidden file input.
export function watchPictures(root, textarea, input, dotnet, maxSide, quality, maxBytes) {
  guardDrops();
  if (!root || root._dogPictures) return;
  const state = { dotnet, busy: false, queue: [] };
  root._dogPictures = state;

  const take = async files => {
    const list = Array.from(files || []).filter(f => f instanceof Blob);
    if (!list.length) return;
    state.queue.push(list);
    if (state.busy) return;
    state.busy = true;
    try {
      while (state.queue.length) {
        const batch = state.queue.shift();
        // C# says how many more the message takes; zero when the model cannot see (it says so itself).
        const room = await state.dotnet.invokeMethodAsync('PicturesOffered', batch.length);
        if (room <= 0) continue;
        let refused = 0, added = 0, left = 0;
        try {
          for (const file of batch) {
            if (added >= room) { left++; continue; }
            let jpeg = null;
            try { jpeg = await toJpeg(file, maxSide, quality, maxBytes); } catch { jpeg = null; }
            if (!jpeg) { refused++; continue; }
            // A picture C# could not take counts as refused; the rest of the batch still goes.
            let ok = false;
            try { ok = await state.dotnet.invokeMethodAsync('PictureArrived', jpeg); } catch { ok = false; }
            if (ok) added++; else refused++;
          }
        } finally {
          // Always said, or the box would wait for this batch for good (Send stays off while pictures are made ready).
          await state.dotnet.invokeMethodAsync('PicturesDone', refused, left);
        }
      }
    } catch {
      // The chat box went away while a picture was being made.
    } finally {
      state.busy = false;
    }
  };

  textarea.addEventListener('paste', e => {
    const data = e.clipboardData;
    if (!data || !data.files || !data.files.length) return;
    // Text copied from a document often comes with a picture of itself: the text wins.
    const text = data.getData('text/plain');
    if (text && text.trim().length) return;
    e.preventDefault();
    take(data.files);
  });

  root.addEventListener('dragover', e => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    e.dataTransfer.dropEffect = 'copy';
    root.classList.add('dropping');
  });
  root.addEventListener('dragleave', e => {
    if (!root.contains(e.relatedTarget)) root.classList.remove('dropping');
  });
  root.addEventListener('drop', e => {
    root.classList.remove('dropping');
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    take(e.dataTransfer.files);
  });

  if (input) {
    input.addEventListener('change', () => {
      const files = Array.from(input.files || []);
      input.value = '';
      take(files);
    });
  }
}

export function choosePictures(input) {
  if (input) input.click();
}

// "Write it for me… › From a picture" (M17 step 5). A picture DOG.ai already holds (a try or a kept picture, WebP)
// arrives as its bytes from C#, never through an address, and becomes the same JPEG a chat picture is.
export function bytesToJpeg(bytes, maxSide, quality, maxBytes) {
  return toJpeg(new Blob([bytes]), maxSide, quality, maxBytes);
}

// A file chosen in Windows' Open dialog (the hidden input), as that JPEG. Resolves with the bytes, no bytes (an empty
// array) for a file that is not a picture the page takes, and null when nothing was chosen.
export function pickPicture(input, maxSide, quality, maxBytes) {
  guardDrops();
  return new Promise(resolve => {
    if (!input) { resolve(null); return; }
    const done = async () => {
      input.removeEventListener('change', done);
      input.removeEventListener('cancel', cancelled);
      const file = input.files && input.files[0];
      input.value = '';
      if (!file) { resolve(null); return; }
      let jpeg = null;
      try { jpeg = await toJpeg(file, maxSide, quality, maxBytes); } catch { jpeg = null; }
      resolve(jpeg || new Uint8Array(0));
    };
    const cancelled = () => {
      input.removeEventListener('change', done);
      input.removeEventListener('cancel', cancelled);
      resolve(null);
    };
    input.addEventListener('change', done);
    input.addEventListener('cancel', cancelled);
    input.click();
  });
}

// A first frame for a video (M20 step 4). Two calls, so the cut is decided in C# (VideoFrames.Cut, one tested rule):
// openFrame / openFrameBytes decode the picture and hold it here, in memory, and answer its size; cutFrame cuts the
// part C# chose, writes it at the size given as a JPEG (white behind anything see-through) and lets the picture go.
let heldFrame = null;

function dropHeld() {
  if (heldFrame) { try { heldFrame.close(); } catch { } }
  heldFrame = null;
}

async function holdFrame(blob, maxBytes) {
  dropHeld();
  if (!(blob instanceof Blob) || blob.size < 12 || blob.size > maxBytes) return [];
  const head = new Uint8Array(await blob.slice(0, 12).arrayBuffer());
  if (!kindOf(head)) return [];
  try {
    heldFrame = await createImageBitmap(blob, { imageOrientation: 'from-image' });
  } catch {
    heldFrame = null;
    return [];
  }
  if (!heldFrame.width || !heldFrame.height) { dropHeld(); return []; }
  return [heldFrame.width, heldFrame.height];
}

// A file chosen in Windows' Open dialog. Resolves with [width, height], [] for a file that is not a picture the page
// takes, and null when nothing was chosen.
export function openFrame(input, maxBytes) {
  guardDrops();
  return new Promise(resolve => {
    if (!input) { resolve(null); return; }
    const done = async () => {
      input.removeEventListener('change', done);
      input.removeEventListener('cancel', cancelled);
      const file = input.files && input.files[0];
      input.value = '';
      if (!file) { resolve(null); return; }
      try { resolve(await holdFrame(file, maxBytes)); } catch { resolve([]); }
    };
    const cancelled = () => {
      input.removeEventListener('change', done);
      input.removeEventListener('cancel', cancelled);
      resolve(null);
    };
    input.addEventListener('change', done);
    input.addEventListener('cancel', cancelled);
    input.click();
  });
}

// A picture DOG.ai already holds, as its bytes from C#.
export function openFrameBytes(bytes, maxBytes) {
  return holdFrame(new Blob([bytes]), maxBytes);
}

export async function cutFrame(x, y, width, height, outWidth, outHeight, quality) {
  const bitmap = heldFrame;
  heldFrame = null;
  if (!bitmap) return null;
  try {
    const canvas = document.createElement('canvas');
    canvas.width = outWidth;
    canvas.height = outHeight;
    const g = canvas.getContext('2d', { alpha: false });
    g.fillStyle = '#ffffff';
    g.fillRect(0, 0, outWidth, outHeight);
    g.imageSmoothingEnabled = true;
    g.imageSmoothingQuality = 'high';
    g.drawImage(bitmap, x, y, width, height, 0, 0, outWidth, outHeight);
    const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality / 100));
    return blob ? new Uint8Array(await blob.arrayBuffer()) : null;
  } finally {
    bitmap.close();
  }
}

export function dropFrame() {
  dropHeld();
}
