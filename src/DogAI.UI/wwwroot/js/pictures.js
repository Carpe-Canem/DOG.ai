// Turns a picture shown on the page (a data: address) into other picture bytes, in memory. Nothing is written to a file.
// - toPng: the picture maker's "Use this picture" for portraits. Pictures are drawn as WebP, and the avatar cropper and
//   character cards take PNG; doing it here means DOG.ai never needs Windows' own WebP support.
// - toJpeg: story pictures in an EPUB book. WebP is new to EPUB and older e-readers do not show it; JPEG every reader does.
async function convert(src, type, quality) {
    const img = new Image();
    img.src = src;
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    canvas.getContext('2d').drawImage(img, 0, 0);
    const blob = await new Promise((resolve, reject) =>
        canvas.toBlob(b => (b ? resolve(b) : reject(new Error('The picture could not be converted.'))), type, quality));
    return new Uint8Array(await blob.arrayBuffer());
}

export function toPng(src) {
    return convert(src, 'image/png');
}

export function toJpeg(src, quality) {
    return convert(src, 'image/jpeg', quality);
}
