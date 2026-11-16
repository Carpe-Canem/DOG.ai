// Pictures put on a chat message (M17 step 4): the attach button, paste and drop. A file from outside is opened only
// here, by the browser control: it is reduced to the longest side given and written again as a JPEG, white behind
// anything see-through, so what DOG.ai keeps is the picture alone (no camera, place or date). Nothing is written to a
// file. What C# receives is that JPEG, which Core checks (AttachedPictures.Problem) before keeping it.

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

// A file dropped anywhere else on the page would make the browser control try to open it (and the address, with the
// file's name, would be refused and noted in the log). Dropped outside the chat box, it does nothing.
let pageGuarded = false;
function guardPage() {
  if (pageGuarded) return;
  pageGuarded = true;
  document.addEventListener('dragover', e => { if (hasFiles(e)) { e.preventDefault(); e.dataTransfer.dropEffect = 'none'; } });
  document.addEventListener('drop', e => { if (hasFiles(e)) e.preventDefault(); });
}

// root: the chat box with its pictures row (drop target); textarea: where pasting happens; input: the hidden file input.
export function watchPictures(root, textarea, input, dotnet, maxSide, quality, maxBytes) {
  guardPage();
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
        for (const file of batch) {
          if (added >= room) { left++; continue; }
          let jpeg = null;
          try { jpeg = await toJpeg(file, maxSide, quality, maxBytes); } catch { jpeg = null; }
          if (!jpeg) { refused++; continue; }
          const ok = await state.dotnet.invokeMethodAsync('PictureArrived', jpeg);
          if (ok) added++; else refused++;
        }
        await state.dotnet.invokeMethodAsync('PicturesDone', refused, left);
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
