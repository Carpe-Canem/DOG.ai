// Tells the story screen when the reader has selected text inside one passage, so it can offer to
// rewrite, expand or shorten it. Reports the passage's id and the selected text; id 0 means nothing usable.
// Only one story is on screen at a time, so there is one watcher.

let watcher = null;

export function watchSelection(root, dotnet) {
  if (!root || (watcher && watcher.root === root)) return;
  unwatchSelection();
  let timer = 0;
  let lastId = 0;
  let lastText = '';
  const passageOf = node => {
    const el = node && (node.nodeType === 1 ? node : node.parentElement);
    const body = el && el.closest('.passage[data-id] .msg-body');
    return body && root.contains(body) ? body.closest('.passage') : null;
  };
  const report = () => {
    const selection = document.getSelection();
    let id = 0;
    let text = '';
    if (selection && !selection.isCollapsed && selection.rangeCount) {
      const from = passageOf(selection.anchorNode);
      const to = passageOf(selection.focusNode);
      if (from && from === to) {
        id = Number(from.dataset.id) || 0;
        text = selection.toString();
      }
    }
    if (id === lastId && text === lastText) return;
    lastId = id;
    lastText = text;
    dotnet.invokeMethodAsync('SelectionChanged', id, text).catch(() => { });
  };
  const onChange = () => {
    clearTimeout(timer);
    timer = setTimeout(report, 250);
  };
  watcher = { root, onChange, stop: () => clearTimeout(timer) };
  document.addEventListener('selectionchange', onChange);
}

export function unwatchSelection() {
  if (!watcher) return;
  document.removeEventListener('selectionchange', watcher.onChange);
  watcher.stop();
  watcher = null;
}
