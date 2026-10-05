/* Installed into Laserfiche Web Files/assets/custom by deploy-webclient-button.ps1. */
(function () {
  'use strict';
  if (window.__lfReportsButtonLoaded) return;
  window.__lfReportsButtonLoaded = true;
  const reportsUrl = __LF_REPORTS_URL_JSON__;
  const buttonId = 'lf-smart-reports-button';

  function repositoryName() {
    const field = document.querySelector('#WebAccessRepositoryName, [id$="_WebAccessRepositoryName"]');
    return (field && (field.value || field.textContent) ||
      new URLSearchParams(window.location.search).get('repo') || '').trim();
  }
  function destination() {
    const url = new URL(reportsUrl);
    const repository = repositoryName();
    if (repository) url.searchParams.set('repository', repository);
    url.searchParams.set('source', 'webclient');
    return url.href;
  }
  function mount() {
    const nav = document.getElementById('rightNavbar');
    if (!nav) return;
    const existing = document.getElementById(buttonId);
    if (existing) {
      const href = destination();
      if (existing.href !== href) existing.href = href;
      return;
    }
    const dashboard = Array.from(nav.querySelectorAll('a')).find(function (anchor) {
      return /dashboard/i.test(anchor.textContent) || anchor.id === 'lf-dashboard-button';
    });
    const anchor = document.createElement('a');
    anchor.id = buttonId;
    anchor.href = destination();
    anchor.target = '_blank';
    anchor.rel = 'noopener noreferrer';
    anchor.textContent = '▤ التقارير الذكية';
    anchor.title = 'فتح تقارير ليزرفيش الذكية للمستودع الحالي';
    anchor.setAttribute('aria-label', anchor.title);
    if (dashboard) anchor.className = dashboard.className;
    anchor.style.cssText = 'white-space:nowrap;cursor:pointer;';
    anchor.style.setProperty('color', '#ffffff', 'important');
    anchor.addEventListener('click', function () { anchor.href = destination(); });

    const dashboardItem = dashboard && dashboard.closest('li');
    if (dashboardItem && nav.contains(dashboardItem)) {
      const item = document.createElement('li');
      item.className = dashboardItem.className;
      item.appendChild(anchor);
      dashboardItem.parentNode.insertBefore(item, dashboardItem);
    } else if (dashboard) {
      anchor.style.marginInlineEnd = '16px';
      dashboard.parentNode.insertBefore(anchor, dashboard);
    } else {
      const list = /^(UL|OL)$/.test(nav.tagName) ? nav : nav.querySelector('ul, ol');
      if (list) {
        const item = document.createElement('li');
        item.appendChild(anchor);
        list.insertBefore(item, list.firstChild);
      } else {
        anchor.style.marginInlineEnd = '16px';
        nav.insertBefore(anchor, nav.firstChild);
      }
    }
  }
  function start() {
    mount();
    let scheduled = false;
    new MutationObserver(function () {
      if (scheduled) return;
      scheduled = true;
      window.setTimeout(function () { scheduled = false; mount(); }, 50);
    }).observe(document.body, { childList: true, subtree: true });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
  else start();
})();
