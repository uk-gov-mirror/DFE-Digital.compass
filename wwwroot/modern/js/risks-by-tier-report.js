/* global document, window */
(function () {
  'use strict';

  function run() {
    var configEl = document.getElementById('rbt-drilldown-config');
    if (!configEl || !configEl.textContent) return;

    var config;
    try {
      config = JSON.parse(configEl.textContent);
    } catch (e) {
      return;
    }

    var risksByKey = config.risksByKey || {};
    var risksById = config.risksById || {};
    var panel = document.getElementById('rbt-matrix-drilldown');
    var titleEl = document.getElementById('rbt-matrix-drilldown-title');
    var bodyEl = document.getElementById('rbt-matrix-drilldown-body');
    var activeCell = null;

    if (window.RaidRiskInfoModal) {
      window.RaidRiskInfoModal.init({
        likelihoodScale: config.likelihoodScale || [],
        impactScale: config.impactScale || [],
        canActionTierChanges: config.canActionTierChanges === true
      });
    }

    function escHtml(s) {
      var d = document.createElement('div');
      d.appendChild(document.createTextNode(s == null ? '' : String(s)));
      return d.innerHTML;
    }

    function likelihoodImpact(text) {
      if (!text) return '—';
      return escHtml(text);
    }

    function fmtDate(dt) {
      if (!dt) return '—';
      try {
        var d = new Date(dt);
        if (Number.isNaN(d.getTime())) return '—';
        return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
      } catch (err) {
        return '—';
      }
    }

    function buildTitle(dirKey, tierKey, label) {
      var parts = [];
      if (label) parts.push(label);
      else {
        if (dirKey !== '*') parts.push('Directorate filter');
        if (tierKey !== '*') parts.push('Tier filter');
        if (dirKey === '*' && tierKey === '*') parts.push('All risks in view');
      }
      return parts.join(' — ');
    }

    function renderRiskTitleCell(r) {
      var closedBadge = r.isClosed ? '<span class="dfe-f-badge dfe-f-badge--small">Closed</span>' : '';
      return (
        '<button type="button" class="govuk-link govuk-link--no-visited-state govuk-!-font-weight-bold rbt-risk-title-btn" data-rbt-risk-id="' + escHtml(String(r.id)) + '" aria-label="View details for ' + escHtml(r.title) + '">' + escHtml(r.title) + '</button>' +
        '<div class="govuk-hint govuk-!-margin-bottom-0" style="font-size:0.875rem;">' + escHtml(r.reference) + '</div>' +
        closedBadge
      );
    }

    function renderRows(risks) {
      if (!bodyEl) return;
      if (!risks || !risks.length) {
        bodyEl.innerHTML = '<tr class="govuk-table__row"><td class="govuk-table__cell" colspan="11">No risks for this selection.</td></tr>';
        return;
      }

      bodyEl.innerHTML = risks.map(function (r) {
        var workCell = r.workItemUrl
          ? '<a class="govuk-link govuk-link--no-visited-state" href="' + escHtml(r.workItemUrl) + '">' + escHtml(r.workItemOrProject) + '</a>'
          : escHtml(r.workItemOrProject);
        var daysCell = r.daysSinceLastUpdate >= 30 && !r.isClosed
          ? '<span class="dfe-f-badge dfe-f-badge--small dfe-f-badge--red">' + r.daysSinceLastUpdate + '</span>'
          : String(r.daysSinceLastUpdate);
        return (
          '<tr class="govuk-table__row">' +
          '<td class="govuk-table__cell">' + renderRiskTitleCell(r) + '</td>' +
          '<td class="govuk-table__cell">' + likelihoodImpact(r.residualLikelihoodImpact) + '</td>' +
          '<td class="govuk-table__cell">' + likelihoodImpact(r.currentLikelihoodImpact) + '</td>' +
          '<td class="govuk-table__cell">' + likelihoodImpact(r.inherentLikelihoodImpact) + '</td>' +
          '<td class="govuk-table__cell">' + escHtml(r.mitigation || '—') + '</td>' +
          '<td class="govuk-table__cell">' + escHtml(fmtDate(r.lastReviewedAt)) + '</td>' +
          '<td class="govuk-table__cell">' + escHtml(fmtDate(r.lastUpdatedAt)) + '</td>' +
          '<td class="govuk-table__cell">' + escHtml(fmtDate(r.createdAt)) + '</td>' +
          '<td class="govuk-table__cell govuk-table__cell--numeric">' + daysCell + '</td>' +
          '<td class="govuk-table__cell">' + workCell + '</td>' +
          '<td class="govuk-table__cell">' + escHtml(r.directorate) + '</td>' +
          '</tr>'
        );
      }).join('');
    }

    function riskFromId(id) {
      if (id == null || id === '') return null;
      return risksById[id] || risksById[String(id)] || null;
    }

    function showDrill(cell) {
      var dirKey = cell.getAttribute('data-rbt-dir') || '*';
      var tierKey = cell.getAttribute('data-rbt-tier') || '*';
      var label = cell.getAttribute('data-rbt-label') || '';
      var key = dirKey + '|' + tierKey;
      var risks = risksByKey[key] || [];

      if (activeCell) activeCell.classList.remove('rbt-matrix-drill--active');
      activeCell = cell;
      cell.classList.add('rbt-matrix-drill--active');

      if (titleEl) {
        titleEl.textContent = buildTitle(dirKey, tierKey, label) + ' (' + risks.length + ')';
      }
      renderRows(risks);

      if (panel) {
        panel.hidden = false;
        panel.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
      }
    }

    function hideDrill() {
      if (activeCell) {
        activeCell.classList.remove('rbt-matrix-drill--active');
        activeCell = null;
      }
      if (panel) panel.hidden = true;
      if (bodyEl) bodyEl.innerHTML = '';
      if (titleEl) titleEl.textContent = '';
    }

    document.querySelectorAll('.rbt-matrix-drill').forEach(function (el) {
      el.addEventListener('click', function () { showDrill(el); });
      el.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault();
          showDrill(el);
        }
      });
    });

    var closeBtn = panel && panel.querySelector('.rbt-matrix-drilldown__close');
    if (closeBtn) closeBtn.addEventListener('click', hideDrill);

    document.addEventListener('click', function (e) {
      var titleBtn = e.target.closest('.rbt-risk-title-btn[data-rbt-risk-id]');
      if (!titleBtn) return;
      e.preventDefault();
      var risk = riskFromId(titleBtn.getAttribute('data-rbt-risk-id'));
      if (risk && window.RaidRiskInfoModal) window.RaidRiskInfoModal.open(risk);
    });
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', run);
  } else {
    run();
  }
})();
