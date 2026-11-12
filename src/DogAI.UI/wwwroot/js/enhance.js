// Colours code blocks (highlight.js) and draws maths (KaTeX) in replies, after Blazor has put them on the page.
// Both libraries are bundled under vendor/ and nothing is fetched. The text they work on is already escaped (code) or
// handed over as plain text (maths); KaTeX is told not to trust links or commands.
(function () {
  let queued = false;

  function enhance() {
    queued = false;
    if (window.hljs) {
      for (const el of document.querySelectorAll('pre code[data-lang]:not([data-hl])')) {
        el.dataset.hl = '1';
        const lang = el.dataset.lang;
        if (!window.hljs.getLanguage(lang)) continue;
        try {
          const out = window.hljs.highlight(el.textContent, { language: lang, ignoreIllegals: true });
          el.innerHTML = out.value; // highlight.js escapes the text it was given
          el.classList.add('hljs');
        } catch { /* leave it plain */ }
      }
    }
    if (window.katex) {
      for (const el of document.querySelectorAll('.math:not([data-math])')) {
        el.dataset.math = '1';
        let tex = el.textContent.trim();
        const display = el.tagName === 'DIV' || tex.startsWith('\\[');
        tex = tex.replace(/^\\[(\[]/, '').replace(/\\[)\]]$/, '').trim();
        try {
          window.katex.render(tex, el, { displayMode: display, throwOnError: false, trust: false, output: 'htmlAndMathml', maxExpand: 200, maxSize: 20 });
        } catch { /* keep the source text */ }
      }
    }
  }

  function schedule() {
    if (queued) return;
    queued = true;
    requestAnimationFrame(enhance);
  }

  new MutationObserver(schedule).observe(document.documentElement, { childList: true, subtree: true });
  schedule();
})();
