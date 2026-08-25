(function () {
  'use strict';

  function applyHashViewBootstrap() {
    var hash = (window.location.hash || '').replace('#', '');
    if (hash !== 'view-manage' && hash.indexOf('ss-') !== 0) return;

    var viewDashboard = document.getElementById('view-dashboard');
    var viewManage = document.getElementById('view-manage');
    var tabDashboard = document.getElementById('view-tab-dashboard');
    var tabManage = document.getElementById('view-tab-manage');
    if (!viewDashboard || !viewManage || !tabDashboard || !tabManage) return;

    viewDashboard.classList.remove('raid-ss-tab-active');
    viewManage.classList.add('raid-ss-tab-active');
    tabDashboard.classList.remove('dfe-f-sub-navigation__item--current');
    tabManage.classList.add('dfe-f-sub-navigation__item--current');
    tabDashboard.querySelector('a').removeAttribute('aria-current');
    tabManage.querySelector('a').setAttribute('aria-current', 'page');
  }

  function initRegisterDetail(config) {
    if (!config || typeof window.RaidSpreadsheet === 'undefined') return;

    var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
    var csrfToken = tokenEl ? tokenEl.value : '';

    window.RaidSpreadsheet.init({
      lookups: config.lookups || {},
      csrfToken: csrfToken,
      baseUrl: config.baseUrl || '/modern/raid',
      registerId: config.registerId,
      tableLayouts: config.tableLayouts || {},
      readOnly: !!config.readOnly,
      canEditLockedInherentRatings: !!config.canEditLockedInherentRatings,
      riskInfo: config.riskInfo || {}
    });

    function switchView(viewName) {
      var isDashboard = viewName === 'dashboard';
      document.getElementById('view-dashboard').classList.toggle('raid-ss-tab-active', isDashboard);
      document.getElementById('view-manage').classList.toggle('raid-ss-tab-active', !isDashboard);
      var tabDash = document.getElementById('view-tab-dashboard');
      var tabManage = document.getElementById('view-tab-manage');
      tabDash.classList.toggle('dfe-f-sub-navigation__item--current', isDashboard);
      tabManage.classList.toggle('dfe-f-sub-navigation__item--current', !isDashboard);
      tabDash.querySelector('a').toggleAttribute('aria-current', isDashboard);
      tabManage.querySelector('a').toggleAttribute('aria-current', !isDashboard);
      if (!isDashboard && window.RaidSpreadsheetIntro && window.RaidSpreadsheetIntro.tryAutoOpen) {
        setTimeout(function () { window.RaidSpreadsheetIntro.tryAutoOpen(); }, 150);
      }
    }

    function activateManageTab(tabName) {
      ['risks', 'issues', 'assumptions', 'nearmisses', 'dependencies'].forEach(function (name) {
        var isCurrent = name === tabName;
        var li = document.getElementById('manage-tab-' + name);
        var panel = document.getElementById('ss-panel-' + name);
        if (li) {
          li.classList.toggle('dfe-f-sub-navigation__item--current', isCurrent);
          li.querySelector('a').toggleAttribute('aria-current', isCurrent);
        }
        if (panel) panel.classList.toggle('raid-ss-tab-active', isCurrent);
      });
    }

    document.querySelectorAll('#register-view-tabs a').forEach(function (link) {
      link.addEventListener('click', function (e) {
        e.preventDefault();
        switchView(this.getAttribute('data-view'));
        window.location.hash = this.getAttribute('href').substring(1);
      });
    });

    document.querySelectorAll('[data-view-switch]').forEach(function (link) {
      link.addEventListener('click', function (e) {
        e.preventDefault();
        switchView('manage');
        var subTab = this.getAttribute('data-tab-switch');
        if (subTab) activateManageTab(subTab);
      });
    });

    document.querySelectorAll('#manage-sub-tabs a').forEach(function (link) {
      link.addEventListener('click', function (e) {
        e.preventDefault();
        var tabName = this.getAttribute('data-tab');
        activateManageTab(tabName);
        window.location.hash = 'ss-' + tabName;
      });
    });

    var hash = window.location.hash.replace('#', '');
    if (hash === 'view-manage' || hash.indexOf('ss-') === 0) {
      switchView('manage');
      if (hash.indexOf('ss-') === 0) {
        activateManageTab(hash.replace('ss-', ''));
      }
    }
  }

  applyHashViewBootstrap();

  window.RaidRegisterDetail = {
    init: initRegisterDetail
  };
})();
