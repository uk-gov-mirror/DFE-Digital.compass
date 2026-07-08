/**
 * Priority resourcing report — grouped bar chart by priority with directorate views and drill-down table.
 */
(function () {
  'use strict';

  var state = {
    payload: null,
    chart: null,
    activeSectionKey: null
  };

  function ragBadgeClass(rag) {
    var r = (rag || 'Not set').toLowerCase();
    if (r === 'green') return 'dfe-f-badge dfe-f-badge--green dfe-f-badge--small';
    if (r === 'amber-green') return 'dfe-f-badge dfe-f-badge--yellow dfe-f-badge--small';
    if (r === 'amber-red') return 'dfe-f-badge dfe-f-badge--orange dfe-f-badge--small';
    if (r === 'red') return 'dfe-f-badge dfe-f-badge--red dfe-f-badge--small';
    return 'dfe-f-badge dfe-f-badge--grey dfe-f-badge--small';
  }

  function priBadgeClass(pri) {
    var p = (pri || 'Not set').toLowerCase();
    if (p === 'critical') return 'dfe-f-badge dfe-f-badge--red dfe-f-badge--small';
    if (p === 'high') return 'dfe-f-badge dfe-f-badge--orange dfe-f-badge--small';
    if (p === 'medium') return 'dfe-f-badge dfe-f-badge--yellow dfe-f-badge--small';
    if (p === 'low') return 'dfe-f-badge dfe-f-badge--green dfe-f-badge--small';
    return 'dfe-f-badge dfe-f-badge--grey dfe-f-badge--small';
  }

  function escHtml(value) {
    return String(value || '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;');
  }

  function sectionByKey(key) {
    if (!state.payload || !state.payload.sections) return null;
    return state.payload.sections.find(function (s) { return s.key === key; }) || null;
  }

  function workItemById(id) {
    if (!state.payload || !state.payload.workItems) return null;
    return state.payload.workItems[String(id)] || null;
  }

  function buildExportUrl(ids, label) {
    if (!state.payload.exportBaseUrl || !ids || !ids.length) return '';
    var params = new URLSearchParams();
    ids.forEach(function (id) { params.append('ids', String(id)); });
    params.set('label', label || 'priority-resourcing');
    return state.payload.exportBaseUrl + '?' + params.toString();
  }

  function hideDrill() {
    var panel = document.getElementById('pr-drill-panel');
    if (panel) panel.hidden = true;
  }

  function showDrill(section, priority, resourceType) {
    var panel = document.getElementById('pr-drill-panel');
    var titleEl = document.getElementById('pr-drill-title');
    var captionEl = document.getElementById('pr-drill-caption');
    var bodyEl = document.getElementById('pr-drill-body');
    var exportEl = document.getElementById('pr-drill-export');
    if (!panel || !titleEl || !captionEl || !bodyEl || !section || !section.drill) return;

    var drill = section.drill[priority];
    if (!drill) return;

    var ids = resourceType === 'perm' ? drill.perm
      : resourceType === 'msp' ? drill.msp
        : drill.all;

    var resourceLabel = resourceType === 'perm' ? 'Perm FTE'
      : resourceType === 'msp' ? 'MSC FTE'
        : 'all resources';

    titleEl.textContent = section.title + ' — ' + priority + ' priority';
    captionEl.textContent = 'Work items with ' + resourceLabel + ' declared in submitted monthly returns.';

    var rows = (ids || [])
      .map(function (id) { return workItemById(id); })
      .filter(Boolean)
      .sort(function (a, b) {
        var priOrder = { Critical: 0, High: 1, Medium: 2, Low: 3 };
        var ap = priOrder[a.priority] != null ? priOrder[a.priority] : 4;
        var bp = priOrder[b.priority] != null ? priOrder[b.priority] : 4;
        if (ap !== bp) return ap - bp;
        return String(a.title).localeCompare(String(b.title));
      });

    bodyEl.innerHTML = rows.map(function (w) {
      var href = (state.payload.workDetailPrefix || '') + w.id;
      return '<tr class="govuk-table__row">' +
        '<td class="govuk-table__cell"><a class="govuk-link govuk-link--no-visited-state govuk-!-font-weight-bold" href="' + escHtml(href) + '">' + escHtml(w.title) + '</a></td>' +
        '<td class="govuk-table__cell"><span class="' + ragBadgeClass(w.rag) + '">' + escHtml(w.rag) + '</span></td>' +
        '<td class="govuk-table__cell"><span class="' + priBadgeClass(w.priority) + '">' + escHtml(w.priority) + '</span></td>' +
        '<td class="govuk-table__cell govuk-table__cell--numeric">' + Number(w.permFte || 0).toFixed(2).replace(/\.00$/, '') + '</td>' +
        '<td class="govuk-table__cell govuk-table__cell--numeric">' + Number(w.mspFte || 0).toFixed(2).replace(/\.00$/, '') + '</td>' +
        '<td class="govuk-table__cell">' + escHtml(w.businessArea) + '</td>' +
        '<td class="govuk-table__cell">' + escHtml(w.directorates) + '</td>' +
        '</tr>';
    }).join('');

    if (exportEl) {
      var exportUrl = buildExportUrl(ids, 'priority-resourcing-' + section.key + '-' + priority + '-' + resourceType);
      if (exportUrl) {
        exportEl.href = exportUrl;
        exportEl.hidden = false;
      } else {
        exportEl.hidden = true;
      }
    }

    panel.hidden = false;
    panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  function renderChart(sectionKey) {
    var section = sectionByKey(sectionKey);
    var canvas = document.getElementById('pr-priority-chart');
    if (!section || !canvas || typeof Chart === 'undefined') return;

    state.activeSectionKey = sectionKey;

    if (state.chart) {
      state.chart.destroy();
      state.chart = null;
    }

    var permDataset = {
      label: 'Perm FTE',
      data: section.perm.map(function (v) { return Number(v || 0); }),
      backgroundColor: '#00703c',
      resourceType: 'perm'
    };
    var mspDataset = {
      label: 'MSC FTE',
      data: section.msp.map(function (v) { return Number(v || 0); }),
      backgroundColor: '#1d70b8',
      resourceType: 'msp'
    };

    state.chart = new Chart(canvas.getContext('2d'), {
      type: 'bar',
      data: {
        labels: section.labels,
        datasets: [permDataset, mspDataset]
      },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        interaction: { mode: 'nearest', intersect: true },
        onClick: function (_evt, elements) {
          if (!elements || !elements.length) return;
          var el = elements[0];
          var priority = section.labels[el.index];
          var dataset = state.chart.data.datasets[el.datasetIndex];
          var resourceType = dataset.resourceType || 'all';
          showDrill(section, priority, resourceType);
        },
        plugins: {
          legend: { position: 'bottom' },
          tooltip: {
            callbacks: {
              footer: function () { return 'Click to view work items'; }
            }
          }
        },
        scales: {
          x: {
            title: { display: true, text: 'Delivery priority' }
          },
          y: {
            beginAtZero: true,
            title: { display: true, text: 'FTE count' },
            ticks: {
              callback: function (value) {
                var n = Number(value);
                return Number.isInteger(n) ? n : n.toFixed(1);
              }
            }
          }
        }
      }
    });
  }

  function bindTabs() {
    var tabs = document.querySelectorAll('[data-pr-section]');
    tabs.forEach(function (btn) {
      btn.addEventListener('click', function () {
        var key = btn.getAttribute('data-pr-section');
        tabs.forEach(function (b) {
          var active = b === btn;
          b.classList.toggle('pr-directorate-tabs__btn--active', active);
          b.setAttribute('aria-selected', active ? 'true' : 'false');
        });
        hideDrill();
        renderChart(key);
      });
    });
  }

  function bindClose() {
    var closeBtn = document.getElementById('pr-drill-close');
    if (closeBtn) closeBtn.addEventListener('click', hideDrill);
  }

  function init(payload) {
    state.payload = payload || {};
    if (!state.payload.sections || !state.payload.sections.length) return;
    bindTabs();
    bindClose();
    renderChart(state.payload.sections[0].key);
  }

  window.PriorityResourcingReport = { init: init };
})();
