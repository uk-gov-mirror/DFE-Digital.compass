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
    var likelihoodScale = config.likelihoodScale || [];
    var impactScale = config.impactScale || [];
    var canActionTierChanges = config.canActionTierChanges === true;
    var panel = document.getElementById('rbt-matrix-drilldown');
    var titleEl = document.getElementById('rbt-matrix-drilldown-title');
    var bodyEl = document.getElementById('rbt-matrix-drilldown-body');
    var activeCell = null;

    var modal = document.getElementById('rbt-risk-modal');
    var modalTitle = document.getElementById('rbt-risk-modal-title');
    var modalReference = document.getElementById('rbt-risk-modal-reference');
    var modalViewLink = document.getElementById('rbt-risk-modal-view-link');
    var modalActions = document.getElementById('rbt-risk-modal-actions');
    var overviewList = document.getElementById('rbt-risk-overview-list');
    var ratingsContent = document.getElementById('rbt-risk-ratings-content');
    var descriptionText = document.getElementById('rbt-risk-description-text');
    var mitigationText = document.getElementById('rbt-risk-mitigation-text');

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

    function fmtScore(score) {
      if (score == null || score === '') return '—';
      var n = Number(score);
      return Number.isFinite(n) ? String(Math.round(n)) : '—';
    }

    function scoreBandClass(score) {
      if (score == null || score === '') return '';
      var s = Number(score);
      if (!Number.isFinite(s)) return '';
      if (s >= 20) return 'raid-ss-score-badge--highest';
      if (s >= 15) return 'raid-ss-score-badge--elevated';
      if (s >= 8) return 'raid-ss-score-badge--medium';
      return 'raid-ss-score-badge--lower';
    }

    function bandOrFallback(band, scoreKey, pairKey, risk) {
      if (band && (band.likelihoodLabel || band.impactLabel || band.likelihoodIndex || band.impactIndex)) {
        return band;
      }
      return {
        score: risk[scoreKey],
        likelihoodLabel: '—',
        impactLabel: '—',
        likelihoodIndex: 0,
        impactIndex: 0,
        pairText: risk[pairKey]
      };
    }

    function parsePairIndex(pairText, scale) {
      if (!pairText || pairText === '—' || !scale || !scale.length) return 0;
      var parts = String(pairText).split('×');
      if (parts.length < 1) return 0;
      var label = parts[0].trim().toLowerCase();
      for (var i = 0; i < scale.length; i++) {
        if (String(scale[i]).trim().toLowerCase() === label) return i + 1;
      }
      return 0;
    }

    function parsePairImpactIndex(pairText, scale) {
      if (!pairText || pairText === '—' || !scale || !scale.length) return 0;
      var parts = String(pairText).split('×');
      if (parts.length < 2) return 0;
      var label = parts[1].trim().toLowerCase();
      for (var i = 0; i < scale.length; i++) {
        if (String(scale[i]).trim().toLowerCase() === label) return i + 1;
      }
      return 0;
    }

    function renderRatingScale(dimensionLabel, labels, activeIndex) {
      if (!labels || !labels.length) return '';
      var tags = labels.map(function (label, idx) {
        var active = activeIndex === idx + 1;
        var cls = 'rbt-rating-scale__tag' + (active ? ' rbt-rating-scale__tag--active' : '');
        var aria = active ? ' aria-current="true"' : '';
        return '<span class="' + cls + '"' + aria + '>' + escHtml(label) + '</span>';
      }).join('');
      return (
        '<div class="rbt-rating-scale">' +
        '<span class="rbt-rating-scale__label">' + escHtml(dimensionLabel) + '</span>' +
        '<div class="rbt-rating-scale__tags" role="list">' + tags + '</div>' +
        '</div>'
      );
    }

    function renderRatingBand(title, summary, band) {
      var scoreHtml = '—';
      if (band.score != null && band.score !== '') {
        var bandCls = scoreBandClass(band.score);
        scoreHtml = '<span class="raid-ss-score-badge ' + escHtml(bandCls) + '">' + escHtml(fmtScore(band.score)) + '/25</span>';
      }

      var likIdx = band.likelihoodIndex || 0;
      var impIdx = band.impactIndex || 0;

      return (
        '<section class="rbt-rating-band" aria-label="' + escHtml(title) + ' rating">' +
        '<div class="rbt-rating-band__header">' +
        '<h3 class="govuk-heading-s rbt-rating-band__title">' + escHtml(title) + '</h3>' +
        scoreHtml +
        '</div>' +
        '<p class="govuk-body-s rbt-rating-band__summary">' + escHtml(summary) + '</p>' +
        renderRatingScale('Likelihood', likelihoodScale, likIdx) +
        renderRatingScale('Impact', impactScale, impIdx) +
        '</section>'
      );
    }

    function renderRatingsPane(risk) {
      if (!ratingsContent) return;

      var inherent = bandOrFallback(risk.inherent, 'inherentScore', 'inherentLikelihoodImpact', risk);
      var current = bandOrFallback(risk.current, 'currentScore', 'currentLikelihoodImpact', risk);
      var residual = bandOrFallback(risk.residual, 'residualScore', 'residualLikelihoodImpact', risk);

      if (!inherent.likelihoodIndex && risk.inherentLikelihoodImpact) {
        inherent.likelihoodIndex = parsePairIndex(risk.inherentLikelihoodImpact, likelihoodScale);
        inherent.impactIndex = parsePairImpactIndex(risk.inherentLikelihoodImpact, impactScale);
      }
      if (!current.likelihoodIndex && risk.currentLikelihoodImpact) {
        current.likelihoodIndex = parsePairIndex(risk.currentLikelihoodImpact, likelihoodScale);
        current.impactIndex = parsePairImpactIndex(risk.currentLikelihoodImpact, impactScale);
      }
      if (!residual.likelihoodIndex && risk.residualLikelihoodImpact) {
        residual.likelihoodIndex = parsePairIndex(risk.residualLikelihoodImpact, likelihoodScale);
        residual.impactIndex = parsePairImpactIndex(risk.residualLikelihoodImpact, impactScale);
      }

      var trend = risk.scoreTrend || 'stable';
      var trendSummary = risk.scoreTrendSummary ||
        'Inherent is the first assessment when the risk was recorded. Current reflects the situation now. Residual is what remains after controls and mitigation.';

      ratingsContent.innerHTML =
        '<div class="rbt-rating-trend rbt-rating-trend--' + escHtml(trend) + '">' +
        '<p class="govuk-body-s govuk-!-margin-bottom-0">' + escHtml(trendSummary) + '</p>' +
        '</div>' +
        renderRatingBand(
          'Inherent',
          'First assessment when the risk was recorded.',
          inherent
        ) +
        renderRatingBand(
          'Current',
          'Assessment of the situation now, before considering controls.',
          current
        ) +
        renderRatingBand(
          'Residual',
          'What remains after controls and mitigation.',
          residual
        );
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

    function overviewRow(label, value) {
      return (
        '<div class="govuk-summary-list__row">' +
        '<dt class="govuk-summary-list__key">' + escHtml(label) + '</dt>' +
        '<dd class="govuk-summary-list__value">' + escHtml(value || '—') + '</dd>' +
        '</div>'
      );
    }

    function setPaneText(el, text) {
      if (!el) return;
      el.textContent = text && String(text).trim() ? String(text).trim() : '—';
    }

    function switchTab(tabKey) {
      if (!modal) return;
      modal.querySelectorAll('[data-rbt-risk-tab]').forEach(function (tab) {
        var on = tab.getAttribute('data-rbt-risk-tab') === tabKey;
        tab.setAttribute('aria-selected', on ? 'true' : 'false');
      });
      modal.querySelectorAll('.mr-reporting-modal__pane').forEach(function (pane) {
        var on = pane.id === 'rbt-risk-pane-' + tabKey;
        pane.classList.toggle('is-active', on);
        pane.hidden = !on;
      });
    }

    function riskFromId(id) {
      if (id == null || id === '') return null;
      return risksById[id] || risksById[String(id)] || null;
    }

    function renderModalFooterActions(risk) {
      if (!modalActions) return;

      if (!canActionTierChanges) {
        modalActions.innerHTML = '';
        modalActions.hidden = true;
        return;
      }

      var links = [];
      if (risk.escalationActionUrl) {
        links.push(
          '<a class="govuk-button govuk-button--secondary govuk-!-margin-bottom-0" href="' +
          escHtml(risk.escalationActionUrl) +
          '">Action escalation</a>'
        );
      }
      if (risk.deescalationActionUrl) {
        links.push(
          '<a class="govuk-button govuk-button--secondary govuk-!-margin-bottom-0" href="' +
          escHtml(risk.deescalationActionUrl) +
          '">Action de-escalation</a>'
        );
      }

      if (links.length) {
        modalActions.innerHTML = links.join('');
        modalActions.hidden = false;
      } else {
        modalActions.innerHTML = '';
        modalActions.hidden = true;
      }
    }

    function openRiskModal(risk) {
      if (!modal || !risk) return;

      if (modalTitle) modalTitle.textContent = risk.title || 'Risk';
      if (modalReference) modalReference.textContent = risk.reference || '';
      if (modalViewLink) modalViewLink.href = risk.detailUrl || '#';

      if (overviewList) {
        overviewList.innerHTML =
          overviewRow('Status', risk.status) +
          overviewRow('Tier', risk.tierName) +
          overviewRow('Owner', risk.owner) +
          overviewRow('Directorate', risk.directorate) +
          overviewRow('Work item / project', risk.workItemOrProject) +
          overviewRow('Last reviewed', fmtDate(risk.lastReviewedAt)) +
          overviewRow('Last updated', fmtDate(risk.lastUpdatedAt)) +
          overviewRow('Created', fmtDate(risk.createdAt)) +
          overviewRow('Days since update', String(risk.daysSinceLastUpdate == null ? '—' : risk.daysSinceLastUpdate));
      }

      renderRatingsPane(risk);
      renderModalFooterActions(risk);

      setPaneText(descriptionText, risk.description);
      setPaneText(mitigationText, risk.mitigationFull || risk.mitigation);
      switchTab('overview');
      modal.hidden = false;
      modal.classList.add('is-open');
      document.body.style.overflow = 'hidden';
    }

    function closeRiskModal() {
      if (!modal) return;
      modal.hidden = true;
      modal.classList.remove('is-open');
      document.body.style.overflow = '';
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
      if (risk) openRiskModal(risk);
    });

    if (modal) {
      modal.querySelectorAll('[data-rbt-risk-modal-close]').forEach(function (el) {
        el.addEventListener('click', closeRiskModal);
      });
      modal.querySelectorAll('[data-rbt-risk-tab]').forEach(function (tab) {
        tab.addEventListener('click', function () {
          switchTab(tab.getAttribute('data-rbt-risk-tab'));
        });
      });
      document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape' && modal.classList.contains('is-open')) closeRiskModal();
      });
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', run);
  } else {
    run();
  }
})();
