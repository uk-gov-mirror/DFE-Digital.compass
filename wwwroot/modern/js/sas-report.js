(function () {
  'use strict';

  var root = document.querySelector('.sas-rpt');
  if (!root) return;

  var panels = {
    overview: document.getElementById('sas-tab-panel-overview'),
    standards: document.getElementById('sas-tab-panel-standards'),
    assessors: document.getElementById('sas-tab-panel-assessors')
  };
  var buttons = {
    overview: document.getElementById('sas-tab-btn-overview'),
    standards: document.getElementById('sas-tab-btn-standards'),
    assessors: document.getElementById('sas-tab-btn-assessors')
  };

  function activateTab(name) {
    var active = name || 'overview';
    Object.keys(panels).forEach(function (key) {
      var panel = panels[key];
      var btn = buttons[key];
      if (!panel) return;
      var isActive = key === active;
      panel.hidden = !isActive;
      if (btn) {
        btn.classList.toggle('is-active', isActive);
        btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
      }
    });
    window.dispatchEvent(new Event('resize'));
  }

  root.querySelectorAll('[data-sas-tab]').forEach(function (btn) {
    btn.addEventListener('click', function () {
      activateTab(btn.getAttribute('data-sas-tab'));
    });
  });

  root.querySelectorAll('[data-sas-tab-link]').forEach(function (link) {
    link.addEventListener('click', function () {
      var tab = link.getAttribute('data-sas-tab-link');
      if (tab) activateTab(tab);
    });
  });

  var hash = (window.location.hash || '').replace('#', '');
  if (hash === 'standards' || hash === 'sas-standards-analysis') {
    activateTab('standards');
  } else if (hash === 'assessors' || hash === 'sas-assessor-analysis') {
    activateTab('assessors');
  }

  function setPanelView(panel, view) {
    var chartPane = panel.querySelector('.sas-panel__pane--chart');
    var tablePane = panel.querySelector('.sas-panel__pane--table');
    if (!chartPane || !tablePane) return;
    var showChart = view === 'chart';
    chartPane.hidden = !showChart;
    tablePane.hidden = showChart;
    panel.querySelectorAll('.sas-panel__view-btn').forEach(function (btn) {
      var isActive = btn.getAttribute('data-sas-view') === view;
      btn.classList.toggle('is-active', isActive);
      btn.setAttribute('aria-pressed', isActive ? 'true' : 'false');
    });
    if (showChart) window.dispatchEvent(new Event('resize'));
  }

  root.querySelectorAll('.sas-panel').forEach(function (panel) {
    panel.querySelectorAll('.sas-panel__view-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        setPanelView(panel, btn.getAttribute('data-sas-view'));
      });
    });

    var expandBtn = panel.querySelector('.sas-panel__expand');
    var grid = panel.closest('.sas-rpt__summary-grid');
    if (expandBtn) {
      expandBtn.addEventListener('click', function () {
        var expanded = panel.classList.toggle('sas-panel--expanded');
        if (grid) grid.classList.toggle('sas-rpt__summary-grid--expanded', expanded);
        expandBtn.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        expandBtn.textContent = expanded ? 'Collapse' : 'Expand';
        window.dispatchEvent(new Event('resize'));
      });
    }
  });

  var leagueRoot = document.getElementById('sas-assessor-league');
  if (leagueRoot) {
    var leaguePanels = {};
    leagueRoot.querySelectorAll('.sas-assessor-league-panel').forEach(function (panel) {
      var id = panel.id || '';
      if (id.indexOf('sas-league-panel-') === 0) {
        leaguePanels[id.slice('sas-league-panel-'.length)] = panel;
      }
    });

    function initDfeComponents() {
      if (typeof window.DfeFrontend !== 'undefined' && typeof window.DfeFrontend.initAll === 'function') {
        window.DfeFrontend.initAll();
      }
    }

    var filterLabels = {
      assessments: 'Service assessments',
      'peer-reviews': 'Peer reviews',
      panels: 'Panel assignments',
      published: 'Published panel assignments',
      red: 'Red outcomes',
      amber: 'Amber outcomes',
      green: 'Green outcomes'
    };

    function assignmentMatchesFilter(item, filter) {
      var type = item.getAttribute('data-sas-type') || '';
      var status = item.getAttribute('data-sas-status') || '';
      var outcome = item.getAttribute('data-sas-outcome') || '';
      if (filter === 'assessments') return type === 'service assessment';
      if (filter === 'peer-reviews') return type === 'peer review';
      if (filter === 'published') return status === 'published';
      if (filter === 'red') return outcome === 'red';
      if (filter === 'amber') return outcome === 'amber';
      if (filter === 'green') return outcome === 'green';
      return true;
    }

    function collapseAllLeagueDetails() {
      leagueRoot.querySelectorAll('.sas-league-drill').forEach(function (btn) {
        btn.setAttribute('aria-expanded', 'false');
        btn.classList.remove('sas-league-drill--active');
      });
      leagueRoot.querySelectorAll('.sas-league-detail').forEach(function (detailRow) {
        detailRow.hidden = true;
      });
    }

    function resetLeagueDrills(exceptBtn) {
      var keepDetailId = exceptBtn ? exceptBtn.getAttribute('aria-controls') : null;
      leagueRoot.querySelectorAll('.sas-league-drill').forEach(function (btn) {
        if (btn === exceptBtn) return;
        btn.setAttribute('aria-expanded', 'false');
        btn.classList.remove('sas-league-drill--active');
      });
      leagueRoot.querySelectorAll('.sas-league-detail').forEach(function (detailRow) {
        if (keepDetailId && detailRow.id === keepDetailId) return;
        detailRow.hidden = true;
      });
    }

    function applyLeagueFilter(detailRow, filter) {
      var visibleCount = 0;
      detailRow.querySelectorAll('.sas-league-detail__item').forEach(function (item) {
        var show = assignmentMatchesFilter(item, filter);
        item.hidden = !show;
        if (show) visibleCount += 1;
      });
      return visibleCount;
    }

    function setLeagueDetailExpanded(btn) {
      var detailId = btn.getAttribute('data-expand-id');
      var filter = btn.getAttribute('data-sas-league-filter') || 'panels';
      var detailRow = detailId ? document.getElementById(detailId) : null;
      if (!detailRow) return;

      resetLeagueDrills(btn);

      var assessorName = '';
      var parentRow = btn.closest('[data-sas-league-row]');
      if (parentRow) {
        var nameCell = parentRow.querySelector('th[scope="row"]');
        if (nameCell) {
          assessorName = nameCell.childNodes[0] ? String(nameCell.childNodes[0].textContent || '').trim() : '';
        }
      }

      var visibleCount = applyLeagueFilter(detailRow, filter);
      var labelEl = detailRow.querySelector('.sas-league-detail__label');
      if (labelEl) {
        var filterLabel = filterLabels[filter] || 'Panel assignments';
        labelEl.innerHTML =
          visibleCount.toLocaleString('en-GB') + ' ' + filterLabel.toLowerCase() +
          (assessorName ? ' for <strong>' + assessorName.replace(/</g, '&lt;').replace(/>/g, '&gt;') + '</strong>' : '');
      }

      detailRow.hidden = false;
      btn.setAttribute('aria-expanded', 'true');
      btn.classList.add('sas-league-drill--active');
      detailRow.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }

    function activateLeagueYear(key) {
      if (!key) return;
      collapseAllLeagueDetails();
      Object.keys(leaguePanels).forEach(function (k) {
        var panel = leaguePanels[k];
        if (!panel) return;
        var isActive = k === key;
        panel.classList.toggle('on', isActive);
        panel.hidden = !isActive;
      });
      leagueRoot.querySelectorAll('[data-sas-league-year]').forEach(function (btn) {
        var isActive = btn.getAttribute('data-sas-league-year') === key;
        btn.classList.toggle('on', isActive);
        var item = btn.closest('.dfe-f-sub-navigation__item');
        if (item) {
          item.classList.toggle('dfe-f-sub-navigation__item--active', isActive);
        }
      });
      initDfeComponents();
    }

    leagueRoot.querySelectorAll('[data-sas-league-year]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        activateLeagueYear(btn.getAttribute('data-sas-league-year'));
      });
    });

    var defaultLeagueBtn =
      leagueRoot.querySelector('.dfe-f-sub-navigation__item--active [data-sas-league-year]') ||
      leagueRoot.querySelector('[data-sas-league-year]');
    if (defaultLeagueBtn) {
      activateLeagueYear(defaultLeagueBtn.getAttribute('data-sas-league-year'));
    }

    leagueRoot.addEventListener('click', function (e) {
      var btn = e.target.closest('.sas-league-drill');
      if (!btn || !leagueRoot.contains(btn)) return;
      var isOpen = btn.getAttribute('aria-expanded') === 'true' && btn.classList.contains('sas-league-drill--active');
      if (isOpen) {
        collapseAllLeagueDetails();
        return;
      }
      setLeagueDetailExpanded(btn);
    });

    leagueRoot.addEventListener('keydown', function (e) {
      if (e.key !== 'Enter' && e.key !== ' ') return;
      var btn = e.target.closest('.sas-league-drill');
      if (!btn || !leagueRoot.contains(btn)) return;
      e.preventDefault();
      btn.click();
    });
  }

  window.SasReport = window.SasReport || {};
  window.SasReport.activateTab = activateTab;
})();
