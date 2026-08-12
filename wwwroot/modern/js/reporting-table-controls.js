/* global document, localStorage */
(function () {
  'use strict';

  var ZOOM_DEFAULT = 1;
  var ZOOM_MIN = 0.75;
  var ZOOM_MAX = 1.5;
  var ZOOM_STEP = 0.1;
  var STORAGE_PREFIX = 'compass.reportingTable.zoom.v1.';

  function run() {
    document.querySelectorAll('[data-reporting-table-region]').forEach(initRegion);
  }

  function initRegion(region) {
    var scroll = region.querySelector('[data-reporting-table-scroll]');
    if (!scroll) return;

    var tableId = region.getAttribute('data-table-id') || 'default';
    var storageKey = STORAGE_PREFIX + tableId;

    function getZoom() {
      var z = parseFloat(scroll.style.zoom);
      return isNaN(z) || z <= 0 ? ZOOM_DEFAULT : z;
    }

    function applyZoom(zoom) {
      var level = Math.round(zoom * 100) / 100;
      level = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, level));
      scroll.style.zoom = level === ZOOM_DEFAULT ? '' : String(level);
      try {
        if (level === ZOOM_DEFAULT) {
          localStorage.removeItem(storageKey);
        } else {
          localStorage.setItem(storageKey, String(level));
        }
      } catch (e) { /* localStorage unavailable */ }
      return level;
    }

    function restoreZoom() {
      try {
        var stored = localStorage.getItem(storageKey);
        if (stored) {
          var level = parseFloat(stored);
          if (!isNaN(level)) applyZoom(level);
        }
      } catch (e) { /* parse error */ }
    }

    restoreZoom();

    var zoomIn = region.querySelector('.mr-report-zoom-in-btn');
    var zoomOut = region.querySelector('.mr-report-zoom-out-btn');
    var zoomReset = region.querySelector('.mr-report-zoom-reset-btn');
    var fullscreenBtn = region.querySelector('.mr-report-table-fullscreen-btn');

    if (zoomIn) {
      zoomIn.addEventListener('click', function () {
        applyZoom(getZoom() + ZOOM_STEP);
      });
    }

    if (zoomOut) {
      zoomOut.addEventListener('click', function () {
        applyZoom(getZoom() - ZOOM_STEP);
      });
    }

    if (zoomReset) {
      zoomReset.addEventListener('click', function () {
        applyZoom(ZOOM_DEFAULT);
      });
    }

    if (fullscreenBtn) {
      fullscreenBtn.addEventListener('click', function () {
        toggleFullscreen(region, fullscreenBtn);
      });
    }
  }

  function toggleFullscreen(region, btn) {
    var isFs = region.classList.toggle('mr-report-table-region--fullscreen');
    region.querySelectorAll('.btn-fs-expand').forEach(function (el) {
      el.style.display = isFs ? 'none' : '';
    });
    region.querySelectorAll('.btn-fs-collapse').forEach(function (el) {
      el.style.display = isFs ? '' : 'none';
    });
    if (btn) {
      btn.classList.toggle('raid-ss-btn-active', isFs);
    }
    document.body.style.overflow = isFs ? 'hidden' : '';
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', run);
  } else {
    run();
  }
})();
