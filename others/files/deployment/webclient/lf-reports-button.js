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
 * No Laserfiche session, token or cookie is passed to Reports; the user signs in there.
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
        a.href = REPORTS_URL;
        a.target = 'laserfiche-reports';
        a.rel = 'noopener noreferrer';
        a.style.display = 'none';
        _doc.body.appendChild(a);
        a.click();
        _doc.body.removeChild(a);
    }

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
