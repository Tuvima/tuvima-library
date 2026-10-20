/*
 * Tuvima TV web client - Universe page logic.
 *
 * Pure functions (no DOM) that turn a Universe detail (GET /api/v1/universe/{qid}) into the lines and rows the page
 * draws: the totals line, each Series' rows, the Cast "as ..." lines and the Adaptation lines. Every new field is
 * optional (missing means zero, empty or null). Classic script so a packaged app can load it from file://.
 */
(function (global) {
  'use strict';

  var TV = (global.TV = global.TV || {});

  var SEP = ' · ';   // middle dot
  var DASH = ' — ';  // em dash
  var ALSO_TITLE = 'Also in this world';

  function text(value) { return value === undefined || value === null ? '' : String(value); }

  function count(value) {
    var n = Number(value);
    return isFinite(n) && n > 0 ? Math.floor(n) : 0;
  }

  function list(value) { return Object.prototype.toString.call(value) === '[object Array]' ? value : []; }

  function itemsLabel(n) { return n + (n === 1 ? ' item' : ' items'); }

  // "3 Series · 14 Works · 2 formats". A part with a zero count is left out. As on the Dashboard's Universe page, a
  // Universe whose lines are all TV shows says "1 show" / "3 shows" instead of "Series".
  function totalsLine(detail) {
    var d = detail || {};
    var parts = [];
    var series = count(d.series_count);
    var works = count(d.item_count);
    var formats = list(d.media_types).length;
    var shelves = list(d.shelves);
    var allShows = shelves.length > 0 && shelves.every(function (s) { return text(s && s.group_type).toLowerCase() === 'show'; });
    if (series > 0) { parts.push(series + (allShows ? (series === 1 ? ' show' : ' shows') : ' Series')); }
    if (works > 0) { parts.push(works + (works === 1 ? ' Work' : ' Works')); }
    if (formats > 0) { parts.push(formats + (formats === 1 ? ' format' : ' formats')); }
    return parts.join(SEP);
  }

  // "as Hero, Villain", or null when the cast member has no characters.
  function castLine(member) {
    var labels = [];
    list(member && member.characters).forEach(function (c) {
      var label = text(c && c.label).trim();
      if (label) { labels.push(label); }
    });
    return labels.length ? 'as ' + labels.join(', ') : null;
  }

  function capitalise(value) {
    var s = text(value).trim();
    return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
  }

  // "The Film — Based on The Novel".
  function adaptationLine(adaptation) {
    var a = adaptation || {};
    var line = text(a.label);
    var relation = capitalise(a.relationship);
    var source = text(a.source_label).trim();
    if (relation && source) { line += DASH + relation + ' ' + source; }
    return line;
  }

  // Which rows one Series (a shelf entry) shows. Returns null when it has neither a run nor Also items (skip it).
  // kindLabel is the client's group-type label ("Series", "Show"), or '' when unknown.
  function shelfPlan(entry, kindLabel) {
    var e = entry || {};
    var shelf = e.shelf || {};
    var runItems = list(shelf.items);
    var runCount = e.item_count === undefined || e.item_count === null ? runItems.length : count(e.item_count);
    var also = e.also_in_this_world || null;
    var alsoItems = list(also && also.items);
    var alsoCount = count(e.also_in_this_world_count) || alsoItems.length;
    var showRun = runCount > 0 && runItems.length > 0;
    var showAlso = alsoItems.length > 0;
    if (!showRun && !showAlso) { return null; }

    var kind = text(kindLabel);
    var title = text(shelf.title);
    var runMeta = (kind ? kind + SEP : '') + itemsLabel(runCount);
    // With no run the Series heading carries the Also count, so a Series never reads "0 items".
    var alsoOnlyMeta = (kind ? kind + SEP : '') + ALSO_TITLE + SEP + itemsLabel(alsoCount);

    return {
      collectionId: text(e.collection_id),
      showRun: showRun,
      runItems: showRun ? runItems : [],
      runTitle: title,
      runMeta: runMeta,
      showAlso: showAlso,
      alsoItems: showAlso ? alsoItems : [],
      alsoTitle: showRun ? (text(also && also.title) || ALSO_TITLE) : title,
      alsoMeta: showRun ? itemsLabel(alsoCount) : alsoOnlyMeta
    };
  }

  // Everything the page draws, in wire order. labelOf maps a group_type to the client's label.
  function view(detail, labelOf) {
    var d = detail || {};
    var shelves = [];
    list(d.shelves).forEach(function (entry) {
      var plan = shelfPlan(entry, labelOf ? labelOf(text(entry && entry.group_type).toLowerCase()) : '');
      if (plan) { shelves.push(plan); }
    });
    var cast = list(d.cast).filter(function (m) { return m && text(m.name); }).map(function (m) {
      return { name: text(m.name), line: castLine(m) };
    });
    var adaptations = list(d.adaptations).map(adaptationLine).filter(function (line) { return line; });
    return {
      name: text(d.name),
      description: text(d.description),
      totals: totalsLine(d),
      shelves: shelves,
      cast: cast,
      adaptations: adaptations
    };
  }

  TV.universe = {
    totalsLine: totalsLine,
    castLine: castLine,
    adaptationLine: adaptationLine,
    shelfPlan: shelfPlan,
    view: view
  };
})(window);
