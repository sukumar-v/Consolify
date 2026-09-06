/* ============================================================================
   Spatial focus engine
   ============================================================================

   D-pad navigation derived from where things actually are on screen, rather than
   from an index into an array the renderer happened to build.

   This is what makes themes possible. The old library nav walked a zones list and
   did arithmetic on hardcoded tile pitches ("focus.col * 223 + 100"), so any theme
   that changed the tile size or the number of columns broke navigation even though
   it looked fine. Here the only thing navigation knows is the set of rectangles on
   screen, so a theme can lay the screen out however it likes -- a 6-wide grid, a
   PS5-style single row, mixed tile sizes, CSS grid auto-fill -- and Up still goes
   to whatever is visually above.

   THE CONTRACT, which is what a theme writes against:

     [data-focus-scope]  a navigation container. The innermost *active* one owns
                         the D-pad; overlays therefore trap focus for free.
     [data-focusable]    takes focus. Anything, anywhere in the scope.
     [data-game-id]      this element stands for a game -- drives the backdrop,
                         A to launch, Y for its menu.
     [data-action]       this element does something. See ACTIONS in app.js.
     [data-focus-key]    optional stable identity, so focus survives a re-render.
                         Falls back to data-game-id, then data-action.

   A theme that emits those attributes navigates correctly without knowing anything
   about this file.

   ---------------------------------------------------------------------------
   A trap worth knowing about, because it has bitten this codebase before:
   #stage is a fixed 1920x1080 box scaled to the window with a CSS transform, so
   getBoundingClientRect() returns POST-transform pixels while scrollTop is in
   layout pixels. The two do not mix. Choosing a target is pure geometry and any
   uniform scale is fine, so that uses rects; scrolling has to match scrollTop and
   so uses offsetTop. Do not "simplify" one into the other.
   ============================================================================ */

// On window explicitly: a top-level const is script-scoped, and a theme's own script needs a
// real global to reach.
window.Nav = (() => {

  /** Elements that can actually be reached right now: rendered, sized, not hidden. */
  function focusables(scope) {
    if (!scope) return [];
    return [...scope.querySelectorAll("[data-focusable]")].filter(el => {
      if (el.closest("[hidden]") || el.dataset.focusDisabled === "true") return false;
      const r = el.getBoundingClientRect();
      // Zero-sized means display:none somewhere up the tree, or not laid out yet.
      return r.width > 0 && r.height > 0;
    });
  }

  /** The innermost active scope: the last open overlay, else the active screen. */
  function activeScope() {
    const overlays = [...document.querySelectorAll("[data-focus-scope].overlay.active")];
    if (overlays.length) return overlays[overlays.length - 1];
    return document.querySelector(".screen.active[data-focus-scope]")
        || document.querySelector("[data-focus-scope]");
  }

  /** A stable identity for an element, so focus can be restored across a re-render. */
  function keyOf(el) {
    if (!el) return null;
    return el.dataset.focusKey
        || (el.dataset.gameId ? "game:" + el.dataset.gameId : null)
        || (el.dataset.action ? "action:" + el.dataset.action : null);
  }

  function centre(r) { return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }

  /* Directional scoring.

     Candidates must lie in the direction travelled, measured from the current
     element's trailing EDGE rather than its centre -- otherwise a tall neighbour
     whose centre sits above the current centre is wrongly rejected.

     The score is the distance along the axis plus the cross-axis miss, weighted so
     that a candidate roughly in line always beats a nearer one far off to the side.
     Elements that overlap the travel corridor get that penalty waived, which is what
     makes a ragged grid feel predictable.

     `anchor` is the sticky cross-axis coordinate: while you keep moving vertically
     it stays where the run started, so drifting through a wide tile and out the
     other side returns to the column you set off from instead of creeping sideways. */
  function score(from, to, dir, anchor) {
    const horizontal = dir === "Left" || dir === "Right";
    const forward = dir === "Right" || dir === "Down";

    // Position along the axis of travel.
    const fromEdge = horizontal ? (forward ? from.right : from.left)
                                : (forward ? from.bottom : from.top);
    const toNear = horizontal ? (forward ? to.left : to.right)
                              : (forward ? to.top : to.bottom);
    const along = forward ? toNear - fromEdge : fromEdge - toNear;
    // A tolerance rather than > 0: neighbours in the same row share an edge, and
    // sub-pixel layout puts some of them a hair behind it.
    if (along < -1) return Infinity;

    // Cross-axis miss, measured against the sticky anchor.
    const toC = centre(to);
    const cross = horizontal ? Math.abs(toC.y - anchor) : Math.abs(toC.x - anchor);

    // Does the candidate straddle the corridor we are travelling down?
    const overlaps = horizontal
      ? to.bottom > from.top && to.top < from.bottom
      : to.right > from.left && to.left < from.right;

    return Math.max(along, 0) + cross * (overlaps ? 0.25 : 2.5);
  }

  /* Wrapping is per-axis and deliberately different in each direction.

     Left/Right wrap within the current row, because a row of tiles reads as a ring
     -- that is how the old grid behaved and losing it felt broken. Up/Down do NOT
     wrap: falling off the bottom of a library and landing back on the tab bar is
     disorienting, and every screen here has a natural top and bottom. */
  function wrapTarget(list, current, dir) {
    if (dir !== "Left" && dir !== "Right") return null;
    const cur = current.getBoundingClientRect();
    const sameRow = list.filter(el => {
      if (el === current) return false;
      const r = el.getBoundingClientRect();
      return r.bottom > cur.top && r.top < cur.bottom;
    });
    if (!sameRow.length) return null;
    // Wrapping right means going to the leftmost of the row, and vice versa.
    return sameRow.reduce((best, el) => {
      const a = el.getBoundingClientRect(), b = best.getBoundingClientRect();
      return dir === "Right" ? (a.left < b.left ? el : best)
                             : (a.left > b.left ? el : best);
    });
  }

  return { focusables, activeScope, keyOf, centre, score, wrapTarget };
})();
