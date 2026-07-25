/*
 * Copyright 2026 Andre Kaufmann
 * SPDX-License-Identifier: Apache-2.0
 *
 * Knotarium help — runtime. Renders the sidebar from nav.js, builds the on-this-page outline from
 * the article's own headings, runs the offline search, and drives the mobile drawer.
 *
 * Deliberately dependency-free and framework-free: the help ships as plain files that must open
 * from a filesystem, from the bundled /help/ route, and from an air-gapped machine. Nothing here
 * fetches from the network except search-index.json, which sits beside this file.
 *
 * Progressive enhancement contract: page CONTENT is always in the HTML. This script only adds
 * navigation. With JavaScript disabled the prose still reads top to bottom.
 */
(function () {
  'use strict';

  // index.html sits at the help root; everything else lives in pages/. Each page declares which it
  // is via <body data-kg-page="..."> so link prefixes resolve without guessing from the URL (which
  // breaks under file:// on Windows and behind a reverse-proxy subpath).
  var currentPage = document.body.dataset.kgPage || 'index';
  var isRoot = currentPage === 'index';
  var pagePrefix = isRoot ? 'pages/' : '';
  var rootPrefix = isRoot ? '' : '../';

  /* -------------------------------------------------------------- header -- */

  /*
   * The header is injected rather than repeated in every page's markup. With 40+ pages and no build
   * step, a duplicated chrome block is guaranteed to drift — one page ends up with a stale link or a
   * broken search box and nothing catches it. Page CONTENT stays in the HTML; only navigation
   * furniture is generated, which is the same rule the sidebar and outline follow.
   */
  function buildHeader() {
    if (document.querySelector('.kg-header')) return; // a page may still supply its own

    var brandHref = rootPrefix + 'index.html';

    var header = document.createElement('header');
    header.className = 'kg-header';
    header.innerHTML = [
      '<button id="kg-menu-btn" class="kg-menu-btn" aria-label="Toggle navigation" aria-expanded="false">',
        '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round">',
          '<path d="M3 6h18M3 12h18M3 18h18"/>',
        '</svg>',
      '</button>',
      '<a class="kg-brand" href="' + brandHref + '">',
        '<svg width="22" height="22" viewBox="0 0 24 24" fill="none" aria-hidden="true">',
          '<path d="M7.4 7.4 L15.4 11.2" stroke="#6366f1" stroke-width="1.7" stroke-linecap="round"/>',
          '<path d="M7.4 16.6 L15.4 12.8" stroke="#6366f1" stroke-width="1.7" stroke-linecap="round"/>',
          '<circle cx="5" cy="6" r="2.7" stroke="#6366f1" stroke-width="1.8"/>',
          '<circle cx="5" cy="18" r="2.7" stroke="#6366f1" stroke-width="1.8"/>',
          '<circle cx="18" cy="12" r="2.7" stroke="#818cf8" stroke-width="1.8" fill="rgba(99,102,241,0.15)"/>',
        '</svg>',
        'Knotarium <span class="kg-brand-sub">Help</span>',
      '</a>',
      '<div class="kg-search">',
        '<span class="kg-search-icon">',
          '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round">',
            '<circle cx="11" cy="11" r="7"/><path d="M20 20l-3.5-3.5"/>',
          '</svg>',
        '</span>',
        '<input id="kg-search-input" type="search" placeholder="Search the documentation" aria-label="Search the documentation" autocomplete="off">',
        '<span class="kg-search-kbd">/</span>',
        '<div id="kg-search-results" class="kg-search-results" role="listbox" aria-label="Search results"></div>',
      '</div>',
      '<div class="kg-header-spacer"></div>',
      '<a class="kg-header-link is-optional" href="https://github.com/AndreKfm/Knotarium">GitHub</a>',
    ].join('');

    var scrim = document.createElement('div');
    scrim.id = 'kg-scrim';
    scrim.className = 'kg-scrim';

    // Insert AFTER the skip link, never before it. The skip link has to stay the first thing the
    // keyboard reaches, or it cannot do its job — a reader would have to tab through the whole
    // header to get to the control whose purpose is skipping the header.
    var skipLink = document.querySelector('.kg-skip-link');
    var anchor = skipLink ? skipLink.nextSibling : document.body.firstChild;
    document.body.insertBefore(header, anchor);
    document.body.insertBefore(scrim, header.nextSibling);
  }

  /* ------------------------------------------------------------- sidebar -- */

  function buildSidebar() {
    var sidebar = document.getElementById('kg-sidebar');
    if (!sidebar || !window.KG_NAV) return;

    var frag = document.createDocumentFragment();

    window.KG_NAV.forEach(function (group) {
      var section = document.createElement('div');
      section.className = 'kg-nav-group';

      var heading = document.createElement('p');
      heading.className = 'kg-nav-group-title';
      heading.textContent = group.title;
      section.appendChild(heading);

      group.pages.forEach(function (page) {
        // Pages not yet written render as inert labels rather than links: a dead link in offline
        // help is worse than a visible "not written yet", because there is no server to 404.
        if (page.pending) {
          var stub = document.createElement('span');
          stub.className = 'kg-nav-link';
          stub.style.opacity = '0.38';
          stub.style.cursor = 'default';
          stub.title = 'Not written yet';
          stub.textContent = page.title;
          section.appendChild(stub);
          return;
        }
        var link = document.createElement('a');
        link.className = 'kg-nav-link';
        link.href = pagePrefix + page.file;
        link.textContent = page.title;
        if (page.file === currentPage) {
          link.classList.add('is-current');
          link.setAttribute('aria-current', 'page');
        }
        section.appendChild(link);
      });

      frag.appendChild(section);
    });

    sidebar.appendChild(frag);

    // Keep the active entry visible when the sidebar is taller than the viewport.
    var active = sidebar.querySelector('.is-current');
    if (active && active.offsetTop > sidebar.clientHeight - 120) {
      sidebar.scrollTop = active.offsetTop - sidebar.clientHeight / 2;
    }
  }

  /* ------------------------------------------------------ previous / next -- */

  function buildPageNav() {
    var host = document.getElementById('kg-pagenav');
    if (!host || !window.KG_NAV || isRoot) return;

    // Flatten to reading order, skipping unwritten pages so prev/next never lands on a stub.
    var flat = [];
    window.KG_NAV.forEach(function (group) {
      group.pages.forEach(function (page) {
        if (!page.pending) flat.push(page);
      });
    });

    var idx = flat.findIndex(function (p) { return p.file === currentPage; });
    if (idx === -1) return;

    function link(page, dir) {
      var a = document.createElement('a');
      a.href = page.file;
      a.className = dir === 'next' ? 'is-next' : '';
      a.innerHTML =
        '<div class="kg-pagenav-dir">' + (dir === 'next' ? 'Next' : 'Previous') + '</div>' +
        '<div class="kg-pagenav-title"></div>';
      a.querySelector('.kg-pagenav-title').textContent = page.title;
      return a;
    }

    if (idx > 0) host.appendChild(link(flat[idx - 1], 'prev'));
    if (idx < flat.length - 1) host.appendChild(link(flat[idx + 1], 'next'));
  }

  /* ------------------------------------------ on-this-page outline + spy -- */

  function slugify(text) {
    return text.toLowerCase().trim()
      .replace(/[^\w\s-]/g, '')
      .replace(/\s+/g, '-')
      .replace(/-+/g, '-');
  }

  function buildToc() {
    var toc = document.getElementById('kg-toc');
    var article = document.querySelector('.kg-article');
    if (!toc || !article) return;

    var headings = Array.prototype.slice.call(article.querySelectorAll('h2, h3'));
    if (headings.length < 2) { toc.style.display = 'none'; return; }

    var used = {};
    var links = [];

    headings.forEach(function (h) {
      if (!h.id) {
        var base = slugify(h.textContent);
        // Duplicate headings are normal in a reference (every node has "Fields"), so disambiguate
        // rather than letting two anchors collide.
        used[base] = (used[base] || 0) + 1;
        h.id = used[base] > 1 ? base + '-' + used[base] : base;
      }

      var anchor = document.createElement('a');
      anchor.className = 'kg-anchor';
      anchor.href = '#' + h.id;
      anchor.textContent = '#';
      anchor.setAttribute('aria-label', 'Link to this section');
      h.appendChild(anchor);

      var entry = document.createElement('a');
      entry.href = '#' + h.id;
      entry.textContent = h.textContent.replace(/#$/, '');
      if (h.tagName === 'H3') entry.className = 'lvl-3';
      toc.appendChild(entry);
      links.push(entry);
    });

    // Scrollspy. rootMargin pins the "active" band near the top of the viewport so the highlighted
    // entry matches the section the reader is actually looking at, not whatever is merely visible.
    if (!('IntersectionObserver' in window)) return;

    var visible = new Set();
    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) visible.add(entry.target.id);
        else visible.delete(entry.target.id);
      });

      var firstVisible = headings.find(function (h) { return visible.has(h.id); });
      links.forEach(function (l) {
        l.classList.toggle('is-active', !!firstVisible && l.getAttribute('href') === '#' + firstVisible.id);
      });
    }, { rootMargin: '-70px 0px -75% 0px', threshold: 0 });

    headings.forEach(function (h) { observer.observe(h); });
  }

  /* -------------------------------------------------------------- search -- */

  function initSearch() {
    var input = document.getElementById('kg-search-input');
    var results = document.getElementById('kg-search-results');
    if (!input || !results) return;

    var index = null;
    var loading = false;
    var activeIndex = -1;

    function loadIndex() {
      if (index || loading) return Promise.resolve();
      loading = true;
      // file:// blocks fetch in most browsers. That is expected and non-fatal: search degrades to
      // "unavailable" while every other navigation affordance keeps working.
      return fetch(rootPrefix + 'assets/search-index.json')
        .then(function (r) { return r.ok ? r.json() : []; })
        .then(function (data) { index = data; })
        .catch(function () { index = []; })
        .finally(function () { loading = false; });
    }

    function escapeHtml(s) {
      return s.replace(/[&<>"']/g, function (c) {
        return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
      });
    }

    function score(entry, terms) {
      var haystackTitle = entry.title.toLowerCase();
      var haystackBody = entry.text.toLowerCase();
      var total = 0;
      for (var i = 0; i < terms.length; i++) {
        var t = terms[i];
        var inTitle = haystackTitle.indexOf(t) !== -1;
        var inBody = haystackBody.indexOf(t) !== -1;
        if (!inTitle && !inBody) return 0; // every term must appear somewhere
        if (inTitle) total += haystackTitle === t ? 100 : 40;
        if (inBody) total += 4;
      }
      return total;
    }

    function snippetFor(text, term) {
      var lower = text.toLowerCase();
      var at = lower.indexOf(term);
      if (at === -1) return escapeHtml(text.slice(0, 110)) + '…';
      var from = Math.max(0, at - 40);
      var raw = text.slice(from, from + 130);
      var html = escapeHtml(raw);
      // Highlight on the escaped string; term is lower-cased plain text, so a case-insensitive
      // regex over escaped output is safe as long as the term itself is escaped too.
      var safeTerm = escapeHtml(term).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
      html = html.replace(new RegExp(safeTerm, 'ig'), function (m) { return '<mark>' + m + '</mark>'; });
      return (from > 0 ? '…' : '') + html + '…';
    }

    function render(query) {
      var terms = query.toLowerCase().split(/\s+/).filter(Boolean);
      activeIndex = -1;

      if (!terms.length) { close(); return; }
      if (!index) { return; }

      var hits = index
        .map(function (entry) { return { entry: entry, score: score(entry, terms) }; })
        .filter(function (h) { return h.score > 0; })
        .sort(function (a, b) { return b.score - a.score; })
        .slice(0, 12);

      results.innerHTML = '';

      if (!hits.length) {
        results.innerHTML = '<div class="kg-search-empty">No results for “' + escapeHtml(query) + '”</div>';
        results.classList.add('is-open');
        return;
      }

      hits.forEach(function (hit) {
        var a = document.createElement('a');
        a.className = 'kg-search-result';
        a.href = (isRoot ? 'pages/' : '') + hit.entry.url;
        a.innerHTML =
          '<div class="kg-search-result-crumb">' + escapeHtml(hit.entry.section) + '</div>' +
          '<div class="kg-search-result-title">' + escapeHtml(hit.entry.title) + '</div>' +
          '<div class="kg-search-result-snippet">' + snippetFor(hit.entry.text, terms[0]) + '</div>';
        results.appendChild(a);
      });

      results.classList.add('is-open');
    }

    function close() {
      results.classList.remove('is-open');
      results.innerHTML = '';
      activeIndex = -1;
    }

    function moveActive(delta) {
      var items = results.querySelectorAll('.kg-search-result');
      if (!items.length) return;
      activeIndex = (activeIndex + delta + items.length) % items.length;
      items.forEach(function (el, i) { el.classList.toggle('is-active', i === activeIndex); });
      items[activeIndex].scrollIntoView({ block: 'nearest' });
    }

    input.addEventListener('focus', loadIndex);
    input.addEventListener('input', function () {
      loadIndex().then(function () { render(input.value); });
    });

    input.addEventListener('keydown', function (e) {
      if (e.key === 'ArrowDown') { e.preventDefault(); moveActive(1); }
      else if (e.key === 'ArrowUp') { e.preventDefault(); moveActive(-1); }
      else if (e.key === 'Enter') {
        var active = results.querySelector('.kg-search-result.is-active');
        if (active) { e.preventDefault(); window.location.href = active.href; }
      } else if (e.key === 'Escape') { input.value = ''; close(); input.blur(); }
    });

    document.addEventListener('click', function (e) {
      if (!results.contains(e.target) && e.target !== input) close();
    });

    // "/" to search, matching the app's own single-key shortcuts — but never steal the key while
    // the reader is typing into something.
    document.addEventListener('keydown', function (e) {
      var tag = (e.target.tagName || '').toLowerCase();
      var typing = tag === 'input' || tag === 'textarea' || e.target.isContentEditable;
      if (e.key === '/' && !typing) { e.preventDefault(); input.focus(); }
      if (e.key === 'k' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); input.focus(); }
    });
  }

  /* -------------------------------------------------------- mobile drawer -- */

  function initDrawer() {
    var btn = document.getElementById('kg-menu-btn');
    var sidebar = document.getElementById('kg-sidebar');
    var scrim = document.getElementById('kg-scrim');
    if (!btn || !sidebar || !scrim) return;

    function setOpen(open) {
      sidebar.classList.toggle('is-open', open);
      scrim.classList.toggle('is-open', open);
      btn.setAttribute('aria-expanded', String(open));
      document.body.style.overflow = open ? 'hidden' : '';
    }

    btn.addEventListener('click', function () { setOpen(!sidebar.classList.contains('is-open')); });
    scrim.addEventListener('click', function () { setOpen(false); });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && sidebar.classList.contains('is-open')) setOpen(false);
    });
    // Tapping a link inside the drawer navigates; make sure the drawer state does not survive a
    // same-page anchor jump.
    sidebar.addEventListener('click', function (e) {
      if (e.target.closest('a')) setOpen(false);
    });

    // Growing past the drawer breakpoint pins the sidebar open again by stylesheet, but the drawer
    // state is still "open" — which would leave the body scroll-locked on a desktop layout with no
    // scrim left to click. Reset it on the way up.
    var wide = window.matchMedia('(min-width: 1001px)');
    var onBreakpoint = function (e) { if (e.matches) setOpen(false); };
    if (wide.addEventListener) wide.addEventListener('change', onBreakpoint);
    else if (wide.addListener) wide.addListener(onBreakpoint); // Safari < 14
  }

  /* ----------------------------------------------------------------- go -- */

  function init() {
    buildHeader();
    buildSidebar();
    buildToc();
    buildPageNav();
    initSearch();
    initDrawer();
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
