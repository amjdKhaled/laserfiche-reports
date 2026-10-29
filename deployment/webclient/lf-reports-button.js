/**
 * lf-reports-button.js
 * Adds a "Reports" button to the Laserfiche Web Access (Browse.aspx) top navbar,
 * next to the existing custom Dashboard button, that opens Laserfiche Reports.
 *
 * Deployed by deployment\windows\deploy-webclient-button.ps1, which replaces the
 * __REPORTS_URL__ sentinel and adds this script to Browse.aspx.
 *
 * Reports is served by IIS at https://<server>:5188/ and accepts browsers on the
 * Reports server itself only; other machines receive 403.
 * Opens /launch?repository=<open repository>. With LFDS single sign-on enabled in
 * Reports, the browser's LFDS sign-in logs the same user in; otherwise the password
 * form opens with the repository filled in. No token or cookie is passed to Reports.
 *
 * DELETED DOCUMENTS
 * The Repository API cannot read the Recycle Bin. The Reports tab opened by this button
 * keeps this tab as its opener and asks it, by postMessage, for the Recycle Bin listing.
 * This tab reads it with the signed-in user's own Web Client session, so each user gets
 * exactly what the Web Client Recycle Bin shows them (their own deletions; administrators
 * see everything). Only rows are returned, never cookies or tokens. ListingsService is a
 * Web Client 12 internal service; if a later version changes it, Reports shows an error.
 */
(function () {
    'use strict';

    if (window.__lfReportsButtonInitialized) return;
    window.__lfReportsButtonInitialized = true;

    // Laserfiche AngularJS shadows `document`; capture the real objects first.
    var _win = window;
    var _doc = window.document;

    var REPORTS_URL = '__REPORTS_URL__';
    var BUTTON_ID = 'lf-reports-btn';
    var BUTTON_DATA_ATTR = 'data-lf-reports-button';
    var POLL_TIMEOUT_MS = 12000;

    // The repository open in the Web Client, as the Dashboard button reads it.
    function getRepository() {
        var el = _doc.getElementById('WebAccessRepositoryName');
        if (el && typeof el.value === 'string' && el.value.trim().length > 0) return el.value.trim();
        try {
            var params = new URLSearchParams(_win.location.search);
            var fromQuery = params.get('repo') || params.get('db') || params.get('repository');
            if (fromQuery && fromQuery.trim().length > 0) return fromQuery.trim();
        } catch (e) { /* URLSearchParams unavailable */ }
        return null;
    }

    function openReports(event) {
        if (event) {
            if (event.preventDefault) event.preventDefault();
            if (event.stopPropagation) event.stopPropagation();
            if (event.stopImmediatePropagation) event.stopImmediatePropagation();
        }
        if (!REPORTS_URL || REPORTS_URL.indexOf('://') < 0) {
            alert('The Reports button is not configured. Re-run deploy-webclient-button.ps1.');
            return;
        }
        // A named target reuses one Reports tab instead of opening a new one per click.
        var a = _doc.createElement('a');
        var repo = getRepository();
        a.href = REPORTS_URL.replace(/\/+$/, '') + '/launch' +
            (repo ? '?repository=' + encodeURIComponent(repo) + '&source=webclient' : '');
        a.target = 'laserfiche-reports';
        // Keep this tab as the opener: Reports asks it for the Recycle Bin listing.
        a.rel = 'opener';
        a.style.display = 'none';
        _doc.body.appendChild(a);
        a.click();
        _doc.body.removeChild(a);
    }

    var REPORTS_ORIGIN = (function () {
        try { return new _win.URL(REPORTS_URL).origin; } catch (e) { return null; }
    }());
    var RECYCLE_PAGE_SIZE = 50;
    var RECYCLE_MAX_ROWS = 10000;

    function readCookie(name) {
        var parts = (_doc.cookie || '').split(';');
        for (var i = 0; i < parts.length; i++) {
            var pair = parts[i].replace(/^\s+/, '');
            if (pair.indexOf(name + '=') === 0) return decodeURIComponent(pair.substring(name.length + 1));
        }
        return '';
    }

    // Reads every Recycle Bin page, as the Web Client's own Recycle Bin view does.
    function readRecycleBin(repo) {
        var url = _win.location.pathname.replace(/[^\/]*$/, '') + 'ListingsService.ashx/GetRecycleBinFolderListing';
        var rows = [];
        function page(start, listingId) {
            return _win.fetch(url, {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': 'application/json;charset=UTF-8',
                    'Lf-Repository': repo,
                    'X-Lf-Repo-Id': repo,
                    'Lf-Locale': 'en-US',          // Fixed date format: M/D/YYYY h:mm AM/PM.
                    'Lf-Language': 'en-US',
                    'X-Lf-Suppress-Login-Redirect': '1',
                    'X-Xsrf-Token': readCookie('XSRF-TOKEN')
                },
                body: JSON.stringify({ repoName: repo, folderId: -1, start: start, count: RECYCLE_PAGE_SIZE, listingid: listingId })
            }).then(function (response) {
                if (response.status === 401 || response.status === 403) throw new Error('unauthorized');
                if (!response.ok) throw new Error('http_' + response.status);
                return response.json();
            }).then(function (body) {
                var listing = body && body.d;
                if (!listing || !listing.result || typeof listing.total !== 'number') throw new Error('unexpected_response');
                for (var i = 0; i < listing.result.length; i++) {
                    var r = listing.result[i];
                    rows.push({ id: r.Id, name: r.Name, type: r.EntryType || r.Type, location: r.ParentPath,
                                deleted: r.DeletedDateTime, deletedBy: r.DeleterName, created: r.CreatedDateTime });
                }
                if (listing.result.length === 0 || rows.length >= listing.total) return { total: listing.total, rows: rows };
                if (rows.length >= RECYCLE_MAX_ROWS) throw new Error('too_many');
                return page(start + listing.result.length, listing.listingid);
            });
        }
        return page(1, -1);
    }

    // Answers only the Reports origin, and only to the window that asked.
    _win.addEventListener('message', function (event) {
        var request = event.data;
        if (!REPORTS_ORIGIN || event.origin !== REPORTS_ORIGIN || !event.source ||
            !request || request.type !== 'lf-reports:recycle-bin') return;
        function reply(result) {
            result.type = 'lf-reports:recycle-bin-result';
            result.requestId = request.requestId;
            event.source.postMessage(result, event.origin);
        }
        var repo = getRepository();
        if (!repo) { reply({ ok: false, error: 'no_repository' }); return; }
        if (request.repository && String(request.repository).toLowerCase() !== repo.toLowerCase()) {
            reply({ ok: false, error: 'repository_mismatch', repository: repo });
            return;
        }
        readRecycleBin(repo).then(function (listing) {
            reply({ ok: true, repository: repo, total: listing.total, rows: listing.rows });
        }, function (error) {
            reply({ ok: false, error: (error && error.message) || 'failed' });
        });
    });

    function createButton() {
        var btn = _doc.createElement('button');
        btn.type = 'button';
        btn.id = BUTTON_ID;
        btn.setAttribute(BUTTON_DATA_ATTR, 'true');
        btn.title = 'Open Laserfiche Reports';
        btn.className = 'btn browseNavButton lf-reports-btn';
        btn.style.cssText = [
            'display:inline-flex', 'align-items:center', 'gap:5px', 'padding:6px 10px',
            'color:#ffffff', 'background:transparent', 'border:none', 'font-size:14px',
            'font-weight:500', 'cursor:pointer', 'white-space:nowrap', 'font-family:inherit',
            'line-height:1', 'border-radius:4px', 'transition:background .15s',
            'pointer-events:auto', 'position:relative', 'z-index:1000', 'user-select:none'
        ].join(';');
        btn.addEventListener('mouseover', function () { btn.style.background = 'rgba(255,255,255,0.15)'; });
        btn.addEventListener('mouseout', function () { btn.style.background = 'transparent'; });

        // Document-with-lines icon (inline SVG, no external asset).
        var svg = _doc.createElementNS('http://www.w3.org/2000/svg', 'svg');
        svg.setAttribute('width', '20');
        svg.setAttribute('height', '20');
        svg.setAttribute('viewBox', '0 0 24 24');
        svg.setAttribute('fill', 'none');
        svg.setAttribute('stroke', 'currentColor');
        svg.setAttribute('stroke-width', '2');
        svg.setAttribute('aria-hidden', 'true');
        svg.style.cssText = 'flex-shrink:0;vertical-align:middle;pointer-events:none;';
        svg.innerHTML = '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>' +
            '<path d="M14 2v6h6"/><path d="M8 13h8"/><path d="M8 17h5"/>';
        btn.appendChild(svg);

        var label = _doc.createElement('span');
        label.textContent = 'Reports';
        label.style.cssText = 'pointer-events:none;';
        btn.appendChild(label);
        return btn;
    }

    // One capture-phase listener: Angular bubble-phase handlers cannot swallow the click.
    var _handlerRegistered = false;
    function ensureDelegatedHandler() {
        if (_handlerRegistered) return;
        _handlerRegistered = true;
        _doc.addEventListener('click', function (event) {
            var target = event.target;
            while (target && target !== _doc) {
                if (target.getAttribute && target.getAttribute(BUTTON_DATA_ATTR) === 'true') {
                    openReports(event);
                    return;
                }
                target = target.parentNode;
            }
        }, true);
    }

    function tryInject() {
        if (_doc.getElementById(BUTTON_ID)) return true;
        var rightNavbar = _doc.getElementById('rightNavbar');
        if (!rightNavbar) return false;

        var li = _doc.createElement('li');
        li.style.cssText = 'list-style:none;display:inline-flex;align-items:center;pointer-events:auto;';
        li.appendChild(createButton());
        var ul = _doc.createElement('ul');
        ul.className = 'nav navbar-nav lf-reports-nav';
        ul.style.cssText = 'margin:0;padding:0;list-style:none;display:inline-flex;align-items:center;pointer-events:auto;';
        ul.appendChild(li);

        // Sit immediately after the Dashboard button when it exists.
        var dashboardNav = rightNavbar.querySelector('.lf-dashboard-nav');
        rightNavbar.insertBefore(ul, dashboardNav ? dashboardNav.nextSibling : rightNavbar.firstChild);
        ensureDelegatedHandler();
        return true;
    }

    function startObserver() {
        if (!_win.MutationObserver) return;
        new _win.MutationObserver(function () {
            if (!_doc.getElementById(BUTTON_ID) && _doc.getElementById('rightNavbar')) tryInject();
        }).observe(_doc.body, { childList: true, subtree: true });
    }

    function init() {
        if (tryInject()) { startObserver(); return; }
        var elapsed = 0;
        var interval = setInterval(function () {
            elapsed += 250;
            if (tryInject()) {
                clearInterval(interval);
                startObserver();
            } else if (elapsed >= POLL_TIMEOUT_MS) {
                clearInterval(interval);
                console.warn('[LFReports] id="rightNavbar" was not found; the Reports button was not added.');
            }
        }, 250);
    }

    if (_doc.readyState === 'loading') _doc.addEventListener('DOMContentLoaded', init);
    else init();
}());
