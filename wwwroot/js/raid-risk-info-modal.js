/* global document, window */
(function (global) {
  'use strict';

  var likelihoodScale = [];
  var impactScale = [];
  var canActionTierChanges = false;
  var bound = false;

  function escHtml(s) {
    var d = document.createElement('div');
    d.appendChild(document.createTextNode(s == null ? '' : String(s)));
    return d.innerHTML;
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

  function labelScaleIndex(scale, label) {
    if (!label || label === '—' || !scale || !scale.length) return 0;
    var s = String(label).trim().toLowerCase();
    for (var i = 0; i < scale.length; i++) {
      if (String(scale[i]).trim().toLowerCase() === s) return i + 1;
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

  function fillBandIndexes(band, pairText) {
    if (!band.likelihoodIndex && (band.likelihoodLabel || pairText)) {
      band.likelihoodIndex = labelScaleIndex(likelihoodScale, band.likelihoodLabel) ||
        parsePairIndex(pairText, likelihoodScale);
    }
    if (!band.impactIndex && (band.impactLabel || pairText)) {
      band.impactIndex = labelScaleIndex(impactScale, band.impactLabel) ||
        parsePairImpactIndex(pairText, impactScale);
    }
    return band;
  }

  function renderRatingsPane(risk) {
    var ratingsContent = document.getElementById('rbt-risk-ratings-content');
    if (!ratingsContent) return;

    var inherent = fillBandIndexes(
      bandOrFallback(risk.inherent, 'inherentScore', 'inherentLikelihoodImpact', risk),
      risk.inherentLikelihoodImpact
    );
    var current = fillBandIndexes(
      bandOrFallback(risk.current, 'currentScore', 'currentLikelihoodImpact', risk),
      risk.currentLikelihoodImpact
    );
    var residual = fillBandIndexes(
      bandOrFallback(risk.residual, 'residualScore', 'residualLikelihoodImpact', risk),
      risk.residualLikelihoodImpact
    );

    var trend = risk.scoreTrend || 'stable';
    var trendSummary = risk.scoreTrendSummary ||
      'Inherent is the first assessment when the risk was recorded. Current reflects the situation now. Residual is what remains after controls and mitigation.';

    ratingsContent.innerHTML =
      '<div class="rbt-rating-trend rbt-rating-trend--' + escHtml(trend) + '">' +
      '<p class="govuk-body-s govuk-!-margin-bottom-0">' + escHtml(trendSummary) + '</p>' +
      '</div>' +
      renderRatingBand('Inherent', 'First assessment when the risk was recorded.', inherent) +
      renderRatingBand('Current', 'Assessment of the situation now, before considering controls.', current) +
      renderRatingBand('Residual', 'What remains after controls and mitigation.', residual);
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
    var modal = document.getElementById('rbt-risk-modal');
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

  function renderModalFooterActions(risk) {
    var modalActions = document.getElementById('rbt-risk-modal-actions');
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

  function open(risk) {
    var modal = document.getElementById('rbt-risk-modal');
    if (!modal || !risk) return;

    var modalTitle = document.getElementById('rbt-risk-modal-title');
    var modalReference = document.getElementById('rbt-risk-modal-reference');
    var modalViewLink = document.getElementById('rbt-risk-modal-view-link');
    var overviewList = document.getElementById('rbt-risk-overview-list');
    var descriptionText = document.getElementById('rbt-risk-description-text');
    var mitigationText = document.getElementById('rbt-risk-mitigation-text');

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

  function close() {
    var modal = document.getElementById('rbt-risk-modal');
    if (!modal) return;
    modal.hidden = true;
    modal.classList.remove('is-open');
    document.body.style.overflow = '';
  }

  function bind() {
    if (bound) return;
    var modal = document.getElementById('rbt-risk-modal');
    if (!modal) return;
    bound = true;

    modal.querySelectorAll('[data-rbt-risk-modal-close]').forEach(function (el) {
      el.addEventListener('click', close);
    });
    modal.querySelectorAll('[data-rbt-risk-tab]').forEach(function (tab) {
      tab.addEventListener('click', function () {
        switchTab(tab.getAttribute('data-rbt-risk-tab'));
      });
    });
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape' && modal.classList.contains('is-open')) close();
    });
  }

  function init(options) {
    options = options || {};
    likelihoodScale = options.likelihoodScale || [];
    impactScale = options.impactScale || [];
    canActionTierChanges = options.canActionTierChanges === true;
    bind();
  }

  global.RaidRiskInfoModal = {
    init: init,
    open: open,
    close: close
  };
})(window);
