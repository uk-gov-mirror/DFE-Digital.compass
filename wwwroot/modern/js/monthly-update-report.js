/**
 * Monthly report — section order and toggle expanded state (localStorage), edit-view reorder UI.
 */
(function () {
  var STORAGE_KEY = 'compass.mr.sectionOrder.v1';
  var EXPANDED_STORAGE_KEY = 'compass.mr.sectionExpanded.v1';
  var DEFAULT_ORDER = [
    'submission',
    'intelligence',
    'active-work-items',
    'business-area',
    'changes',
    'trends',
    'milestones',
    'raid',
    'accessibility'
  ];

  function readStoredOrder() {
    try {
      var raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      var parsed = JSON.parse(raw);
      return Array.isArray(parsed) && parsed.length ? parsed : null;
    } catch (e) {
      return null;
    }
  }

  function normalizeOrder(order, knownIds) {
    var seen = {};
    var result = [];
    order.forEach(function (id) {
      if (knownIds.indexOf(id) !== -1 && !seen[id]) {
        seen[id] = true;
        result.push(id);
      }
    });
    knownIds.forEach(function (id) {
      if (!seen[id]) result.push(id);
    });
    return result;
  }

  function getItemsContainer() {
    return document.querySelector('#mr-report-sections .dfe-f-toggle-group__items');
  }

  function getPanelsMap(container) {
    var map = {};
    container.querySelectorAll('[data-mr-section-id]').forEach(function (el) {
      map[el.getAttribute('data-mr-section-id')] = el;
    });
    return map;
  }

  function applyOrderToDom(order) {
    var container = getItemsContainer();
    if (!container) return;
    var panels = getPanelsMap(container);
    var knownIds = Object.keys(panels);
    var normalized = normalizeOrder(order, knownIds);
    normalized.forEach(function (id) {
      if (panels[id]) container.appendChild(panels[id]);
    });
    return normalized;
  }

  function panelTitle(el) {
    return el.getAttribute('data-mr-section-title')
      || (el.querySelector('.dfe-f-toggle__title') && el.querySelector('.dfe-f-toggle__title').textContent.trim())
      || el.getAttribute('data-mr-section-id')
      || 'Section';
  }

  function setToggleExpandedFn() {
    return window.DfeFrontend && typeof window.DfeFrontend.setToggleExpanded === 'function'
      ? window.DfeFrontend.setToggleExpanded
      : null;
  }

  function getToggleStorageKey(toggleEl) {
    var sectionId = toggleEl.getAttribute('data-mr-section-id');
    if (sectionId) return sectionId;
    var btn = toggleEl.querySelector('.dfe-f-toggle__button');
    if (btn && btn.id) return 'btn:' + btn.id;
    return null;
  }

  function isToggleExpanded(toggleEl) {
    var btn = toggleEl.querySelector('.dfe-f-toggle__button');
    return btn && btn.getAttribute('aria-expanded') === 'true';
  }

  function readStoredExpanded() {
    try {
      var raw = localStorage.getItem(EXPANDED_STORAGE_KEY);
      if (!raw) return null;
      var parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? parsed : null;
    } catch (e) {
      return null;
    }
  }

  function writeStoredExpanded(state) {
    try {
      localStorage.setItem(EXPANDED_STORAGE_KEY, JSON.stringify(state));
    } catch (e) { /* ignore quota errors */ }
  }

  function collectExpandedState(reportGroup) {
    var state = {};
    reportGroup.querySelectorAll('.dfe-f-toggle').forEach(function (el) {
      var key = getToggleStorageKey(el);
      if (key) state[key] = isToggleExpanded(el);
    });
    return state;
  }

  function persistExpandedState(reportGroup) {
    writeStoredExpanded(collectExpandedState(reportGroup));
  }

  /** First visit: collapse all report sections except submission summary. */
  function applyDefaultSectionExpandedState(reportGroup) {
    if (!reportGroup) return;
    var setExpanded = setToggleExpandedFn();
    if (!setExpanded) return;
    reportGroup.querySelectorAll('.dfe-f-toggle-group__items > .dfe-f-toggle[data-mr-section-id]').forEach(function (el) {
      setExpanded(el, el.getAttribute('data-mr-section-id') === 'submission');
    });
  }

  function applyStoredExpandedState(reportGroup, stored) {
    if (!reportGroup || !stored) return;
    var setExpanded = setToggleExpandedFn();
    if (!setExpanded) return;
    reportGroup.querySelectorAll('.dfe-f-toggle').forEach(function (el) {
      var key = getToggleStorageKey(el);
      if (!key || !Object.prototype.hasOwnProperty.call(stored, key)) return;
      setExpanded(el, !!stored[key]);
    });
  }

  function milestoneTogglesSetExpanded(reportGroup, expanded) {
    var setExpanded = setToggleExpandedFn();
    if (!setExpanded) return;
    reportGroup.querySelectorAll('.mr-milestone-groups .dfe-f-toggle').forEach(function (el) {
      setExpanded(el, expanded);
    });
  }

  function bindExpandedPersistence(reportGroup) {
    reportGroup.addEventListener('click', function (e) {
      if (!e.target.closest('.dfe-f-toggle__button')) return;
      persistExpandedState(reportGroup);
    });

    var expandAll = reportGroup.querySelector('[data-dfe-toggle-expand-all]');
    var collapseAll = reportGroup.querySelector('[data-dfe-toggle-collapse-all]');

    if (expandAll) {
      expandAll.addEventListener('click', function () {
        milestoneTogglesSetExpanded(reportGroup, true);
        persistExpandedState(reportGroup);
      });
    }

    if (collapseAll) {
      collapseAll.addEventListener('click', function () {
        milestoneTogglesSetExpanded(reportGroup, false);
        var setExpanded = setToggleExpandedFn();
        var submissionPanel = reportGroup.querySelector('[data-mr-section-id="submission"]');
        if (submissionPanel && setExpanded) {
          setExpanded(submissionPanel, true);
        }
        persistExpandedState(reportGroup);
      });
    }
  }

  function init() {
    var layout = document.getElementById('mr-report-layout');
    var reportGroup = document.getElementById('mr-report-sections');
    var orderPanel = document.getElementById('mr-report-order-panel');
    var orderList = document.getElementById('mr-report-order-list');
    var editBtn = document.getElementById('mr-report-edit-view');
    var saveBtn = document.getElementById('mr-report-order-save');
    var cancelBtn = document.getElementById('mr-report-order-cancel');
    if (!layout || !reportGroup || !orderPanel || !orderList) return;

    var container = getItemsContainer();
    if (!container) return;

    var panels = getPanelsMap(container);
    var knownIds = Object.keys(panels);
    var currentOrder = normalizeOrder(readStoredOrder() || DEFAULT_ORDER, knownIds);
    applyOrderToDom(currentOrder);

    var storedExpanded = readStoredExpanded();
    if (storedExpanded) {
      applyStoredExpandedState(reportGroup, storedExpanded);
    } else {
      applyDefaultSectionExpandedState(reportGroup);
    }
    bindExpandedPersistence(reportGroup);

    function jumpToSection(sectionId) {
      if (!sectionId) return;
      var panel = reportGroup.querySelector('[data-mr-section-id="' + sectionId + '"]');
      if (!panel) return;
      var setExpanded = setToggleExpandedFn();
      if (setExpanded) setExpanded(panel, true);
      panel.scrollIntoView({ behavior: 'smooth', block: 'start' });
      persistExpandedState(reportGroup);
    }

    document.querySelectorAll('[data-mr-jump-section]').forEach(function (el) {
      el.addEventListener('click', function () {
        jumpToSection(el.getAttribute('data-mr-jump-section'));
      });
    });

    var draftOrder = currentOrder.slice();
    var editing = false;

    function renderOrderList() {
      orderList.innerHTML = '';
      draftOrder.forEach(function (id, index) {
        var panel = panels[id];
        if (!panel) return;
        var li = document.createElement('li');
        li.className = 'mr-report-order-list__item';
        li.setAttribute('data-mr-order-id', id);
        li.draggable = true;

        var label = document.createElement('span');
        label.className = 'mr-report-order-list__label';
        label.textContent = panelTitle(panel);

        var actions = document.createElement('span');
        actions.className = 'mr-report-order-list__actions';

        function makeBtn(text, aria, disabled, handler) {
          var btn = document.createElement('button');
          btn.type = 'button';
          btn.className = 'govuk-button govuk-button--secondary govuk-!-margin-bottom-0 mr-report-order-list__btn';
          btn.textContent = text;
          btn.setAttribute('aria-label', aria);
          btn.disabled = disabled;
          btn.addEventListener('click', handler);
          return btn;
        }

        actions.appendChild(makeBtn('↑', 'Move ' + panelTitle(panel) + ' up', index === 0, function () {
          if (index === 0) return;
          var tmp = draftOrder[index - 1];
          draftOrder[index - 1] = draftOrder[index];
          draftOrder[index] = tmp;
          renderOrderList();
        }));
        actions.appendChild(makeBtn('↓', 'Move ' + panelTitle(panel) + ' down', index === draftOrder.length - 1, function () {
          if (index >= draftOrder.length - 1) return;
          var tmp = draftOrder[index + 1];
          draftOrder[index + 1] = draftOrder[index];
          draftOrder[index] = tmp;
          renderOrderList();
        }));

        li.appendChild(label);
        li.appendChild(actions);

        li.addEventListener('dragstart', function (e) {
          li.classList.add('mr-report-order-list__item--dragging');
          e.dataTransfer.setData('text/plain', id);
          e.dataTransfer.effectAllowed = 'move';
        });
        li.addEventListener('dragend', function () {
          li.classList.remove('mr-report-order-list__item--dragging');
        });
        li.addEventListener('dragover', function (e) {
          e.preventDefault();
          e.dataTransfer.dropEffect = 'move';
        });
        li.addEventListener('drop', function (e) {
          e.preventDefault();
          var fromId = e.dataTransfer.getData('text/plain');
          var toId = id;
          if (!fromId || fromId === toId) return;
          var fromIdx = draftOrder.indexOf(fromId);
          var toIdx = draftOrder.indexOf(toId);
          if (fromIdx < 0 || toIdx < 0) return;
          draftOrder.splice(fromIdx, 1);
          draftOrder.splice(toIdx, 0, fromId);
          renderOrderList();
        });

        orderList.appendChild(li);
      });
    }

    function setEditing(on) {
      editing = on;
      layout.classList.toggle('mr-report-layout--editing', on);
      orderPanel.hidden = !on;
      if (editBtn) {
        editBtn.setAttribute('aria-pressed', on ? 'true' : 'false');
        editBtn.textContent = on ? 'Done reordering' : 'Reorder sections';
      }
      if (on) {
        draftOrder = currentOrder.slice();
        renderOrderList();
      }
    }

    if (editBtn) {
      editBtn.addEventListener('click', function () {
        setEditing(!editing);
      });
    }

    if (saveBtn) {
      saveBtn.addEventListener('click', function () {
        currentOrder = normalizeOrder(draftOrder, knownIds);
        applyOrderToDom(currentOrder);
        try {
          localStorage.setItem(STORAGE_KEY, JSON.stringify(currentOrder));
        } catch (e) { /* ignore quota errors */ }
        setEditing(false);
      });
    }

    if (cancelBtn) {
      cancelBtn.addEventListener('click', function () {
        setEditing(false);
      });
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
