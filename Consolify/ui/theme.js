/* ============================================================================
   Theme templates
   ============================================================================

   Lets a theme supply markup, not just styling. A theme.html sitting next to
   theme.css holds <template> blocks; anything it does not define falls back to
   the built-in markup, so a theme can override one tile and nothing else.

   TWO KINDS OF TEMPLATE, and the split is what keeps this safe:

   1. ITEM templates decide what one thing looks like -- a game tile, a carousel
      tile, a collection card. The theme owns the markup completely.

        <template data-template="game-tile">
          <div class="tile" data-focusable data-game-id="{{id}}">
            <img src="{{cover}}" alt="">
            <span data-if="favorite">*</span>
            <b>{{title}}</b><i>{{platform}} · {{playtime}}</i>
          </div>
        </template>

   2. SCREEN templates decide where the app's own regions go. The theme lays out
      slots; the app moves its regions into them, IDs and handlers intact.

        <template data-template="screen-library">
          <aside data-slot="topbar"></aside>
          <main><div data-slot="continue"></div><div data-slot="grid"></div></main>
        </template>

      That is the whole trick. A theme cannot rebuild #gridScroll and hope the
      renderer still finds it, so it never has to: it says where the grid goes,
      and the app puts the real one there. A slot the theme leaves out hides its
      region rather than deleting it, so every $("id") lookup still resolves and
      the navigation engine skips it (focusables() ignores [hidden]).

   BINDING is deliberately tiny -- {{field}} in text and attributes, plus
   data-if / data-unless on an element. No expressions, no loops, no script:
   a theme is markup, and the interesting layout freedom is in CSS anyway now
   that navigation follows geometry.
   ============================================================================ */

window.Theme = (() => {
  let templates = {};      // name -> HTMLTemplateElement
  let loadedFrom = null;   // url the current set came from, so a reload is detectable

  /** Parse a theme.html. Returns the number of templates found. */
  function load(html, url) {
    templates = {};
    loadedFrom = url || null;
    if (!html) return 0;
    try {
      const doc = new DOMParser().parseFromString(html, "text/html");
      doc.querySelectorAll("template[data-template]").forEach(t => {
        templates[t.dataset.template] = t;
      });
    } catch (e) {
      console.warn("theme.html could not be parsed", e);
      templates = {};
    }
    return Object.keys(templates).length;
  }

  function clear() { templates = {}; loadedFrom = null; }
  function has(name) { return !!templates[name]; }
  function source() { return loadedFrom; }
  function names() { return Object.keys(templates); }

  /* {{a.b}} against the data object. Missing values render empty rather than
     "undefined" -- a theme referencing a field the app does not have should
     leave a gap, not print a word. */
  const FIELD = /\{\{\s*([\w.]+)\s*\}\}/g;

  function lookup(data, path) {
    let v = data;
    for (const part of path.split(".")) {
      if (v === null || v === undefined) return undefined;
      v = v[part];
    }
    return v;
  }

  function substitute(text, data) {
    return text.replace(FIELD, (_, path) => {
      const v = lookup(data, path);
      return v === undefined || v === null ? "" : String(v);
    });
  }

  function truthy(v) {
    return !(v === undefined || v === null || v === false || v === "" || v === 0
             || (Array.isArray(v) && v.length === 0));
  }

  /**
   * Render an item template to an element.
   *
   * Returns null when the theme has no such template, which is the caller's cue
   * to build its own markup -- every call site keeps its original path.
   */
  function render(name, data) {
    const tpl = templates[name];
    if (!tpl) return null;

    const frag = tpl.content.cloneNode(true);

    // Conditionals first: no point binding text into something about to be dropped.
    frag.querySelectorAll("[data-if],[data-unless]").forEach(el => {
      const showIf = el.dataset.if ? truthy(lookup(data, el.dataset.if)) : true;
      const hideIf = el.dataset.unless ? truthy(lookup(data, el.dataset.unless)) : false;
      if (!showIf || hideIf) el.remove();
      else { delete el.dataset.if; delete el.dataset.unless; }
    });

    // Attributes, then text. Attribute values are set with setAttribute rather
    // than innerHTML anywhere, so a game title full of angle brackets cannot
    // become markup.
    frag.querySelectorAll("*").forEach(el => {
      for (const attr of [...el.attributes]) {
        if (attr.value.includes("{{")) el.setAttribute(attr.name, substitute(attr.value, data));
      }
    });
    const walker = document.createTreeWalker(frag, NodeFilter.SHOW_TEXT);
    const texts = [];
    while (walker.nextNode()) if (walker.currentNode.nodeValue.includes("{{")) texts.push(walker.currentNode);
    texts.forEach(n => { n.nodeValue = substitute(n.nodeValue, data); });

    // One element per item, so the caller has something to attach handlers and
    // focus attributes to. A template with several roots gets wrapped.
    const els = [...frag.children];
    if (els.length === 1) return els[0];
    const wrap = document.createElement("div");
    wrap.append(frag);
    return wrap;
  }

  /**
   * Rearrange a screen into the theme's layout.
   *
   * The screen's own regions are moved -- not copied -- so every element keeps
   * its identity, its listeners and its scroll position. Called again with no
   * template, it puts them back in their original order.
   */
  function applyScreen(screen, name) {
    if (!screen) return false;
    const regions = [...screen.querySelectorAll("[data-region]")];
    if (!regions.length) return false;

    // Remember the original home once, so restoring is exact -- including whether the
    // region was hidden to begin with. Some regions are opt-in: they exist for themes
    // that want them and stay out of the built-in layout until one asks.
    if (!screen.__regionHome) {
      screen.__regionHome = regions.map(el =>
        ({ el, parent: el.parentNode, next: el.nextSibling, hidden: el.hidden }));
    }

    const tpl = templates[name];
    if (!tpl) {
      if (!screen.__themed) return false;
      screen.__regionHome.forEach(({ el, parent, next, hidden }) => {
        el.hidden = hidden;
        parent.insertBefore(el, next);
      });
      // Anything the theme added is not ours to keep.
      [...screen.children].forEach(c => { if (c.dataset.themeLayout !== undefined) c.remove(); });
      screen.__themed = false;
      return true;
    }

    const layout = document.createElement("div");
    layout.dataset.themeLayout = "";
    layout.className = "theme-layout";
    layout.append(tpl.content.cloneNode(true));

    const used = new Set();
    layout.querySelectorAll("[data-slot]").forEach(slot => {
      const region = screen.querySelector(`[data-region="${CSS.escape(slot.dataset.slot)}"]`)
                  || screen.__regionHome.find(r => r.el.dataset.region === slot.dataset.slot)?.el;
      if (!region) return;
      region.hidden = false;
      slot.append(region);
      used.add(region);
    });

    // A region the theme did not ask for stays in the document but out of sight,
    // so $("gridScroll") still resolves and nothing has to null-check.
    screen.__regionHome.forEach(({ el }) => { if (!used.has(el)) el.hidden = true; });

    [...screen.children].forEach(c => { if (c.dataset.themeLayout !== undefined) c.remove(); });
    screen.append(layout);
    screen.__themed = true;
    return true;
  }

  return { load, clear, has, render, applyScreen, source, names };
})();
