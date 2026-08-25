(function () {
  'use strict';

  var lookups = {};
  var csrfToken = '';
  var baseUrl = '';
  var registerId = 0;
  var activeFilters = {};
  var textFilters = {};
  var sortState = {};
  var STORAGE_PREFIX = 'raid-ss-';
  var a11yReorderTable = null;
  var serverTableLayouts = {};
  var readOnly = false;
  var canEditLockedInherentRatings = false;
  var riskInfoCache = {};
  var riskInfoDirectorate = '—';

  function readCsrfToken() {
    var tokenEl = document.querySelector('input[name="__RequestVerificationToken"]');
    return tokenEl ? tokenEl.value : '';
  }

  function parseJsonResponse(res) {
    return res.text().then(function (text) {
      var trimmed = (text || '').trim();
      if (!trimmed) {
        if (!res.ok) {
          throw new Error('Request failed (' + res.status + ')');
        }
        return {};
      }
      try {
        return JSON.parse(trimmed);
      } catch (e) {
        if (!res.ok) {
          throw new Error('Request failed (' + res.status + ')');
        }
        throw e;
      }
    });
  }

  function init(config) {
    lookups = config.lookups || {};
    csrfToken = config.csrfToken || readCsrfToken();
    baseUrl = config.baseUrl || '/modern/raid';
    registerId = config.registerId || 0;
    serverTableLayouts = config.tableLayouts || {};
    readOnly = !!config.readOnly;
    canEditLockedInherentRatings = !!config.canEditLockedInherentRatings;
    initRiskInfoModal(config.riskInfo || {});

    bindCellClicks();
    bindInlineAdd();
    bindLinkExistingRisk();
    bindDetailButtons();
    bindModalClose();
    bindKeyboard();
    bindRefLinks();
    if (!readOnly) {
      bindRelationLinks();
      bindRelationEditModal();
    }
    bindFullscreen();
    bindSortHeaders();
    buildFilterRows();
    bindFilterToggleButtons();
    captureDefaultColumnOrders();
    setupColumnResize();
    restoreColumnWidths();
    bindWrapToggle();
    restoreWrapState();
    bindReorderToggle();
    restoreColumnOrder();
    bindCommentButtons();
    bindMitigationButtons();
    bindKriButtons();
    if (readOnly) {
      applyReadOnlySpreadsheetUi();
    }
    bindAccessibleReorder();
    setupHeaderKeyboardResize();
    bindZoomControls();
    bindResetTableModal();
    bindClearFiltersButtons();
    bindTableSearch();
    restoreTableZoom();
    syncAllHeaderScrollMetrics();
    window.addEventListener('resize', syncAllHeaderScrollMetrics);
  }

  // ── Sorting ──

  function bindSortHeaders() {
    document.querySelectorAll('.raid-ss-sortable').forEach(function (th) {
      th.style.cursor = 'pointer';
      th.style.userSelect = 'none';
      th.addEventListener('click', function (e) {
        var table = th.closest('table');
        var col = th.getAttribute('data-col');
        var tableId = table.id;
        var key = tableId + ':' + col;

        var dir = 'asc';
        if (sortState[key] === 'asc') dir = 'desc';
        else if (sortState[key] === 'desc') dir = null;

        table.querySelectorAll('.raid-ss-sortable').forEach(function (h) {
          h.classList.remove('raid-ss-sort-asc', 'raid-ss-sort-desc');
        });

        sortState = {};
        if (dir) {
          sortState[key] = dir;
          th.classList.add(dir === 'asc' ? 'raid-ss-sort-asc' : 'raid-ss-sort-desc');
        }

        sortTable(table, th, dir);
      });
    });
  }

  function sortTable(table, th, dir) {
    var tbody = table.querySelector('tbody');
    var rows = Array.from(tbody.querySelectorAll('.raid-ss-row'));
    if (!dir) {
      rows.sort(function (a, b) {
        return parseInt(a.getAttribute('data-id')) - parseInt(b.getAttribute('data-id'));
      });
    } else {
      var colIdx = Array.from(th.parentNode.children).indexOf(th);
      rows.sort(function (a, b) {
        var aCell = a.children[colIdx];
        var bCell = b.children[colIdx];
        var aVal = getCellSortValue(aCell);
        var bVal = getCellSortValue(bCell);
        var aNum = parseFloat(aVal);
        var bNum = parseFloat(bVal);
        var result;
        if (!isNaN(aNum) && !isNaN(bNum)) {
          result = aNum - bNum;
        } else {
          result = aVal.localeCompare(bVal, undefined, { sensitivity: 'base' });
        }
        return dir === 'desc' ? -result : result;
      });
    }
    rows.forEach(function (row) { tbody.appendChild(row); });
  }

  function getCellSortValue(cell) {
    var sortAttr = cell.getAttribute('data-sort-value');
    if (sortAttr) return sortAttr;
    var val = cell.querySelector('.raid-ss-val');
    var text = val ? val.textContent.trim() : cell.textContent.trim();
    return text === '—' ? '' : text;
  }

  // ── Filtering ──

  var TEXT_FILTER_COLUMNS = ['ref', 'title', 'description', 'cause', 'impact', 'contingency', 'assurance', 'financialImpact', 'kris', 'mitigations', 'lastCommentUpdate'];

  function getCellFilterText(cell) {
    if (!cell) return '';
    var relLink = cell.querySelector('.raid-ss-relation-item-link');
    if (relLink) return relLink.textContent.trim();
    var refLink = cell.querySelector('.raid-ss-ref-link');
    if (refLink) return refLink.textContent.trim();
    var scoreBadge = cell.querySelector('.raid-ss-score-badge');
    if (scoreBadge) return scoreBadge.textContent.trim();
    var val = cell.querySelector('.raid-ss-val');
    if (val) return val.textContent.trim();
    return cell.textContent.trim();
  }

  function columnShouldHaveFilter(th) {
    return !th.classList.contains('raid-ss-no-filter') && !!th.getAttribute('data-col');
  }

  function columnUsesTextFilter(th, colName) {
    if (th.classList.contains('raid-ss-filterable')) return false;
    if (th.classList.contains('raid-ss-filter-text')) return true;
    return TEXT_FILTER_COLUMNS.indexOf(colName) !== -1;
  }

  function getColumnHeaderLabel(th) {
    return (th.textContent || '').trim().replace(/\s+/g, ' ');
  }

  function createMultiFilterDropdown(table, colIdx, colName) {
    var dropdown = document.createElement('div');
    dropdown.className = 'raid-ss-filter-dropdown';

    var toggle = document.createElement('button');
    toggle.type = 'button';
    toggle.className = 'raid-ss-filter-toggle';
    toggle.textContent = 'All';
    toggle.setAttribute('data-col', colName);
    toggle.setAttribute('data-col-idx', colIdx);

    var panel = document.createElement('div');
    panel.className = 'raid-ss-filter-panel';

    var uniqueVals = getUniqueColumnValues(table, colIdx);
    uniqueVals.forEach(function (v) {
      var label = document.createElement('label');
      var cb = document.createElement('input');
      cb.type = 'checkbox';
      cb.value = v;
      cb.checked = false;
      label.appendChild(cb);
      label.appendChild(document.createTextNode(v));
      panel.appendChild(label);
    });

    var actions = document.createElement('div');
    actions.className = 'raid-ss-filter-actions';
    var selectAllBtn = document.createElement('button');
    selectAllBtn.type = 'button';
    selectAllBtn.textContent = 'Select all';
    var clearBtn = document.createElement('button');
    clearBtn.type = 'button';
    clearBtn.textContent = 'Clear';

    selectAllBtn.addEventListener('click', function () {
      panel.querySelectorAll('input[type="checkbox"]').forEach(function (cb) { cb.checked = true; });
      applyMultiFilter(table, colIdx, colName, panel, toggle);
    });
    clearBtn.addEventListener('click', function () {
      panel.querySelectorAll('input[type="checkbox"]').forEach(function (cb) { cb.checked = false; });
      applyMultiFilter(table, colIdx, colName, panel, toggle);
    });

    actions.appendChild(selectAllBtn);
    actions.appendChild(clearBtn);
    panel.appendChild(actions);

    panel.addEventListener('change', function () {
      applyMultiFilter(table, colIdx, colName, panel, toggle);
    });

    toggle.addEventListener('click', function (e) {
      e.stopPropagation();
      closeAllFilterPanels(panel);
      panel.classList.toggle('raid-ss-filter-open');
    });

    dropdown.appendChild(toggle);
    dropdown.appendChild(panel);
    return dropdown;
  }

  function createTextFilterInput(table, colIdx, colName, th) {
    var filterKey = table.id + ':' + colName;
    var label = getColumnHeaderLabel(th);
    var textInput = document.createElement('input');
    textInput.type = 'search';
    textInput.className = 'raid-ss-title-filter';
    textInput.placeholder = 'Filter ' + label + '\u2026';
    textInput.setAttribute('aria-label', 'Filter by ' + label);
    if (textFilters[filterKey]) textInput.value = textFilters[filterKey].query;

    var debounceTimer = null;
    textInput.addEventListener('input', function () {
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(function () {
        var q = textInput.value.trim().toLowerCase();
        if (!q) {
          delete textFilters[filterKey];
        } else {
          textFilters[filterKey] = { tableId: table.id, colIdx: colIdx, query: q };
        }
        applyFilters(table);
        updateClearFilterBtn(table);
      }, 200);
    });

    return textInput;
  }

  function populateFilterCell(td, th, table, headerRow) {
    var colIdx = Array.from(headerRow.children).indexOf(th);
    var colName = th.getAttribute('data-col');
    if (!columnShouldHaveFilter(th)) return;

    if (columnUsesTextFilter(th, colName)) {
      td.appendChild(createTextFilterInput(table, colIdx, colName, th));
    } else {
      td.appendChild(createMultiFilterDropdown(table, colIdx, colName));
    }
  }

  function buildFilterRows() {
    document.querySelectorAll('.raid-ss-filter-row').forEach(function (filterRow) {
      var headerRow = filterRow.previousElementSibling;
      if (!headerRow) return;
      var table = filterRow.closest('table');
      var headers = headerRow.querySelectorAll('th');
      filterRow.innerHTML = '';

      headers.forEach(function (th) {
        var td = document.createElement('td');
        td.className = 'govuk-table__cell raid-ss-filter-cell';
        td.style.padding = '4px 6px';
        populateFilterCell(td, th, table, headerRow);

        if (th.classList.contains('raid-ss-sticky-col')) {
          td.classList.add('raid-ss-sticky-col');
        }
        if (th.classList.contains('raid-ss-group-start')) {
          td.classList.add('raid-ss-group-start');
        }
        if (th.classList.contains('raid-ss-group-end')) {
          td.classList.add('raid-ss-group-end');
        }

        filterRow.appendChild(td);
      });
    });

    document.addEventListener('click', function (e) {
      if (!e.target.closest('.raid-ss-filter-dropdown')) {
        closeAllFilterPanels();
      }
    });
  }

  function closeAllFilterPanels(except) {
    document.querySelectorAll('.raid-ss-filter-panel.raid-ss-filter-open').forEach(function (p) {
      if (p !== except) p.classList.remove('raid-ss-filter-open');
    });
  }

  function applyMultiFilter(table, colIdx, colName, panel, toggle) {
    var checked = [];
    panel.querySelectorAll('input[type="checkbox"]:checked').forEach(function (cb) {
      checked.push(cb.value);
    });

    var filterKey = table.id + ':' + colName;
    if (checked.length === 0) {
      delete activeFilters[filterKey];
      toggle.textContent = 'All';
      toggle.classList.remove('raid-ss-filter-active');
    } else {
      activeFilters[filterKey] = { tableId: table.id, colIdx: colIdx, values: checked };
      toggle.textContent = checked.length === 1 ? checked[0] : checked.length + ' selected';
      toggle.classList.add('raid-ss-filter-active');
    }

    applyFilters(table);
    updateClearFilterBtn(table);
  }

  function getUniqueColumnValues(table, colIdx) {
    var vals = {};
    table.querySelectorAll('tbody .raid-ss-row').forEach(function (row) {
      var cell = row.children[colIdx];
      if (!cell) return;
      var text = getCellFilterText(cell);
      if (text && text !== '—') vals[text] = true;
    });
    return Object.keys(vals).sort(function (a, b) {
      return a.localeCompare(b, undefined, { sensitivity: 'base' });
    });
  }

  function applyFilters(table) {
    var tableId = table.id;
    var filtersForTable = {};
    var textFiltersForTable = {};
    Object.keys(activeFilters).forEach(function (key) {
      if (activeFilters[key].tableId === tableId) {
        filtersForTable[activeFilters[key].colIdx] = activeFilters[key].values;
      }
    });
    Object.keys(textFilters).forEach(function (key) {
      if (textFilters[key].tableId === tableId) {
        textFiltersForTable[textFilters[key].colIdx] = textFilters[key].query;
      }
    });

    var searchQ = getTableSearchQuery(table);

    table.querySelectorAll('tbody .raid-ss-row').forEach(function (row) {
      var show = true;
      Object.keys(filtersForTable).forEach(function (colIdx) {
        var cell = row.children[parseInt(colIdx)];
        if (!cell) return;
        var text = getCellFilterText(cell);
        if (filtersForTable[colIdx].indexOf(text) === -1) show = false;
      });
      Object.keys(textFiltersForTable).forEach(function (colIdx) {
        var cell = row.children[parseInt(colIdx)];
        if (!cell) return;
        var text = getCellFilterText(cell).toLowerCase();
        if (text.indexOf(textFiltersForTable[colIdx]) === -1) show = false;
      });
      if (show && searchQ && !rowMatchesTableSearch(row, table, searchQ)) show = false;
      row.style.display = show ? '' : 'none';
    });
  }

  function getTableSearchQuery(table) {
    var input = document.querySelector('[data-raid-ss-search="' + table.id + '"]');
    return input ? input.value.trim().toLowerCase() : '';
  }

  var TABLE_SEARCH_COLUMNS = ['title', 'owner', 'description'];

  function rowMatchesTableSearch(row, table, searchQ) {
    var headerRow = table.querySelector('.raid-ss-header-row');
    if (!headerRow) return true;
    var headers = Array.from(headerRow.children);
    return TABLE_SEARCH_COLUMNS.some(function (colName) {
      var idx = headers.findIndex(function (th) { return th.getAttribute('data-col') === colName; });
      if (idx < 0) return false;
      var cell = row.children[idx];
      if (!cell) return false;
      return getCellFilterText(cell).toLowerCase().indexOf(searchQ) !== -1;
    });
  }

  function bindTableSearch() {
    document.querySelectorAll('[data-raid-ss-search]').forEach(function (input) {
      var debounceTimer = null;
      input.addEventListener('input', function () {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(function () {
          var table = document.getElementById(input.getAttribute('data-raid-ss-search'));
          if (!table) return;
          applyFilters(table);
          updateClearFilterBtn(table);
        }, 200);
      });
    });
  }

  function updateClearFilterBtn(table) {
    var hasFilters = Object.keys(activeFilters).some(function (k) {
      return activeFilters[k].tableId === table.id;
    }) || Object.keys(textFilters).some(function (k) {
      return textFilters[k].tableId === table.id;
    }) || !!getTableSearchQuery(table);
    var section = table.closest('.raid-ss-tab-panel') || table.closest('[id^="ss-panel-"]');
    if (section) {
      var btn = section.querySelector('.raid-ss-clear-filters-btn');
      if (btn) btn.style.display = hasFilters ? '' : 'none';
    }
  }

  function clearFilters() {
    activeFilters = {};
    textFilters = {};
    document.querySelectorAll('.raid-ss-title-filter').forEach(function (inp) {
      inp.value = '';
    });
    document.querySelectorAll('[data-raid-ss-search]').forEach(function (inp) {
      inp.value = '';
    });
    document.querySelectorAll('.raid-ss-filter-toggle').forEach(function (btn) {
      btn.textContent = 'All';
      btn.classList.remove('raid-ss-filter-active');
    });
    document.querySelectorAll('.raid-ss-filter-panel input[type="checkbox"]').forEach(function (cb) {
      cb.checked = false;
    });
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      table.querySelectorAll('tbody .raid-ss-row').forEach(function (row) {
        row.style.display = '';
      });
    });
    document.querySelectorAll('.raid-ss-clear-filters-btn').forEach(function (btn) {
      btn.style.display = 'none';
    });
    document.querySelectorAll('.raid-ss-filter-row').forEach(function (row) {
      row.style.display = 'none';
    });
    document.querySelectorAll('.raid-ss-toggle-filters-btn').forEach(function (btn) {
      btn.classList.remove('raid-ss-btn-active');
    });
  }

  function bindFilterToggleButtons() {
    document.querySelectorAll('.raid-ss-toggle-filters-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var section = btn.closest('.raid-ss-tab-panel') || btn.closest('[id^="ss-panel-"]') || btn.closest('.raid-ss-tab-active') || document.body;
        var filterRows = section.querySelectorAll('.raid-ss-filter-row');
        if (filterRows.length === 0) filterRows = document.querySelectorAll('.raid-ss-filter-row');
        var willShow = false;
        filterRows.forEach(function (row) {
          var isVisible = row.style.display !== 'none';
          if (!isVisible) willShow = true;
          row.style.display = isVisible ? 'none' : '';
        });
        btn.classList.toggle('raid-ss-btn-active', willShow);
      });
    });
  }

  // ── Inline editing (select dropdowns and user lookup) ──

  function isInherentRatingLocked(field, currentId) {
    if (canEditLockedInherentRatings) return false;
    return !!currentId;
  }

  function lockInherentRatingCellIfNeeded(cell, field, newVal) {
    if (!newVal || canEditLockedInherentRatings) return;
    var f = (field || '').toLowerCase();
    if (f !== 'originalimpactid' && f !== 'originallikelihoodid') return;
    cell.classList.remove('raid-ss-editable');
    cell.setAttribute('data-inherent-locked', 'true');
  }

  function bindCellClicks() {
    if (readOnly) return;
    document.addEventListener('click', function (e) {
      var cell = e.target.closest('.raid-ss-editable');
      if (!cell || cell.classList.contains('raid-ss-editing')) return;

      if (cell.getAttribute('data-inherent-locked') === 'true') return;

      var type = cell.getAttribute('data-type');
      if (type === 'modal') {
        openTextModal(cell);
        return;
      }
      if (type === 'userlookup') {
        closeAllEditors();
        openUserLookup(cell);
        return;
      }

      closeAllEditors();
      openEditor(cell);
    });
  }

  function openEditor(cell) {
    var type = cell.getAttribute('data-type');
    var field = cell.getAttribute('data-field');
    var row = cell.closest('.raid-ss-row');
    var valSpan = cell.querySelector('.raid-ss-val');
    if (!valSpan) return;

    cell.classList.add('raid-ss-editing');
    row.classList.add('raid-ss-row-editing');

    if (type === 'select') {
      var lookupName = cell.getAttribute('data-lookup');
      var currentId = cell.getAttribute('data-current');
      var options = lookups[lookupName] || [];

      var sel = document.createElement('select');
      sel.className = 'raid-ss-inline-select';
      sel.innerHTML = '<option value="">— Select —</option>';
      options.forEach(function (opt) {
        var o = document.createElement('option');
        o.value = opt.id;
        o.textContent = opt.name;
        if (String(opt.id) === String(currentId)) o.selected = true;
        sel.appendChild(o);
      });

      valSpan.style.display = 'none';
      cell.appendChild(sel);
      sel.focus();

      sel.addEventListener('change', function () {
        var newVal = sel.value;
        var newText = sel.options[sel.selectedIndex].text;
        var badgeKind = cell.getAttribute('data-badge-kind');
        saveField(row, field, newVal, function () {
          cell.setAttribute('data-current', newVal);
          if (badgeKind) {
            setLookupBadgeValSpan(valSpan, newVal ? newText : '—', badgeKind);
          } else {
            valSpan.textContent = newVal ? newText : '—';
          }
          lockInherentRatingCellIfNeeded(cell, field, newVal);
          closeEditor(cell);
          updateScoresFromResponse(row);
        }, function () {
          closeEditor(cell);
        });
      });

      sel.addEventListener('blur', function () {
        setTimeout(function () { closeEditor(cell); }, 150);
      });

      sel.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') closeEditor(cell);
      });
    }
  }

  function closeEditor(cell) {
    var row = cell.closest('.raid-ss-row');
    cell.classList.remove('raid-ss-editing');
    if (row) row.classList.remove('raid-ss-row-editing');
    var valSpan = cell.querySelector('.raid-ss-val');
    if (valSpan) valSpan.style.display = '';
    var inp = cell.querySelector('.raid-ss-inline-select, .raid-ss-inline-input, .raid-ss-user-lookup-wrap');
    if (inp) inp.remove();
  }

  function closeAllEditors() {
    document.querySelectorAll('.raid-ss-editing').forEach(closeEditor);
  }

  // ── User lookup (Owner field) ──

  function openUserLookup(cell) {
    var field = cell.getAttribute('data-field');
    var row = cell.closest('.raid-ss-row');
    var valSpan = cell.querySelector('.raid-ss-val');
    if (!valSpan) return;

    row.classList.add('raid-ss-row-editing');

    var modal = document.getElementById('raid-user-modal');
    if (!modal) return;

    var searchInput = document.getElementById('raid-user-modal-search');
    var listEl = document.getElementById('raid-user-modal-list');
    var hintEl = document.getElementById('raid-user-modal-hint');
    var clearBtn = document.getElementById('raid-user-modal-clear');
    var cancelBtn = document.getElementById('raid-user-modal-cancel');
    var closeBtn = modal.querySelector('.raid-modal-close');

    searchInput.value = '';
    listEl.innerHTML = '';
    listEl.style.display = 'none';
    hintEl.style.display = '';
    hintEl.textContent = 'Type at least 2 characters to search.';

    var hasCurrent = cell.getAttribute('data-current') && valSpan.textContent.trim() !== '—';
    clearBtn.style.display = hasCurrent ? '' : 'none';

    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    searchInput.focus();

    var debounceTimer = null;

    function cleanup() {
      row.classList.remove('raid-ss-row-editing');
      modal.style.display = 'none';
      document.body.style.overflow = '';
      searchInput.removeEventListener('input', onInput);
      searchInput.removeEventListener('keydown', onKeydown);
      clearBtn.replaceWith(clearBtn.cloneNode(true));
      cancelBtn.replaceWith(cancelBtn.cloneNode(true));
      closeBtn.replaceWith(closeBtn.cloneNode(true));
      clearTimeout(debounceTimer);
    }

    function selectUser(userId, userName) {
      saveField(row, field, userId, function () {
        cell.setAttribute('data-current', userId);
        valSpan.textContent = userName;
        cleanup();
      }, function () {
        cleanup();
      });
    }

    function onInput() {
      clearTimeout(debounceTimer);
      var q = searchInput.value.trim();
      if (q.length < 2) {
        listEl.style.display = 'none';
        hintEl.style.display = '';
        hintEl.textContent = 'Type at least 2 characters to search.';
        return;
      }
      hintEl.textContent = 'Searching…';
      hintEl.style.display = '';
      debounceTimer = setTimeout(function () {
        fetch('/api/users/search?q=' + encodeURIComponent(q) + '&top=10')
          .then(function (r) { return r.json(); })
          .then(function (users) {
            if (!Array.isArray(users)) users = users.users || users.value || [];
            listEl.innerHTML = '';
            if (users.length === 0) {
              listEl.style.display = 'none';
              hintEl.textContent = 'No users found.';
              hintEl.style.display = '';
              return;
            }
            hintEl.style.display = 'none';
            users.forEach(function (u) {
              var li = document.createElement('li');
              li.style.cssText = 'padding:8px 4px; border-bottom:1px solid #b1b4b6; cursor:pointer;';
              li.setAttribute('data-user-id', u.id);
              var displayName = u.displayName || u.name || u.email;
              li.innerHTML = '<strong>' + escapeHtml(displayName) + '</strong>'
                + (u.jobTitle ? '<br><span style="color:#505a5f; font-size:14px;">' + escapeHtml(u.jobTitle) + '</span>' : '')
                + (u.email ? '<br><span style="color:#505a5f; font-size:14px;">' + escapeHtml(u.email) + '</span>' : '');
              li.addEventListener('click', function () {
                selectUser(u.id, displayName);
              });
              li.addEventListener('mouseenter', function () { li.style.background = '#f3f2f1'; });
              li.addEventListener('mouseleave', function () { li.style.background = ''; });
              listEl.appendChild(li);
            });
            listEl.style.display = '';
          })
          .catch(function () {
            listEl.style.display = 'none';
            hintEl.textContent = 'Search failed. Please try again.';
            hintEl.style.display = '';
          });
      }, 300);
    }

    function onKeydown(e) {
      if (e.key === 'Escape') cleanup();
    }

    searchInput.addEventListener('input', onInput);
    searchInput.addEventListener('keydown', onKeydown);

    clearBtn.addEventListener('click', function () {
      saveField(row, field, null, function () {
        cell.setAttribute('data-current', '');
        valSpan.textContent = '—';
        cleanup();
      }, function () {
        cleanup();
      });
    }, { once: true });

    cancelBtn.addEventListener('click', function () { cleanup(); }, { once: true });
    closeBtn.addEventListener('click', function () { cleanup(); }, { once: true });
  }

  function escapeHtml(str) {
    var div = document.createElement('div');
    div.textContent = str || '';
    return div.innerHTML;
  }

  // ── Text editing modal (Title, Description, Cause, Impact, Mitigations) ──

  function openTextModal(cell) {
    var field = cell.getAttribute('data-field');
    var label = cell.getAttribute('data-label') || field;
    var row = cell.closest('.raid-ss-row');
    var valSpan = cell.querySelector('.raid-ss-val');
    var currentText = valSpan ? valSpan.textContent.trim() : '';
    if (currentText === '—') currentText = '';

    row.classList.add('raid-ss-row-editing');

    var modal = document.getElementById('raid-text-modal');
    if (!modal) return;

    document.getElementById('raid-text-modal-title').textContent = 'Edit ' + label;
    var textarea = document.getElementById('raid-text-modal-input');
    textarea.value = currentText;

    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    textarea.focus();

    var saveBtn = document.getElementById('raid-text-modal-save');
    var cancelBtn = document.getElementById('raid-text-modal-cancel');
    var closeBtn = modal.querySelector('.raid-modal-close');

    function cleanup() {
      row.classList.remove('raid-ss-row-editing');
      saveBtn.replaceWith(saveBtn.cloneNode(true));
      cancelBtn.replaceWith(cancelBtn.cloneNode(true));
      closeBtn.replaceWith(closeBtn.cloneNode(true));
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }

    saveBtn.addEventListener('click', function handler() {
      var newVal = textarea.value.trim();
      saveField(row, field, newVal, function () {
        if (valSpan) valSpan.textContent = newVal || '—';
        cleanup();
      }, function () {
        cleanup();
      });
    }, { once: true });

    cancelBtn.addEventListener('click', function () { cleanup(); }, { once: true });
    closeBtn.addEventListener('click', function () { cleanup(); }, { once: true });
  }

  // ── Keyboard navigation ──

  function bindKeyboard() {
    document.addEventListener('keydown', function (e) {
      if (e.key === 'Escape') {
        closeAllEditors();
        closeModal();
        closeTextModal();
        closeUserModal();
        closeRelationEditModal();
      }
    });
  }

  function closeUserModal() {
    var modal = document.getElementById('raid-user-modal');
    if (modal) {
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }
    document.querySelectorAll('.raid-ss-row-editing').forEach(function (r) {
      r.classList.remove('raid-ss-row-editing');
    });
  }

  // ── Save field via API ──

  var _lastResponse = null;

  function saveField(row, field, value, onSuccess, onError) {
    var entity = row.getAttribute('data-entity');
    var id = row.getAttribute('data-id');
    var url = baseUrl + '/api/' + entity + '/' + id + '/update';

    row.classList.add('raid-ss-saving');

    fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': csrfToken
      },
      body: JSON.stringify({ field: field, value: value })
    })
    .then(function (res) {
      return parseJsonResponse(res).then(function (data) {
        if (!res.ok) throw new Error((data && data.error) || ('Save failed (' + res.status + ')'));
        return data;
      });
    })
    .then(function (data) {
      row.classList.remove('raid-ss-saving');
      row.classList.add('raid-ss-saved');
      setTimeout(function () { row.classList.remove('raid-ss-saved'); }, 600);
      _lastResponse = data;
      if (data.updatedAt) {
        var editedCell = row.querySelector('.raid-ss-last-edited .raid-ss-val');
        if (editedCell) editedCell.textContent = data.updatedAt;
      }
      if (onSuccess) onSuccess(data);
    })
    .catch(function (err) {
      row.classList.remove('raid-ss-saving');
      row.classList.add('raid-ss-error');
      setTimeout(function () { row.classList.remove('raid-ss-error'); }, 600);
      console.error('RAID save error:', err);
      alert(err.message || 'Could not save.');
      if (onError) onError(err);
    });
  }

  function updateScoresFromResponse(row) {
    if (!_lastResponse) return;
    var scoreMap = {
      inherentScore: _lastResponse.inherentScore,
      currentScore: _lastResponse.currentScore,
      residualScore: _lastResponse.residualScore,
      toleranceScore: _lastResponse.toleranceScore
    };
    Object.keys(scoreMap).forEach(function (key) {
      var cell = row.querySelector('[data-score-field="' + key + '"]');
      if (cell && scoreMap[key] !== undefined) {
        renderScoreBadgeCell(cell, scoreMap[key]);
      }
    });
    if (scoreMap.currentScore !== undefined) {
      applyRefScoreIndicator(row, scoreMap.currentScore);
    }
  }

  // ── Register page counts (dashboard stat cards + manage tabs) ──

  function isOpenRaidStatus(status) {
    var s = (status || '').trim().toLowerCase();
    return s !== 'closed';
  }

  function isOpenRaidItem(data) {
    if (data && data.closedDate) return false;
    return isOpenRaidStatus(data && data.status);
  }

  function adjustRegisterCount(key, delta) {
    if (!delta) return;
    document.querySelectorAll('[data-register-count="' + key + '"]').forEach(function (el) {
      var text = (el.textContent || '').trim();
      var n;
      if (text.charAt(0) === '(') {
        n = (parseInt(text.replace(/[()]/g, ''), 10) || 0) + delta;
        el.textContent = '(' + n + ')';
      } else {
        n = (parseInt(text, 10) || 0) + delta;
        el.textContent = String(n);
      }
    });
  }

  function onRegisterItemCreated(entityType, data) {
    if (entityType === 'risk') upsertRiskInfoFromSpreadsheetRow(data);
    var totalKey = entityType === 'risk' ? 'total-risks'
      : entityType === 'issue' ? 'total-issues'
        : entityType === 'nearmiss' ? 'total-nearmisses'
          : entityType === 'assumption' ? 'total-assumptions'
            : null;
    if (!totalKey) return;

    adjustRegisterCount(totalKey, 1);

    if (isOpenRaidItem(data)) {
      var openKey = entityType === 'risk' ? 'open-risks'
        : entityType === 'issue' ? 'open-issues'
          : entityType === 'nearmiss' ? 'open-nearmisses'
            : entityType === 'assumption' ? 'open-assumptions'
              : null;
      if (openKey) adjustRegisterCount(openKey, 1);
    }
  }

  function onRegisterItemLinked(entityType, data) {
    onRegisterItemCreated(entityType, data);
  }

  // ── Link existing risk ──

  var linkRiskSearchTimer = null;

  function closeLinkRiskModal() {
    var modal = document.getElementById('raid-link-risk-modal');
    if (!modal) return;
    modal.style.display = 'none';
    document.body.style.overflow = '';
    var search = document.getElementById('raid-link-risk-search');
    var list = document.getElementById('raid-link-risk-list');
    var hint = document.getElementById('raid-link-risk-hint');
    var errorEl = document.getElementById('raid-link-risk-error');
    if (search) search.value = '';
    if (list) {
      list.innerHTML = '';
      list.style.display = 'none';
    }
    if (hint) {
      hint.textContent = 'Type at least 2 characters to search.';
      hint.style.display = '';
    }
    if (errorEl) {
      errorEl.style.display = 'none';
      errorEl.textContent = '';
    }
  }

  function renderLinkRiskResults(results) {
    var list = document.getElementById('raid-link-risk-list');
    var hint = document.getElementById('raid-link-risk-hint');
    if (!list || !hint) return;

    list.innerHTML = '';
    if (!results || results.length === 0) {
      list.style.display = 'none';
      hint.textContent = 'No matching risks found.';
      hint.style.display = '';
      return;
    }

    hint.style.display = 'none';
    list.style.display = '';
    results.forEach(function (item) {
      var li = document.createElement('li');
      li.className = 'govuk-!-margin-bottom-2';

      var btn = document.createElement('button');
      btn.type = 'button';
      btn.className = 'govuk-link govuk-link--no-visited-state raid-link-risk-result';
      btn.style.cssText = 'text-align:left; border:none; background:none; padding:0; cursor:pointer;';
      btn.setAttribute('data-risk-id', item.id);
      btn.innerHTML = '<strong>' + (item.reference || '') + '</strong> — ' + (item.title || '') +
        (item.status ? ' <span class="govuk-body-s" style="color:#505a5f;">(' + item.status + ')</span>' : '');

      li.appendChild(btn);
      list.appendChild(li);
    });
  }

  function searchRisksToLink(term) {
    var url = baseUrl + '/api/register/' + registerId + '/risks/search?q=' + encodeURIComponent(term) + '&limit=15';
    return fetch(url, { credentials: 'same-origin' })
      .then(function (r) { return parseJsonResponse(r).then(function (d) {
        if (!r.ok) throw new Error(d.error || 'Search failed (' + r.status + ')');
        return d;
      }); });
  }

  function linkRiskToRegister(riskId) {
    var errorEl = document.getElementById('raid-link-risk-error');
    if (errorEl) {
      errorEl.style.display = 'none';
      errorEl.textContent = '';
    }

    var table = document.getElementById('tbl-risks');
    var tbody = table ? table.querySelector('tbody') : null;
    if (!tbody) return;

    if (tbody.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskId + '"]')) {
      if (errorEl) {
        errorEl.textContent = 'This risk is already in the register.';
        errorEl.style.display = '';
      }
      return;
    }

    var url = baseUrl + '/api/register/' + registerId + '/track';
    fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': csrfToken || readCsrfToken()
      },
      body: JSON.stringify({ type: 'risk', entityId: riskId })
    })
    .then(function (r) {
      return parseJsonResponse(r).then(function (d) {
        if (!r.ok) throw new Error(d.error || 'Link failed (' + r.status + ')');
        return d;
      });
    })
    .then(function (data) {
      if (data.alreadyLinked && !data.risk) {
        if (errorEl) {
          errorEl.textContent = 'This risk is already linked to the register.';
          errorEl.style.display = '';
        }
        return;
      }

      var riskData = data.risk;
      if (!riskData) {
        window.location.reload();
        return;
      }

      if (tbody.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskData.id + '"]')) {
        closeLinkRiskModal();
        return;
      }

      var dataRow = buildNewDataRow(table, riskData, 'risk');
      if (dataRow) {
        tbody.insertBefore(dataRow, tbody.firstChild);
        dataRow.classList.add('raid-ss-row-highlight');
        dataRow.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
        setTimeout(function () { dataRow.classList.remove('raid-ss-row-highlight'); }, 3000);
        onRegisterItemLinked('risk', riskData);
        rebuildFilterRow(table);
        closeLinkRiskModal();
      } else {
        window.location.reload();
      }
    })
    .catch(function (err) {
      if (errorEl) {
        errorEl.textContent = err.message || 'Could not link risk.';
        errorEl.style.display = '';
      }
    });
  }

  function openLinkRiskModal() {
    if (readOnly) return;
    var modal = document.getElementById('raid-link-risk-modal');
    var searchInput = document.getElementById('raid-link-risk-search');
    if (!modal || !searchInput) return;
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    searchInput.focus();
  }

  function bindLinkExistingRisk() {
    var modal = document.getElementById('raid-link-risk-modal');
    var searchInput = document.getElementById('raid-link-risk-search');
    if (!modal || !searchInput) return;

    document.addEventListener('click', function (e) {
      var openBtn = e.target.closest('#btn-link-risk-top');
      if (!openBtn) return;
      e.preventDefault();
      openLinkRiskModal();
    });

    modal.querySelectorAll('[data-raid-link-risk-close]').forEach(function (btn) {
      btn.addEventListener('click', closeLinkRiskModal);
    });

    searchInput.addEventListener('input', function () {
      if (readOnly) return;
      clearTimeout(linkRiskSearchTimer);
      var term = searchInput.value.trim();
      var hint = document.getElementById('raid-link-risk-hint');
      var list = document.getElementById('raid-link-risk-list');

      if (term.length < 2) {
        if (list) {
          list.innerHTML = '';
          list.style.display = 'none';
        }
        if (hint) {
          hint.textContent = 'Type at least 2 characters to search.';
          hint.style.display = '';
        }
        return;
      }

      linkRiskSearchTimer = setTimeout(function () {
        if (hint) hint.textContent = 'Searching…';
        searchRisksToLink(term)
          .then(function (data) { renderLinkRiskResults(data.results || []); })
          .catch(function () {
            if (hint) hint.textContent = 'Search failed. Try again.';
          });
      }, 300);
    });

    modal.addEventListener('click', function (e) {
      if (readOnly) return;
      var resultBtn = e.target.closest('.raid-link-risk-result');
      if (!resultBtn) return;
      e.preventDefault();
      var riskId = parseInt(resultBtn.getAttribute('data-risk-id'), 10);
      if (!riskId) return;
      linkRiskToRegister(riskId);
    });
  }

  // ── Inline add new row ──

  function bindInlineAdd() {
    document.addEventListener('click', function (e) {
      if (readOnly) return;

      var btn = e.target.closest('#btn-add-risk-top')
        || e.target.closest('#btn-add-issue-top')
        || e.target.closest('#btn-add-nearmiss-top')
        || e.target.closest('#btn-add-assumption-top')
        || e.target.closest('.raid-ss-add-btn');
      if (!btn) return;

      e.preventDefault();

      var entityType = btn.getAttribute('data-entity-type');
      var table = btn.closest('table') || document.querySelector('table[data-entity-type="' + entityType + '"]');
      var tbody = table ? table.querySelector('tbody') : null;
      if (!tbody) return;

      var existingInput = tbody.querySelector('.raid-ss-new-row');
      if (existingInput) {
        existingInput.querySelector('input')?.focus();
        return;
      }

      var headerCells = table.querySelectorAll('thead .raid-ss-header-row th');
      var colCount = headerCells.length;

      var newRow = document.createElement('tr');
      newRow.className = 'govuk-table__row raid-ss-new-row';

      var refTd = document.createElement('td');
      refTd.className = 'govuk-table__cell raid-ss-sticky-col';
      refTd.textContent = '—';
      newRow.appendChild(refTd);

      var titleTd = document.createElement('td');
      titleTd.className = 'govuk-table__cell';
      titleTd.setAttribute('colspan', colCount - 2);
      titleTd.style.padding = '4px 6px';

      var inputWrap = document.createElement('div');
      inputWrap.style.display = 'flex';
      inputWrap.style.gap = '8px';
      inputWrap.style.alignItems = 'center';

      var titleInput = document.createElement('input');
      titleInput.type = 'text';
      titleInput.className = 'govuk-input govuk-input--width-10';
      if (entityType === 'assumption') {
        titleInput.placeholder = 'Enter assumption description…';
      } else if (entityType === 'nearmiss') {
        titleInput.placeholder = 'Enter impact description…';
      } else {
        titleInput.placeholder = 'Enter ' + entityType + ' title…';
      }
      titleInput.style.cssText = 'font-size:14px; padding:4px 8px; height:32px; width:50%; max-width:400px;';

      var saveBtn = document.createElement('button');
      saveBtn.type = 'button';
      saveBtn.className = 'govuk-button govuk-button--small govuk-!-margin-bottom-0';
      saveBtn.textContent = 'Save';
      saveBtn.style.fontSize = '13px';
      saveBtn.style.padding = '4px 12px';
      saveBtn.style.minHeight = '28px';

      var cancelBtn = document.createElement('button');
      cancelBtn.type = 'button';
      cancelBtn.className = 'govuk-button govuk-button--secondary govuk-button--small govuk-!-margin-bottom-0';
      cancelBtn.textContent = 'Cancel';
      cancelBtn.style.fontSize = '13px';
      cancelBtn.style.padding = '4px 12px';
      cancelBtn.style.minHeight = '28px';

      inputWrap.appendChild(titleInput);
      inputWrap.appendChild(saveBtn);
      inputWrap.appendChild(cancelBtn);
      titleTd.appendChild(inputWrap);
      newRow.appendChild(titleTd);

      var actionTd = document.createElement('td');
      actionTd.className = 'govuk-table__cell';
      newRow.appendChild(actionTd);

      tbody.insertBefore(newRow, tbody.firstChild);
      titleInput.focus();

      function doSave() {
        var title = titleInput.value.trim();
        if (!title) { titleInput.focus(); return; }
        saveBtn.disabled = true;
        saveBtn.textContent = 'Saving…';

        var url = baseUrl + '/api/register/' + registerId + '/' + entityType + '/create';

        fetch(url, {
          method: 'POST',
          credentials: 'same-origin',
          headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': csrfToken || readCsrfToken()
          },
          body: JSON.stringify(entityType === 'assumption' ? { description: title } : { title: title })
        })
        .then(function (r) {
          return parseJsonResponse(r).then(function (d) {
            if (!r.ok) throw new Error(d.error || 'Create failed (' + r.status + ')');
            return d;
          });
        })
        .then(function (data) {
          newRow.remove();
          data.title = data.title || title;
          var dataRow = buildNewDataRow(table, data, entityType);
          if (dataRow) {
            tbody.insertBefore(dataRow, tbody.firstChild);
            dataRow.classList.add('raid-ss-row-highlight');
            dataRow.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
            setTimeout(function () { dataRow.classList.remove('raid-ss-row-highlight'); }, 3000);
            onRegisterItemCreated(entityType, data);
            rebuildFilterRow(table);
          } else {
            window.location.reload();
          }
        })
        .catch(function (err) {
          saveBtn.disabled = false;
          saveBtn.textContent = 'Save';
          console.error('Inline create error:', err);
          alert('Could not create ' + entityType + ': ' + (err.message || 'Unknown error'));
        });
      }

      saveBtn.addEventListener('click', doSave);
      titleInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); doSave(); }
        if (e.key === 'Escape') newRow.remove();
      });
      cancelBtn.addEventListener('click', function () { newRow.remove(); });
    });
  }

  // ── Build a data row from create API response ──

  function formatEntityRef(entityType, id) {
    var prefix = entityType === 'risk' ? 'R'
      : entityType === 'issue' ? 'I'
        : entityType === 'nearmiss' ? 'NM'
          : entityType === 'assumption' ? 'A'
            : entityType.charAt(0).toUpperCase();
    var num = String(id).padStart(4, '0');
    return prefix + '-' + num;
  }

  function commentEntityType(entity) {
    if (entity === 'nearmiss') return 'NearMiss';
    if (entity === 'assumption') return 'Assumption';
    return entity.charAt(0).toUpperCase() + entity.slice(1);
  }

  function appendValSpan(td, text) {
    var span = document.createElement('span');
    span.className = 'raid-ss-val';
    span.textContent = text || '—';
    td.appendChild(span);
    return span;
  }

  function isSpreadsheetTierEditable(tierId) {
    var opts = lookups.riskTiers || [];
    if (!tierId) return true;
    return opts.some(function (opt) { return String(opt.id) === String(tierId); });
  }

  function raidLookupBadgeClass(kind, text) {
    var t = (text || '').trim();
    var s = t.toLowerCase();
    var base = 'dfe-f-badge dfe-f-badge--small';
    if (!t || t === '—') return base + ' dfe-f-badge--grey';

    if (kind === 'riskStatus') {
      if (s.indexOf('escalat') !== -1) return base + ' dfe-f-badge--red';
      if (s.indexOf('closed') !== -1) return base + ' dfe-f-badge--green';
      if (s.indexOf('proposed') !== -1) return base + ' dfe-f-badge--grey';
      return base + ' dfe-f-badge--blue';
    }
    if (kind === 'issueStatus') {
      if (s.indexOf('closed') !== -1) return base + ' dfe-f-badge--green';
      return base + ' dfe-f-badge--blue';
    }
    if (kind === 'priority') {
      if (s.indexOf('critical') !== -1) return 'dfe-f-badge dfe-f-badge--red dfe-f-badge--small';
      if (s.indexOf('high') !== -1) return 'dfe-f-badge dfe-f-badge--orange dfe-f-badge--small';
      if (s.indexOf('medium') !== -1) return 'dfe-f-badge dfe-f-badge--blue dfe-f-badge--small';
      if (s.indexOf('low') !== -1) return base + ' dfe-f-badge--grey';
      return base + ' dfe-f-badge--grey';
    }
    if (kind === 'severity') {
      if (s.indexOf('critical') !== -1) return 'dfe-f-badge dfe-f-badge--red dfe-f-badge--small';
      if (s.indexOf('major') !== -1) return 'dfe-f-badge dfe-f-badge--orange dfe-f-badge--small';
      return 'dfe-f-badge dfe-f-badge--green dfe-f-badge--small';
    }
    if (kind === 'tier') {
      if (s.indexOf('proposed') !== -1) {
        if (s.indexOf('1') !== -1 || s.indexOf('tier one') !== -1 || s === 't1') return base + ' dfe-f-badge--blue';
        if (s.indexOf('2') !== -1 || s.indexOf('tier two') !== -1 || s === 't2') return base + ' dfe-f-badge--orange';
        return base + ' dfe-f-badge--grey';
      }
      if (s.indexOf('3') !== -1 || s.indexOf('tier three') !== -1 || s === 't3') return base + ' dfe-f-badge--red';
      if (s.indexOf('2') !== -1 || s.indexOf('tier two') !== -1 || s === 't2') return base + ' dfe-f-badge--orange';
      if (s.indexOf('1') !== -1 || s.indexOf('tier one') !== -1 || s === 't1') return base + ' dfe-f-badge--blue';
      return base + ' dfe-f-badge--grey';
    }
    if (kind === 'likelihoodImpact') {
      if (/^\d+$/.test(s)) {
        var n = parseInt(s, 10);
        if (n <= 0) return base + ' dfe-f-badge--grey';
        if (n <= 2) return base + ' dfe-f-badge--green';
        if (n === 3) return base + ' dfe-f-badge--orange';
        return base + ' dfe-f-badge--red';
      }
      if (s === 'very low' || s === 'negligible' || s === 'rare' || s === 'minimal'
          || s.indexOf('very low') !== -1 || s.indexOf('very small') !== -1 || s.indexOf('negligible') !== -1 || s.indexOf('rare') !== -1
          || s === 'low' || s === 'small' || s === 'minor' || s === 'unlikely') {
        return base + ' dfe-f-badge--green';
      }
      if (s.indexOf('medium') !== -1 || s.indexOf('moderate') !== -1 || s.indexOf('possible') !== -1) {
        return base + ' dfe-f-badge--orange';
      }
      if (s.indexOf('very high') !== -1 || s.indexOf('certain') !== -1 || s.indexOf('severe') !== -1
          || s.indexOf('catastrophic') !== -1 || s.indexOf('high') !== -1 || s.indexOf('major') !== -1
          || s.indexOf('likely') !== -1 || s.indexOf('critical') !== -1) {
        return base + ' dfe-f-badge--red';
      }
      return base + ' dfe-f-badge--grey';
    }
    return base + ' dfe-f-badge--grey';
  }

  function formatLookupBadgeLabel(text, kind) {
    if (!text || text === '—') return '—';
    if (kind === 'tier') return text;
    return text.toUpperCase();
  }

  function setLookupBadgeValSpan(span, text, kind) {
    var label = formatLookupBadgeLabel(text, kind);
    span.className = 'raid-ss-val ' + raidLookupBadgeClass(kind, label);
    span.textContent = label;
  }

  function appendBadgeValSpan(td, text, kind) {
    var span = document.createElement('span');
    setLookupBadgeValSpan(span, text || '—', kind);
    td.appendChild(span);
    return span;
  }

  function appendSelectCell(td, field, lookupName, currentId, displayText, badgeKind) {
    td.classList.add('raid-ss-editable');
    td.setAttribute('data-type', 'select');
    td.setAttribute('data-field', field);
    td.setAttribute('data-lookup', lookupName);
    if (currentId != null) td.setAttribute('data-current', currentId);
    if (badgeKind) {
      td.classList.add('raid-ss-badge-cell');
      td.setAttribute('data-badge-kind', badgeKind);
      appendBadgeValSpan(td, displayText || '—', badgeKind);
    } else {
      appendValSpan(td, displayText || '—');
    }
  }

  function appendModalCell(td, field, label, text) {
    td.classList.add('raid-ss-editable');
    td.setAttribute('data-type', 'modal');
    td.setAttribute('data-field', field);
    td.setAttribute('data-label', label);
    appendValSpan(td, text || '—');
  }

  function mitigationStatusBadgeClass(status) {
    var n = String(status || '').trim().toLowerCase();
    if (n === 'complete' || n === 'completed') return 'dfe-f-badge dfe-f-badge--small dfe-f-badge--green';
    if (n === 'overdue') return 'dfe-f-badge dfe-f-badge--small dfe-f-badge--red';
    if (n === 'in progress' || n === 'in_progress') return 'dfe-f-badge dfe-f-badge--small dfe-f-badge--blue';
    return 'dfe-f-badge dfe-f-badge--small dfe-f-badge--grey';
  }

  function mitigationCardStatusModifier(status) {
    var n = String(status || '').trim().toLowerCase();
    if (n === 'complete' || n === 'completed') return 'raid-ss-mitigation-card--complete';
    if (n === 'overdue') return 'raid-ss-mitigation-card--overdue';
    if (n === 'in progress' || n === 'in_progress') return 'raid-ss-mitigation-card--in-progress';
    return 'raid-ss-mitigation-card--not-started';
  }

  function parseMitigationNoteLines(notes) {
    if (!notes || !String(notes).trim()) return [];
    var lines = [];
    var auditRe = /^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}) UTC — (.+)$/;
    String(notes).split('\n').forEach(function (raw) {
      var line = raw.trim();
      if (!line) return;
      var match = auditRe.exec(line);
      if (match) {
        lines.push({ when: match[1] + ' UTC', text: match[2].trim() });
      } else {
        lines.push({ when: null, text: line });
      }
    });
    return lines.reverse();
  }

  function createCountBadge(count, label) {
    var n = Math.max(0, count);
    if (n <= 0) return null;
    var badge = document.createElement('span');
    badge.className = 'dfe-f-count-badge';
    badge.setAttribute('aria-label', label || (n + ' comments'));
    badge.textContent = n > 99 ? '99+' : String(n);
    return badge;
  }

  function createCellIconToggle(opts) {
    var n = Math.max(0, opts.count || 0);
    var btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'raid-ss-cell-icon-toggle' + (opts.extraClass ? ' ' + opts.extraClass : '');
    if (n > 0) btn.classList.add('raid-ss-cell-icon-toggle--has-count');
    if (opts.extraCountClass && n > 0) btn.classList.add(opts.extraCountClass);
    btn.setAttribute('aria-label', opts.ariaLabel || '');
    if (opts.ariaExpanded != null) btn.setAttribute('aria-expanded', opts.ariaExpanded);

    var dataAttrs = opts.dataAttrs || {};
    Object.keys(dataAttrs).forEach(function (key) {
      if (dataAttrs[key] != null && dataAttrs[key] !== '') {
        btn.setAttribute(key, String(dataAttrs[key]));
      }
    });

    var icon = document.createElement('span');
    icon.className = 'material-symbols-outlined raid-ss-cell-icon-toggle__icon';
    icon.setAttribute('aria-hidden', 'true');
    icon.textContent = opts.icon || 'info';
    btn.appendChild(icon);

    var badge = createCountBadge(n, opts.countLabel);
    if (badge) btn.appendChild(badge);

    var sr = document.createElement('span');
    sr.className = 'govuk-visually-hidden';
    sr.textContent = opts.srText || opts.ariaLabel || '';
    btn.appendChild(sr);

    return btn;
  }

  function renderMitigationCellContent(td, riskId, count, refLabel) {
    td.className = 'govuk-table__cell raid-ss-mitigation-cell';
    td.setAttribute('data-col', 'mitigations');
    td.innerHTML = '';

    var n = Math.max(0, count || 0);
    var label = n === 1 ? '1 mitigation' : n + ' mitigations';
    var ref = refLabel || 'risk';
    td.appendChild(createCellIconToggle({
      extraClass: 'raid-ss-mitigation-open-btn',
      icon: 'task_alt',
      count: n,
      countLabel: label,
      ariaLabel: n ? 'View ' + n + ' mitigations for ' + ref : 'Add mitigation for ' + ref,
      srText: n ? label + ' for ' + ref : 'Add mitigation for ' + ref,
      dataAttrs: {
        'data-risk-id': riskId,
        'data-mitigation-count': n,
        'data-ref-label': refLabel || ''
      }
    }));
  }

  function appendMitigationCell(td, riskId, count, refLabel) {
    renderMitigationCellContent(td, riskId, count, refLabel);
  }

  function setMitigationCountForRisk(riskId, count) {
    var row = document.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskId + '"]');
    var cell = row ? row.querySelector('td.raid-ss-mitigation-cell') : null;
    var refLabel = '';
    if (row) {
      var refLink = row.querySelector('.raid-ss-ref-link');
      refLabel = refLink ? refLink.textContent.trim() : '';
    }
    if (cell) {
      renderMitigationCellContent(cell, riskId, count, refLabel);
      return;
    }
  }

  function renderKriCellContent(td, riskId, count, refLabel) {
    td.className = 'govuk-table__cell raid-ss-kri-cell';
    td.setAttribute('data-col', 'kris');
    td.innerHTML = '';

    var n = Math.max(0, count || 0);
    var label = n === 1 ? '1 KRI' : n + ' KRIs';
    var ref = refLabel || 'risk';
    td.appendChild(createCellIconToggle({
      extraClass: 'raid-ss-kri-open-btn',
      icon: 'monitoring',
      count: n,
      countLabel: label,
      ariaLabel: n ? 'View ' + n + ' KRIs for ' + ref : 'Add KRI for ' + ref,
      srText: n ? label + ' for ' + ref : 'Add KRI for ' + ref,
      dataAttrs: {
        'data-risk-id': riskId,
        'data-kri-count': n,
        'data-ref-label': refLabel || ''
      }
    }));
  }

  function appendKriCell(td, riskId, count, refLabel) {
    renderKriCellContent(td, riskId, count, refLabel);
  }

  function setKriCountForRisk(riskId, count, summary) {
    var row = document.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskId + '"]');
    var cell = row ? row.querySelector('td.raid-ss-kri-cell') : null;
    var refLabel = '';
    if (row) {
      var refLink = row.querySelector('.raid-ss-ref-link');
      refLabel = refLink ? refLink.textContent.trim() : '';
    }
    if (cell) {
      renderKriCellContent(cell, riskId, count, refLabel);
      if (summary !== undefined) {
        var btn = cell.querySelector('.raid-ss-kri-open-btn');
        if (btn) btn.setAttribute('data-kris-summary', summary || '');
      }
      return;
    }
    document.querySelectorAll('.raid-ss-kri-open-btn[data-risk-id="' + riskId + '"]').forEach(function (btn) {
      btn.setAttribute('data-kri-count', count);
      if (summary !== undefined) btn.setAttribute('data-kris-summary', summary || '');
    });
  }

  function appendUserLookupCell(td, field, label, currentUserId, displayText) {
    td.classList.add('raid-ss-editable');
    td.setAttribute('data-type', 'userlookup');
    td.setAttribute('data-field', field);
    td.setAttribute('data-label', label);
    if (currentUserId != null) td.setAttribute('data-current', currentUserId);
    appendValSpan(td, displayText || '—');
  }

  function appendScoreCell(td, scoreField, score, th) {
    td.className = 'govuk-table__cell govuk-table__cell--numeric raid-ss-score';
    if (th && th.classList.contains('raid-ss-group-end')) td.classList.add('raid-ss-group-end');
    td.setAttribute('data-score-field', scoreField);
    renderScoreBadgeCell(td, score);
  }

  function formatLastCommentMeta(atIso, kindLabel) {
    var parts = [];
    if (kindLabel) parts.push(kindLabel);
    if (atIso) {
      var d = new Date(atIso);
      if (!isNaN(d.getTime())) {
        parts.push(d.toLocaleString('en-GB', {
          day: 'numeric', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit'
        }) + ' UTC');
      }
    }
    return parts.join(' \u00b7 ');
  }

  function renderLastCommentCellContent(cell, entity, id, refLabel, text, atIso, kindLabel, commentCount) {
    cell.className = 'govuk-table__cell raid-ss-last-comment-update';
    cell.setAttribute('data-entity', entity);
    cell.setAttribute('data-id', String(id));
    if (refLabel) cell.setAttribute('data-ref', refLabel);
    if (atIso) cell.setAttribute('data-sort-value', atIso);
    else cell.removeAttribute('data-sort-value');

    cell.innerHTML = '';
    var wrap = document.createElement('div');
    wrap.className = 'raid-ss-last-comment-cell';

    var trimmed = text && String(text).trim();
    var preview = document.createElement('span');
    preview.className = 'raid-ss-last-comment-cell__preview';
    preview.textContent = trimmed || '\u2014';
    var meta = formatLastCommentMeta(atIso, kindLabel);
    if (trimmed) {
      preview.title = meta ? meta + ' \u2014 ' + trimmed : trimmed;
    }
    wrap.appendChild(preview);

    var n = Math.max(0, commentCount || 0);
    var countLabel = n === 1 ? '1 comment' : n + ' comments';
    var ref = refLabel || 'risk';
    wrap.appendChild(createCellIconToggle({
      extraClass: 'raid-ss-last-comment-view-all',
      icon: 'chat_bubble_outline',
      count: n,
      countLabel: countLabel,
      ariaLabel: n ? 'View ' + countLabel + ' for ' + ref : 'Comments for ' + ref,
      srText: n ? countLabel + ' for ' + ref : 'Comments for ' + ref,
      dataAttrs: {
        'data-entity': entity,
        'data-id': id,
        'data-ref': refLabel || ''
      }
    }));

    cell.appendChild(wrap);
  }

  function appendLastCommentUpdateCell(td, text, atIso, kindLabel, commentCount, refLabel, riskId) {
    renderLastCommentCellContent(
      td,
      'risk',
      riskId,
      refLabel || '',
      text,
      atIso,
      kindLabel,
      commentCount || 0
    );
  }

  function setLastCommentUpdateForRisk(riskId, text, atIso, kindLabel) {
    var row = document.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskId + '"]');
    if (!row) return;
    var cell = row.querySelector('td.raid-ss-last-comment-update');
    if (!cell) return;
    var refLink = row.querySelector('.raid-ss-ref-link');
    var refLabel = refLink ? refLink.textContent.trim() : (cell.getAttribute('data-ref') || '');
    var toggle = row.querySelector('.raid-ss-comment-toggle');
    var count = toggle ? parseInt(toggle.getAttribute('data-comment-count'), 10) || 0 : 0;
    renderLastCommentCellContent(cell, 'risk', riskId, refLabel, text, atIso, kindLabel, count);
  }

  function relationDisplayText(rel) {
    if (!rel || rel.relationKind === 'Unknown') return '—';
    var target = rel.relationRelatedTitle || rel.relationTarget;
    if (rel.relationKind === 'Organisation') return 'Organisation';
    if (rel.relationKind === 'Work') {
      return target ? 'Work item · ' + target : 'Work item';
    }
    if (rel.relationKind === 'FIPS') {
      return target ? 'Service · ' + target : 'Service';
    }
    return '—';
  }

  function enrichRelationFromLookups(rel) {
    if (!rel) return rel;

    var title = (rel.relationRelatedTitle || '').trim();
    if (!title && rel.relationKind === 'Work' && rel.projectId && lookups.workItems) {
      var work = lookups.workItems.find(function (w) { return w.id === rel.projectId; });
      if (work && work.name) rel.relationRelatedTitle = work.name;
    }
    if (!title && rel.relationKind === 'Fips' && rel.primaryProductId && lookups.fipsProducts) {
      var product = lookups.fipsProducts.find(function (p) { return p.id === rel.primaryProductId; });
      if (product && product.name) rel.relationRelatedTitle = product.name;
    }

    return rel;
  }

  function relationTargetName(rel) {
    if (!rel || rel.relationKind === 'Organisation' || rel.relationKind === 'Unknown') return null;
    rel = enrichRelationFromLookups(rel);
    return (rel.relationRelatedTitle || rel.relationTarget || '').trim() || null;
  }

  function defaultOrganisationRelation() {
    return {
      relationKind: 'Organisation',
      relationTarget: null,
      associationUiKind: 'organisation',
      projectId: null,
      primaryProductId: null,
      relationSourceLabel: 'Organisation',
      relationRelatedTitle: 'Organisation',
      relationRelatedDescription:
        'This is an organisational risk or issue. It is not linked to a work item or service register entry.',
      relationLinkHref: ''
    };
  }

  function applyRelationDataAttributes(td, rel) {
    td.setAttribute('data-association-kind', rel.associationUiKind || '');
    td.setAttribute('data-project-id', rel.projectId != null ? String(rel.projectId) : '');
    td.setAttribute('data-primary-product-id', rel.primaryProductId != null ? String(rel.primaryProductId) : '');
    td.setAttribute('data-relation-kind', rel.relationKind || 'Unknown');
    td.setAttribute('data-relation-source', rel.relationSourceLabel || '');
    td.setAttribute('data-relation-title', rel.relationRelatedTitle || rel.relationTarget || '');
    td.setAttribute('data-relation-description', rel.relationRelatedDescription || '');
    td.setAttribute('data-relation-href', rel.relationLinkHref || '');
  }

  function renderRelationCell(td, rel) {
    var data = enrichRelationFromLookups(rel || defaultOrganisationRelation());
    td.className = 'govuk-table__cell govuk-body-s raid-ss-relation-cell';
    td.setAttribute('data-type', 'relation');
    applyRelationDataAttributes(td, data);
    td.innerHTML = '';

    var wrap = document.createElement('div');
    wrap.className = 'raid-ss-relation-cell__content';

    var target = relationTargetName(data);
    var href = (data.relationLinkHref || '').trim();
    var canView = href && data.relationKind !== 'Organisation' && data.relationKind !== 'Unknown';

    if (canView && target) {
      var link = document.createElement('button');
      link.type = 'button';
      link.className = 'govuk-link govuk-link--no-visited-state raid-ss-relation-item-link raid-ss-relation-view-btn';
      link.textContent = target;
      link.setAttribute('aria-label', 'View details for ' + target);
      wrap.appendChild(link);
    } else if (data.relationKind === 'Organisation') {
      var orgEl = document.createElement('button');
      orgEl.type = 'button';
      orgEl.className = 'govuk-link govuk-link--no-visited-state raid-ss-relation-org-label raid-ss-relation-view-btn';
      orgEl.textContent = 'Not linked to a work item or service';
      orgEl.setAttribute('aria-label', 'View organisational relation details');
      wrap.appendChild(orgEl);
    } else if (target) {
      var nameEl = document.createElement('button');
      nameEl.type = 'button';
      nameEl.className = 'govuk-link govuk-link--no-visited-state raid-ss-relation-name raid-ss-relation-view-btn';
      nameEl.textContent = target;
      nameEl.setAttribute('aria-label', 'View relation details for ' + target);
      wrap.appendChild(nameEl);
    } else {
      var emptyEl = document.createElement('span');
      emptyEl.className = 'raid-ss-relation-empty';
      emptyEl.textContent = '—';
      wrap.appendChild(emptyEl);
    }

    var actions = document.createElement('div');
    actions.className = 'raid-ss-relation-actions';

    var changeBtn = document.createElement('button');
    changeBtn.type = 'button';
    changeBtn.className = 'govuk-link govuk-body-s raid-ss-relation-change-btn';
    changeBtn.textContent = 'Change';
    changeBtn.setAttribute('aria-label', 'Change related item');
    actions.appendChild(changeBtn);

    wrap.appendChild(actions);
    td.appendChild(wrap);
  }

  function appendRelationCell(td, rel) {
    renderRelationCell(td, rel);
  }

  function createCommentToggle(entityType, id, count) {
    var n = Math.max(0, count || 0);
    var label = n === 1 ? '1 comment' : n + ' comments';
    var btn = createCellIconToggle({
      extraClass: 'raid-ss-comment-toggle',
      extraCountClass: 'raid-ss-comment-toggle--has-comments',
      icon: 'chat_bubble_outline',
      count: n,
      countLabel: label,
      ariaLabel: n ? label : 'Comments',
      srText: n ? label : 'Comments',
      ariaExpanded: 'false',
      dataAttrs: {
        'data-entity': entityType,
        'data-id': id,
        'data-comment-count': n
      }
    });
    return btn;
  }

  function appendRefCell(td, entityType, id, refText, commentCount) {
    td.classList.add('raid-ss-sticky-col');
    var wrap = document.createElement('div');
    wrap.className = 'raid-ss-ref-cell';

    var refLink = document.createElement('a');
    refLink.className = 'govuk-link govuk-link--no-visited-state raid-ss-ref-link';
    refLink.href = '#';
    var detailPath = entityType === 'nearmiss'
      ? 'near-misses'
      : entityType === 'assumption'
        ? 'assumptions'
        : entityType + 's';
    refLink.setAttribute('data-href', baseUrl + '/' + detailPath + '/' + id);
    refLink.textContent = refText;

    wrap.appendChild(refLink);
    wrap.appendChild(createCommentToggle(entityType, id, commentCount));
    td.appendChild(wrap);
  }

  var RISK_SCORE_COLS = {
    origScore: 'inherentScore',
    currScore: 'currentScore',
    residScore: 'residualScore',
    tolScore: 'toleranceScore'
  };

  function buildRiskCell(td, col, th, data) {
    if (col === 'title') {
      appendModalCell(td, 'title', 'Title', data.title);
    } else     if (col === 'status') {
      appendSelectCell(td, 'statusId', 'riskStatuses', data.statusId, data.status || 'Open', 'riskStatus');
    } else if (col === 'tier') {
      if (isSpreadsheetTierEditable(data.tierId)) {
        appendSelectCell(td, 'tierId', 'riskTiers', data.tierId, data.tier || 'Tier 3', 'tier');
      } else {
        appendBadgeValSpan(td, data.tier || '—', 'tier');
      }
    } else if (col === 'relation') {
      appendRelationCell(td, data.relation);
    } else if (col === 'category') {
      appendSelectCell(td, 'categoryId', 'riskCategories', data.categoryId, data.category);
    } else if (col === 'owner') {
      appendUserLookupCell(td, 'ownerUserId', 'Owner', data.ownerUserId, data.owner);
    } else if (col === 'description') {
      appendModalCell(td, 'description', 'Description', data.description);
    } else if (col === 'cause') {
      appendModalCell(td, 'cause', 'Cause', data.cause);
    } else if (col === 'impact') {
      appendModalCell(td, 'impactIfRealised', 'Impact', data.impactIfRealised);
    } else if (col === 'contingency') {
      appendModalCell(td, 'contingency', 'Contingency', data.contingency);
    } else if (col === 'assurance') {
      appendModalCell(td, 'assurance', 'Assurance', data.assurance);
    } else if (col === 'financialImpact') {
      appendModalCell(td, 'financialImpact', 'Financial impact', data.financialImpact);
    } else if (col === 'kris') {
      appendKriCell(td, data.id, data.kriCount || 0, data.reference || formatEntityRef('risk', data.id));
    } else if (col === 'response') {
      appendModalCell(td, 'response', 'Response strategy', data.response);
    } else if (col === 'mitigations') {
      appendMitigationCell(td, data.id, data.mitigationCount || 0, data.reference || formatEntityRef('risk', data.id));
    } else if (col === 'lastCommentUpdate') {
      appendLastCommentUpdateCell(
        td,
        data.lastCommentUpdateText || data.lastCommentUpdate,
        data.lastCommentUpdateAt,
        data.lastCommentUpdateKind,
        data.commentCount,
        data.reference || formatEntityRef('risk', data.id),
        data.id
      );
    } else if (col === 'origImpact') {
      if (th.classList.contains('raid-ss-group-start')) td.classList.add('raid-ss-group-start');
      if (isInherentRatingLocked('originalImpactId', data.originalImpactId)) {
        td.setAttribute('data-inherent-locked', 'true');
        td.classList.add('raid-ss-badge-cell');
        td.setAttribute('data-badge-kind', 'likelihoodImpact');
        appendBadgeValSpan(td, data.originalImpact || '—', 'likelihoodImpact');
      } else {
        appendSelectCell(td, 'originalImpactId', 'riskImpactLevels', data.originalImpactId, data.originalImpact, 'likelihoodImpact');
      }
    } else if (col === 'origLikelihood') {
      if (isInherentRatingLocked('originalLikelihoodId', data.originalLikelihoodId)) {
        td.setAttribute('data-inherent-locked', 'true');
        td.classList.add('raid-ss-badge-cell');
        td.setAttribute('data-badge-kind', 'likelihoodImpact');
        appendBadgeValSpan(td, data.originalLikelihood || '—', 'likelihoodImpact');
      } else {
        appendSelectCell(td, 'originalLikelihoodId', 'riskLikelihoods', data.originalLikelihoodId, data.originalLikelihood, 'likelihoodImpact');
      }
    } else if (col === 'origScore') {
      appendScoreCell(td, RISK_SCORE_COLS.origScore, data.inherentScore, th);
    } else if (col === 'currImpact') {
      if (th.classList.contains('raid-ss-group-start')) td.classList.add('raid-ss-group-start');
      appendSelectCell(td, 'currentImpactId', 'riskImpactLevels', data.currentImpactId, data.currentImpact, 'likelihoodImpact');
    } else if (col === 'currLikelihood') {
      appendSelectCell(td, 'currentLikelihoodId', 'riskLikelihoods', data.currentLikelihoodId, data.currentLikelihood, 'likelihoodImpact');
    } else if (col === 'currScore') {
      appendScoreCell(td, RISK_SCORE_COLS.currScore, data.currentScore, th);
    } else if (col === 'residImpact') {
      if (th.classList.contains('raid-ss-group-start')) td.classList.add('raid-ss-group-start');
      appendSelectCell(td, 'residualImpactId', 'riskImpactLevels', data.residualImpactId, data.residualImpact, 'likelihoodImpact');
    } else if (col === 'residLikelihood') {
      appendSelectCell(td, 'residualLikelihoodId', 'riskLikelihoods', data.residualLikelihoodId, data.residualLikelihood, 'likelihoodImpact');
    } else if (col === 'residScore') {
      appendScoreCell(td, RISK_SCORE_COLS.residScore, data.residualScore, th);
    } else if (col === 'tolImpact') {
      if (th.classList.contains('raid-ss-group-start')) td.classList.add('raid-ss-group-start');
      td.setAttribute('data-score-edge-for', RISK_SCORE_COLS.tolScore);
      appendSelectCell(td, 'toleranceImpactId', 'riskImpactLevels', data.toleranceImpactId, data.toleranceImpact, 'likelihoodImpact');
    } else if (col === 'tolLikelihood') {
      appendSelectCell(td, 'toleranceLikelihoodId', 'riskLikelihoods', data.toleranceLikelihoodId, data.toleranceLikelihood, 'likelihoodImpact');
    } else if (col === 'tolScore') {
      appendScoreCell(td, RISK_SCORE_COLS.tolScore, data.toleranceScore, th);
    } else if (col === 'proximity') {
      appendSelectCell(td, 'proximityId', 'riskProximities', data.proximityId, data.proximity);
    } else if (col === 'createdDate') {
      td.textContent = data.createdDate || '—';
    } else if (col === 'lastEdited') {
      td.classList.add('raid-ss-last-edited');
      if (data.updatedAtIso) td.setAttribute('data-updated', data.updatedAtIso);
      appendValSpan(td, data.updatedAt || '—');
    } else {
      appendValSpan(td, '—');
    }
  }

  function scopeDisplayText(rel) {
    if (!rel || rel.relationKind !== 'Organisation') return '—';
    return rel.relationTarget ? 'Organisation · ' + rel.relationTarget : 'Organisation';
  }

  function buildNearMissCell(td, col, data) {
    if (col === 'impact') {
      appendModalCell(td, 'impact', 'Impact', data.impact);
    } else if (col === 'status') {
      appendSelectCell(td, 'statusId', 'nearMissStatuses', data.statusId, data.status || 'Open');
    } else if (col === 'seriousness') {
      appendSelectCell(td, 'seriousnessId', 'nearMissSeriousnesses', data.seriousnessId, data.seriousness);
    } else if (col === 'type') {
      appendSelectCell(td, 'typeId', 'nearMissTypes', data.typeId, data.type);
    } else if (col === 'scope') {
      appendValSpan(td, scopeDisplayText(data.relation));
    } else if (col === 'dateLogged') {
      td.textContent = data.dateLogged || '—';
    } else if (col === 'lastEdited') {
      td.classList.add('raid-ss-last-edited');
      if (data.updatedAtIso) td.setAttribute('data-updated', data.updatedAtIso);
      appendValSpan(td, data.updatedAt || '—');
    } else {
      appendValSpan(td, '—');
    }
  }

  function buildAssumptionCell(td, col, data) {
    if (col === 'description') {
      appendModalCell(td, 'description', 'Description', data.description);
    } else if (col === 'status') {
      appendSelectCell(td, 'assumptionStatusId', 'assumptionStatuses', data.statusId, data.status || 'Open');
    } else if (col === 'criticality') {
      appendSelectCell(td, 'assumptionCriticalityId', 'assumptionCriticalities', data.criticalityId, data.criticality);
    } else if (col === 'relation') {
      appendRelationCell(td, data.relation);
    } else if (col === 'owner') {
      appendUserLookupCell(td, 'ownerUserId', 'Owner', data.ownerUserId, data.owner);
    } else if (col === 'createdDate') {
      td.textContent = data.createdDate || '—';
    } else if (col === 'lastEdited') {
      td.classList.add('raid-ss-last-edited');
      if (data.updatedAtIso) td.setAttribute('data-updated', data.updatedAtIso);
      appendValSpan(td, data.updatedAt || '—');
    } else {
      appendValSpan(td, '—');
    }
  }

  function buildIssueCell(td, col, data) {
    if (col === 'title') {
      appendModalCell(td, 'title', 'Title', data.title);
    } else     if (col === 'status') {
      appendSelectCell(td, 'statusId', 'issueStatuses', data.statusId, data.status || 'Open', 'issueStatus');
    } else if (col === 'severity') {
      appendSelectCell(td, 'severityId', 'issueSeverities', data.severityId, data.severity, 'severity');
    } else if (col === 'priority') {
      appendSelectCell(td, 'priorityId', 'issuePriorities', data.priorityId, data.priority, 'priority');
    } else if (col === 'relation') {
      appendRelationCell(td, data.relation);
    } else if (col === 'category') {
      appendSelectCell(td, 'categoryId', 'issueCategories', data.categoryId, data.category);
    } else if (col === 'owner') {
      appendUserLookupCell(td, 'ownerUserId', 'Owner', data.ownerUserId, data.owner);
    } else if (col === 'description') {
      appendModalCell(td, 'description', 'Description', data.description);
    } else if (col === 'identified') {
      td.textContent = data.identifiedDate || '—';
    } else if (col === 'targetDate') {
      td.textContent = data.targetDate || '—';
    } else if (col === 'lastEdited') {
      td.classList.add('raid-ss-last-edited');
      if (data.updatedAtIso) td.setAttribute('data-updated', data.updatedAtIso);
      appendValSpan(td, data.updatedAt || '—');
    } else {
      appendValSpan(td, '—');
    }
  }

  function buildNewDataRow(table, data, entityType) {
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    if (!headerRow) return null;
    var headers = headerRow.querySelectorAll('th');
    var tr = document.createElement('tr');
    tr.className = 'govuk-table__row raid-ss-row';
    tr.setAttribute('data-entity', entityType);
    tr.setAttribute('data-id', data.id);

    var refText = data.reference || data.ref || formatEntityRef(entityType, data.id);

    headers.forEach(function (th) {
      var col = th.getAttribute('data-col');
      var td = document.createElement('td');
      td.className = 'govuk-table__cell';

      if (th.classList.contains('raid-ss-sticky-col')) {
        appendRefCell(td, entityType, data.id, refText, data.commentCount || 0);
      } else if (entityType === 'risk') {
        buildRiskCell(td, col, th, data);
      } else if (entityType === 'issue') {
        buildIssueCell(td, col, data);
      } else if (entityType === 'nearmiss') {
        buildNearMissCell(td, col, data);
      } else if (entityType === 'assumption') {
        buildAssumptionCell(td, col, data);
      } else {
        appendValSpan(td, '—');
      }
      tr.appendChild(td);
    });

    if (entityType === 'risk') {
      applyRefScoreIndicator(tr, data.currentScore);
      ['inherentScore', 'currentScore', 'residualScore', 'toleranceScore'].forEach(function (field) {
        var scoreCell = tr.querySelector('[data-score-field="' + field + '"]');
        if (scoreCell) renderScoreBadgeCell(scoreCell, data[field]);
      });
    }

    if (wrapEnabled) {
      applyWrapState();
    }

    return tr;
  }

  // ── Relation editor and view modal ──

  var relationSelectsPopulated = false;
  var relationEditContext = null;

  function ensureRelationSelectsPopulated() {
    if (relationSelectsPopulated) return;
    var projSel = document.getElementById('raid-relation-edit-project');
    var fipsSel = document.getElementById('raid-relation-edit-fips');
    if (!projSel || !fipsSel) return;
    (lookups.workItems || []).forEach(function (opt) {
      var o = document.createElement('option');
      o.value = opt.id;
      o.textContent = opt.name;
      projSel.appendChild(o);
    });
    (lookups.fipsProducts || []).forEach(function (opt) {
      var o = document.createElement('option');
      o.value = opt.id;
      o.textContent = opt.name;
      fipsSel.appendChild(o);
    });
    relationSelectsPopulated = true;
  }

  function updateRelationEditPanels() {
    var kind = document.querySelector('input[name="raid-relation-edit-kind"]:checked');
    var k = kind ? kind.value : '';
    var workWrap = document.getElementById('raid-relation-edit-work-wrap');
    var productWrap = document.getElementById('raid-relation-edit-product-wrap');
    if (workWrap) workWrap.style.display = k === 'work' ? '' : 'none';
    if (productWrap) productWrap.style.display = k === 'product' ? '' : 'none';
  }

  function closeRelationEditModal() {
    var modal = document.getElementById('raid-relation-edit-modal');
    if (!modal) return;
    modal.style.display = 'none';
    document.body.style.overflow = '';
    relationEditContext = null;
    var row = document.querySelector('.raid-ss-row-editing');
    if (row) row.classList.remove('raid-ss-row-editing');
  }

  function openRelationEditor(cell) {
    var row = cell.closest('.raid-ss-row');
    if (!row) return;
    var modal = document.getElementById('raid-relation-edit-modal');
    if (!modal) return;

    ensureRelationSelectsPopulated();

    var entity = row.getAttribute('data-entity');
    var id = row.getAttribute('data-id');
    var assocKind = cell.getAttribute('data-association-kind') || 'organisation';
    var projectId = cell.getAttribute('data-project-id') || '';
    var productId = cell.getAttribute('data-primary-product-id') || '';

    relationEditContext = { cell: cell, row: row, entity: entity, id: id };

    document.querySelectorAll('input[name="raid-relation-edit-kind"]').forEach(function (inp) {
      inp.checked = inp.value === assocKind;
    });
    var projSel = document.getElementById('raid-relation-edit-project');
    var fipsSel = document.getElementById('raid-relation-edit-fips');
    if (projSel) projSel.value = projectId;
    if (fipsSel) fipsSel.value = productId;
    updateRelationEditPanels();

    var errEl = document.getElementById('raid-relation-edit-error');
    if (errEl) errEl.style.display = 'none';

    row.classList.add('raid-ss-row-editing');
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
  }

  function bindRelationEditModal() {
    document.querySelectorAll('input[name="raid-relation-edit-kind"]').forEach(function (inp) {
      inp.addEventListener('change', updateRelationEditPanels);
    });

    var saveBtn = document.getElementById('raid-relation-edit-save');
    if (saveBtn) {
      saveBtn.addEventListener('click', function () {
        if (!relationEditContext) return;
        var kindInp = document.querySelector('input[name="raid-relation-edit-kind"]:checked');
        var associationKind = kindInp ? kindInp.value : '';
        var projectId = document.getElementById('raid-relation-edit-project')?.value || '';
        var primaryProductId = document.getElementById('raid-relation-edit-fips')?.value || '';
        var ctx = relationEditContext;
        var url = baseUrl + '/api/' + ctx.entity + '/' + ctx.id + '/association';

        saveBtn.disabled = true;
        saveBtn.textContent = 'Saving…';

        fetch(url, {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': csrfToken
          },
          body: JSON.stringify({
            associationKind: associationKind,
            projectId: projectId ? parseInt(projectId, 10) : null,
            primaryProductId: primaryProductId ? parseInt(primaryProductId, 10) : null
          })
        })
        .then(function (res) {
          if (!res.ok) {
            return res.json().then(function (d) { throw new Error(d.error || 'Save failed'); });
          }
          return res.json();
        })
        .then(function (rel) {
          renderRelationCell(ctx.cell, enrichRelationFromLookups(rel));
          ctx.row.classList.remove('raid-ss-row-editing');
          ctx.row.classList.add('raid-ss-saved');
          setTimeout(function () { ctx.row.classList.remove('raid-ss-saved'); }, 600);
          closeRelationEditModal();
        })
        .catch(function (err) {
          var errEl = document.getElementById('raid-relation-edit-error');
          if (errEl) {
            errEl.textContent = err.message || 'Could not save relation.';
            errEl.style.display = '';
          }
        })
        .finally(function () {
          saveBtn.disabled = false;
          saveBtn.textContent = 'Save';
        });
      });
    }

    document.querySelectorAll('[data-raid-relation-edit-close]').forEach(function (btn) {
      btn.addEventListener('click', closeRelationEditModal);
    });
  }

  function relationInfoFromCell(cell) {
    return {
      kind: cell.getAttribute('data-relation-kind') || '',
      source: cell.getAttribute('data-relation-source') || '',
      title: cell.getAttribute('data-relation-title') || '',
      description: cell.getAttribute('data-relation-description') || '',
      href: cell.getAttribute('data-relation-href') || ''
    };
  }

  function bindRelationLinks() {
    document.addEventListener('click', function (e) {
      var changeBtn = e.target.closest('.raid-ss-relation-change-btn');
      if (changeBtn) {
        e.preventDefault();
        e.stopPropagation();

        var editCell = changeBtn.closest('.raid-ss-relation-cell');
        if (!editCell) return;

        closeAllEditors();
        openRelationEditor(editCell);
        return;
      }

      var viewBtn = e.target.closest('.raid-ss-relation-view-btn');
      if (!viewBtn) return;

      var cell = viewBtn.closest('.raid-ss-relation-cell');
      if (!cell) return;

      e.preventDefault();
      var row = cell.closest('tr.raid-ss-row');
      var entityDetailHref = null;
      if (row) {
        var refLink = row.querySelector('.raid-ss-ref-link');
        if (refLink) entityDetailHref = refLink.getAttribute('data-href');
      }
      openRelationModal(relationInfoFromCell(cell), entityDetailHref);
    });
  }

  function openRelationModal(info, entityDetailHref) {
    var modal = document.getElementById('raid-relation-modal');
    if (!modal) return;

    var isOrganisation = info.kind === 'Organisation';
    var href = (info.href || '').trim();
    if (isOrganisation) {
      href = (entityDetailHref || '').trim();
    }
    var canOpen = href.length > 0;

    var titleEl = document.getElementById('raid-relation-modal-title');
    var sourceEl = document.getElementById('raid-relation-modal-source');
    var itemTitleEl = document.getElementById('raid-relation-modal-item-title');
    var descriptionEl = document.getElementById('raid-relation-modal-description');
    var titleRow = document.getElementById('raid-relation-modal-title-row');
    var descriptionRow = document.getElementById('raid-relation-modal-description-row');
    var promptEl = document.getElementById('raid-relation-modal-prompt');
    var yesBtn = document.getElementById('raid-relation-modal-yes');
    var closeBtn = document.getElementById('raid-relation-modal-close');
    var headerCloseBtn = modal.querySelector('.raid-modal-close');

    if (titleEl) {
      titleEl.textContent = isOrganisation ? 'Organisational item' : 'Related item';
    }
    if (sourceEl) {
      sourceEl.textContent = info.source || (isOrganisation ? 'Organisation' : '—');
    }

    if (isOrganisation) {
      if (titleRow) titleRow.style.display = 'none';
      if (descriptionRow) descriptionRow.style.display = '';
      if (itemTitleEl) itemTitleEl.textContent = '';
      if (descriptionEl) {
        descriptionEl.textContent = info.description ||
          'This is an organisational risk or issue. It is not linked to a work item or service register entry.';
      }
    } else {
      if (titleRow) titleRow.style.display = '';
      if (descriptionRow) descriptionRow.style.display = '';
      if (itemTitleEl) {
        itemTitleEl.textContent = info.title || '—';
      }
      if (descriptionEl) {
        var desc = (info.description || '').trim();
        descriptionEl.textContent = desc || 'No description available.';
        if (!desc) {
          descriptionEl.classList.add('dfe-c-text-muted');
        } else {
          descriptionEl.classList.remove('dfe-c-text-muted');
        }
      }
    }

    if (promptEl) {
      promptEl.hidden = !canOpen;
      promptEl.textContent = canOpen
        ? (isOrganisation
          ? 'Open this item in a new tab?'
          : 'Open this related item in a new tab?')
        : '';
    }
    if (yesBtn) {
      yesBtn.hidden = !canOpen;
    }

    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';

    function cleanup() {
      yesBtn.replaceWith(yesBtn.cloneNode(true));
      closeBtn.replaceWith(closeBtn.cloneNode(true));
      headerCloseBtn.replaceWith(headerCloseBtn.cloneNode(true));
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }

    var freshYes = document.getElementById('raid-relation-modal-yes');
    var freshClose = document.getElementById('raid-relation-modal-close');
    var freshHeaderClose = modal.querySelector('.raid-modal-close');

    if (canOpen && freshYes) {
      freshYes.addEventListener('click', function () {
        window.open(href, '_blank');
        cleanup();
      }, { once: true });
    }

    if (freshClose) {
      freshClose.addEventListener('click', function () { cleanup(); }, { once: true });
    }
    if (freshHeaderClose) {
      freshHeaderClose.addEventListener('click', function () { cleanup(); }, { once: true });
    }
  }

  // ── Reference link modal ──

  function initRiskInfoModal(riskInfo) {
    riskInfoCache = riskInfo.risksById || {};
    riskInfoDirectorate = riskInfo.directorate || '—';
    if (window.RaidRiskInfoModal) {
      window.RaidRiskInfoModal.init({
        likelihoodScale: riskInfo.likelihoodScale || [],
        impactScale: riskInfo.impactScale || [],
        canActionTierChanges: false
      });
    }
  }

  function pairText(likelihood, impact) {
    var lik = likelihood && likelihood !== '—' ? likelihood : '—';
    var imp = impact && impact !== '—' ? impact : '—';
    if (lik === '—' && imp === '—') return '—';
    return lik + ' × ' + imp;
  }

  function ratingBandFromLabels(score, likelihood, impact) {
    return {
      score: score == null ? null : score,
      likelihoodLabel: likelihood && likelihood !== '—' ? likelihood : '—',
      impactLabel: impact && impact !== '—' ? impact : '—',
      likelihoodIndex: 0,
      impactIndex: 0
    };
  }

  function relationLabelFromApi(data) {
    var rel = data && data.relation ? data.relation : {};
    if (rel.relationKind === 'Organisation') return 'Organisation';
    return rel.relationTarget || '—';
  }

  function upsertRiskInfoFromSpreadsheetRow(data) {
    if (!data || data.id == null) return;
    var inherent = ratingBandFromLabels(data.inherentScore, data.originalLikelihood, data.originalImpact);
    var current = ratingBandFromLabels(data.currentScore, data.currentLikelihood, data.currentImpact);
    var residual = ratingBandFromLabels(data.residualScore, data.residualLikelihood, data.residualImpact);
    var payload = {
      id: data.id,
      reference: data.reference || '',
      title: data.title || '',
      detailUrl: '/modern/raid/risks/' + data.id,
      isClosed: !!(data.closedDate),
      inherentScore: data.inherentScore,
      currentScore: data.currentScore,
      residualScore: data.residualScore,
      inherent: inherent,
      current: current,
      residual: residual,
      inherentLikelihoodImpact: pairText(data.originalLikelihood, data.originalImpact),
      currentLikelihoodImpact: pairText(data.currentLikelihood, data.currentImpact),
      residualLikelihoodImpact: pairText(data.residualLikelihood, data.residualImpact),
      mitigation: data.responseStrategy || data.response || '',
      mitigationFull: data.responseStrategy || data.response || '',
      description: data.description || '',
      status: data.status || '—',
      tierName: data.tier || 'Not set',
      owner: data.owner || '—',
      lastReviewedAt: data.lastReviewDate || null,
      lastUpdatedAt: data.updatedAtIso || data.updatedAt || null,
      createdAt: data.createdAt || null,
      daysSinceLastUpdate: 0,
      workItemOrProject: relationLabelFromApi(data),
      workItemUrl: (data.relation && data.relation.relationLinkHref) || null,
      directorate: riskInfoDirectorate
    };
    riskInfoCache[data.id] = payload;
    riskInfoCache[String(data.id)] = payload;
  }

  function cellByCol(row, col) {
    var table = row.closest('table');
    if (!table) return null;
    var headers = table.querySelectorAll('thead .raid-ss-header-row th');
    var idx = -1;
    for (var i = 0; i < headers.length; i++) {
      if (headers[i].getAttribute('data-col') === col) {
        idx = i;
        break;
      }
    }
    if (idx < 0) return null;
    return row.children[idx] || null;
  }

  function parseScoreCell(text) {
    if (!text || text === '—') return null;
    var m = String(text).match(/(\d+(?:\.\d+)?)/);
    if (!m) return null;
    var n = Number(m[1]);
    return Number.isFinite(n) ? n : null;
  }

  function overlayRiskFromRow(base, row, href) {
    var risk = Object.assign({}, base);
    if (base.inherent) risk.inherent = Object.assign({}, base.inherent);
    if (base.current) risk.current = Object.assign({}, base.current);
    if (base.residual) risk.residual = Object.assign({}, base.residual);

    function txt(col) {
      return getCellFilterText(cellByCol(row, col));
    }

    function applyText(key, col, fallbackDash) {
      var value = txt(col);
      if (value && value !== '—') risk[key] = value;
      else if (!risk[key] && fallbackDash) risk[key] = '—';
    }

    applyText('title', 'title');
    applyText('status', 'status');
    applyText('tierName', 'tier');
    applyText('owner', 'owner');
    applyText('description', 'description');
    applyText('workItemOrProject', 'relation');

    var response = txt('response');
    if (response && response !== '—') {
      risk.mitigation = response;
      risk.mitigationFull = response;
    }

    function overlayBand(prefix, bandKey, pairKey, scoreKey) {
      var lik = txt(prefix + 'Likelihood');
      var imp = txt(prefix + 'Impact');
      var score = parseScoreCell(txt(prefix + 'Score'));
      if ((!lik || lik === '—') && (!imp || imp === '—') && score == null) return;
      var band = Object.assign({}, risk[bandKey] || ratingBandFromLabels(null, null, null));
      if (lik && lik !== '—') band.likelihoodLabel = lik;
      if (imp && imp !== '—') band.impactLabel = imp;
      if (score != null) {
        band.score = score;
        risk[scoreKey] = score;
      }
      band.likelihoodIndex = 0;
      band.impactIndex = 0;
      risk[bandKey] = band;
      if ((lik && lik !== '—') || (imp && imp !== '—')) {
        risk[pairKey] = pairText(band.likelihoodLabel, band.impactLabel);
      }
    }

    overlayBand('orig', 'inherent', 'inherentLikelihoodImpact', 'inherentScore');
    overlayBand('curr', 'current', 'currentLikelihoodImpact', 'currentScore');
    overlayBand('resid', 'residual', 'residualLikelihoodImpact', 'residualScore');

    if (href) risk.detailUrl = href;
    if (!risk.directorate) risk.directorate = riskInfoDirectorate;
    if (!risk.reference) {
      var refLink = row.querySelector('.raid-ss-ref-link');
      risk.reference = refLink ? refLink.textContent.trim() : '';
    }
    if (!risk.tierName) risk.tierName = 'Not set';
    return risk;
  }

  function openRiskInfoFromRef(link, row) {
    var id = row.getAttribute('data-id');
    var href = link.getAttribute('data-href') || ('/modern/raid/risks/' + id);
    var cached = riskInfoCache[id] || riskInfoCache[String(id)] || {
      id: id,
      reference: link.textContent.trim(),
      title: '',
      detailUrl: href,
      directorate: riskInfoDirectorate,
      workItemOrProject: '—',
      status: '—',
      tierName: 'Not set',
      owner: '—'
    };
    var risk = overlayRiskFromRow(cached, row, href);
    riskInfoCache[id] = risk;
    riskInfoCache[String(id)] = risk;
    window.RaidRiskInfoModal.open(risk);
  }

  function showRefNewTabModal(href, ref) {
    var modal = document.getElementById('raid-ref-modal');
    if (!modal) return;

    document.getElementById('raid-ref-modal-text').textContent =
      'Open ' + ref + ' in a new tab to view the full details?';
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';

    var yesBtn = document.getElementById('raid-ref-modal-yes');
    var noBtn = document.getElementById('raid-ref-modal-no');
    var closeBtn = modal.querySelector('.raid-modal-close');

    function cleanup() {
      yesBtn.replaceWith(yesBtn.cloneNode(true));
      noBtn.replaceWith(noBtn.cloneNode(true));
      closeBtn.replaceWith(closeBtn.cloneNode(true));
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }

    yesBtn.addEventListener('click', function () {
      window.open(href, '_blank');
      cleanup();
    }, { once: true });

    noBtn.addEventListener('click', function () { cleanup(); }, { once: true });
    closeBtn.addEventListener('click', function () { cleanup(); }, { once: true });
  }

  function bindRefLinks() {
    document.addEventListener('click', function (e) {
      var link = e.target.closest('.raid-ss-ref-link');
      if (!link) return;
      e.preventDefault();

      var row = link.closest('tr.raid-ss-row');
      var entity = row ? row.getAttribute('data-entity') : '';
      if (entity === 'risk' && window.RaidRiskInfoModal) {
        openRiskInfoFromRef(link, row);
        return;
      }

      showRefNewTabModal(link.getAttribute('data-href'), link.textContent.trim());
    });
  }

  // ── Detail modal ──

  function bindDetailButtons() {
    document.addEventListener('click', function (e) {
      var btn = e.target.closest('.raid-ss-detail-btn');
      if (!btn) return;

      var entity = btn.getAttribute('data-entity');
      var id = btn.getAttribute('data-id');
      openDetailModal(entity, id);
    });
  }

  function openDetailModal(entity, id) {
    var modal = document.getElementById('raid-detail-modal');
    var body = document.getElementById('raid-modal-body');
    var title = document.getElementById('raid-modal-title');
    var fullLink = document.getElementById('raid-modal-full-link');

    if (!modal) return;

    var row = document.querySelector('.raid-ss-row[data-entity="' + entity + '"][data-id="' + id + '"]');
    var cells = row ? row.querySelectorAll('.govuk-table__cell') : [];
    var html = '<dl class="govuk-summary-list govuk-summary-list--no-border">';
    var headers = row ? row.closest('table').querySelectorAll('thead .raid-ss-header-row th') : [];

    for (var i = 0; i < cells.length && i < headers.length; i++) {
      var headerText = headers[i].textContent.trim();
      if (!headerText) continue;
      var cellVal = cells[i].querySelector('.raid-ss-val');
      var text = cellVal ? cellVal.textContent.trim() : cells[i].textContent.trim();
      html += '<div class="govuk-summary-list__row">';
      html += '<dt class="govuk-summary-list__key">' + escapeHtml(headerText) + '</dt>';
      html += '<dd class="govuk-summary-list__value">' + escapeHtml(text) + '</dd>';
      html += '</div>';
    }
    html += '</dl>';

    title.textContent = (entity.charAt(0).toUpperCase() + entity.slice(1)) + ' #' + id;
    body.innerHTML = html;
    fullLink.href = baseUrl + '/' + entity + 's/' + id;
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
  }

  function closeModal() {
    var modal = document.getElementById('raid-detail-modal');
    if (modal) {
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }
  }

  function closeTextModal() {
    var modal = document.getElementById('raid-text-modal');
    if (modal) {
      modal.style.display = 'none';
      document.body.style.overflow = '';
    }
    document.querySelectorAll('.raid-ss-row-editing').forEach(function (r) {
      r.classList.remove('raid-ss-row-editing');
    });
  }

  function bindModalClose() {
    document.addEventListener('click', function (e) {
      if (e.target.classList.contains('raid-modal-close') ||
          e.target.classList.contains('raid-modal-overlay')) {
        closeModal();
        closeTextModal();
      }
    });
  }

  function escapeHtml(str) {
    var div = document.createElement('div');
    div.textContent = str;
    return div.innerHTML;
  }

  // ── Fullscreen toggle ──

  function bindFullscreen() {
    document.addEventListener('click', function (e) {
      var btn = e.target.closest('.raid-ss-fullscreen-btn');
      if (!btn) return;
      toggleFullscreen();
    });
  }

  function toggleFullscreen() {
    var container = document.getElementById('raid-manage-section');
    if (!container) return;

    var isFS = container.classList.toggle('raid-register-fullscreen');
    container.classList.toggle('govuk-!-margin-top-2', !isFS);
    document.querySelectorAll('.btn-fs-expand').forEach(function (el) { el.style.display = isFS ? 'none' : ''; });
    document.querySelectorAll('.btn-fs-collapse').forEach(function (el) { el.style.display = isFS ? '' : 'none'; });
    document.querySelectorAll('.raid-ss-fullscreen-btn').forEach(function (btn) {
      btn.classList.toggle('raid-ss-btn-active', isFS);
    });
    syncAllHeaderScrollMetrics();
  }

  // ── Column resizing ──

  function wrapHeaderLabel(th) {
    if (th.querySelector('.raid-ss-th-label')) return;
    var label = document.createElement('span');
    label.className = 'raid-ss-th-label';
    var handle = th.querySelector('.raid-ss-resize-handle');
    var node = th.firstChild;
    while (node) {
      var next = node.nextSibling;
      if (node !== handle && !(node.classList && node.classList.contains('raid-ss-resize-handle'))) {
        label.appendChild(node);
      }
      node = next;
    }
    if (handle) {
      th.insertBefore(label, handle);
    } else {
      th.appendChild(label);
    }
    if (!label.textContent.trim() && th.getAttribute('data-col')) {
      label.textContent = th.getAttribute('data-col');
    }
  }

  function riskScoreBandClass(score) {
    if (score == null || score === '' || isNaN(score)) return '';
    var s = parseFloat(score);
    if (s >= 20) return 'raid-ss-score-badge--highest';
    if (s >= 15) return 'raid-ss-score-badge--elevated';
    if (s >= 8) return 'raid-ss-score-badge--medium';
    return 'raid-ss-score-badge--lower';
  }

  function riskScoreEdgeClass(score) {
    if (score == null || score === '' || isNaN(score)) return '';
    var s = parseFloat(score);
    if (s >= 20) return 'raid-ss-score-edge--highest';
    if (s >= 15) return 'raid-ss-score-edge--elevated';
    if (s >= 8) return 'raid-ss-score-edge--medium';
    return 'raid-ss-score-edge--lower';
  }

  var SCORE_EDGE_CLASSES = [
    'raid-ss-score-edge--highest',
    'raid-ss-score-edge--elevated',
    'raid-ss-score-edge--medium',
    'raid-ss-score-edge--lower'
  ];

  function applyScoreEdgeClass(el, score) {
    if (!el) return;
    SCORE_EDGE_CLASSES.forEach(function (cls) { el.classList.remove(cls); });
    var cls = riskScoreEdgeClass(score);
    if (cls) el.classList.add(cls);
  }

  function renderScoreBadgeCell(cell, score) {
    if (!cell) return;
    cell.innerHTML = '';
    applyScoreEdgeClass(cell, score);
    var field = cell.getAttribute('data-score-field');
    var row = cell.closest('tr');
    if (field && row) {
      row.querySelectorAll('[data-score-edge-for="' + field + '"]').forEach(function (el) {
        applyScoreEdgeClass(el, score);
      });
    }
    if (score == null || score === '' || isNaN(parseFloat(score))) {
      var dash = document.createElement('span');
      dash.className = 'dfe-c-text-muted';
      dash.textContent = '—';
      cell.appendChild(dash);
      return;
    }
    var val = Math.round(parseFloat(score));
    var badge = document.createElement('span');
    badge.className = 'raid-ss-score-badge ' + riskScoreBandClass(score);
    badge.textContent = val + '/25';
    cell.appendChild(badge);
  }

  function applyRefScoreIndicator(row, currentScore) {
    var refCell = row.querySelector('.raid-ss-sticky-col');
    applyScoreEdgeClass(refCell, currentScore);
  }

  function setupColumnResize() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      var headers = table.querySelectorAll('thead .raid-ss-header-row th');
      headers.forEach(function (th) {
        wrapHeaderLabel(th);
        applyStickyHeaderCellStyles(th);
        if (!th.hasAttribute('tabindex')) th.setAttribute('tabindex', '0');
        var labelEl = th.querySelector('.raid-ss-th-label');
        var headerLabel = (labelEl ? labelEl.textContent : th.textContent).trim() || th.getAttribute('data-col') || 'column';
        var handle = document.createElement('div');
        handle.className = 'raid-ss-resize-handle';
        handle.setAttribute('role', 'separator');
        handle.setAttribute('aria-orientation', 'vertical');
        handle.setAttribute('aria-label', 'Resize ' + headerLabel + '. Drag or focus header and use Alt+Left/Right arrow.');
        var resizeIcon = document.createElement('span');
        resizeIcon.className = 'material-symbols-outlined';
        resizeIcon.setAttribute('aria-hidden', 'true');
        resizeIcon.textContent = 'drag_indicator';
        handle.appendChild(resizeIcon);
        th.appendChild(handle);

        var startX, startW;
        handle.addEventListener('mousedown', function (e) {
          e.preventDefault();
          e.stopPropagation();
          startX = e.pageX;
          startW = th.offsetWidth;
          handle.classList.add('raid-ss-resizing');

          function onMove(ev) {
            var col = th.getAttribute('data-col');
            var floor = isWideTextColumn(col) ? DEFAULT_WIDE_COL_MIN : COL_RESIZE_MIN;
            var newW = Math.max(floor, startW + ev.pageX - startX);
            applyResizedColumnWidth(table, th, newW);
          }

          function onUp() {
            handle.classList.remove('raid-ss-resizing');
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            saveColumnWidths(table);
          }

          document.addEventListener('mousemove', onMove);
          document.addEventListener('mouseup', onUp);
        });
      });
    });
  }

  var COL_WIDTHS_STORAGE = 'colWidths-v2';
  var DEFAULT_WIDE_COL_MIN = 300;
  var DEFAULT_NARROW_COL_MAX = 150;
  var DEFAULT_RELATION_COL_WIDTH = 250;
  var LEGACY_RELATION_COL_WIDTH = 450;
  var COL_RESIZE_MIN = 50;
  var WIDE_TEXT_COLUMNS = { title: true, description: true, cause: true, impact: true };

  function isWideTextColumn(col) {
    return !!(col && WIDE_TEXT_COLUMNS[col]);
  }

  function forEachColumnCell(table, colIdx, fn) {
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    if (!headerRow || !headerRow.children[colIdx]) return;
    fn(headerRow.children[colIdx]);
    table.querySelectorAll('tbody tr, .raid-ss-filter-row').forEach(function (row) {
      var cell = row.children[colIdx];
      if (cell) fn(cell);
    });
  }

  function applyColumnWidthConfig(table, th, config) {
    var colIdx = Array.from(th.parentNode.children).indexOf(th);
    forEachColumnCell(table, colIdx, function (cell) {
      cell.style.width = config.width != null ? config.width + 'px' : '';
      cell.style.minWidth = config.minWidth != null ? config.minWidth + 'px' : '';
      cell.style.maxWidth = config.maxWidth != null ? config.maxWidth + 'px' : '';
    });
  }

  function applyResizedColumnWidth(table, th, widthPx) {
    var colIdx = Array.from(th.parentNode.children).indexOf(th);
    forEachColumnCell(table, colIdx, function (cell) {
      cell.style.width = widthPx + 'px';
      cell.style.minWidth = widthPx + 'px';
      cell.style.maxWidth = '';
    });
  }

  var DEFAULT_LAST_COMMENT_COL_WIDTH = 300;
  var LEGACY_LAST_COMMENT_COL_WIDTH = 150;

  function defaultWidthConfigForColumn(col) {
    if (col === 'relation') {
      return { width: DEFAULT_RELATION_COL_WIDTH, minWidth: COL_RESIZE_MIN, maxWidth: null };
    }
    if (col === 'lastCommentUpdate') {
      return { width: DEFAULT_LAST_COMMENT_COL_WIDTH, minWidth: COL_RESIZE_MIN, maxWidth: null };
    }
    if (isWideTextColumn(col)) {
      return { width: DEFAULT_WIDE_COL_MIN, minWidth: DEFAULT_WIDE_COL_MIN, maxWidth: null };
    }
    return { width: DEFAULT_NARROW_COL_MAX, minWidth: COL_RESIZE_MIN, maxWidth: DEFAULT_NARROW_COL_MAX };
  }

  function applyDefaultColumnWidths(table) {
    table.querySelectorAll('thead .raid-ss-header-row th').forEach(function (th) {
      var col = th.getAttribute('data-col');
      if (!col) return;
      applyColumnWidthConfig(table, th, defaultWidthConfigForColumn(col));
    });
  }

  function storageKey(table, suffix) {
    return STORAGE_PREFIX + registerId + '-' + table.id + '-' + suffix;
  }

  function saveColumnWidths(table) {
    var headers = table.querySelectorAll('thead .raid-ss-header-row th');
    var widths = {};
    headers.forEach(function (th) {
      var col = th.getAttribute('data-col');
      if (col && th.style.width) {
        widths[col] = parseInt(th.style.width, 10);
      }
    });
    try {
      localStorage.setItem(storageKey(table, COL_WIDTHS_STORAGE), JSON.stringify(widths));
    } catch (e) { /* localStorage unavailable */ }
  }

  function restoreColumnWidths() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      try {
        var stored = localStorage.getItem(storageKey(table, COL_WIDTHS_STORAGE));
        if (!stored) {
          applyDefaultColumnWidths(table);
          return;
        }
        var widths = JSON.parse(stored);
        var headers = table.querySelectorAll('thead .raid-ss-header-row th');
        headers.forEach(function (th) {
          var col = th.getAttribute('data-col');
          if (!col) return;
          if (!widths[col]) {
            if (col === 'relation' || col === 'lastCommentUpdate') {
              applyColumnWidthConfig(table, th, defaultWidthConfigForColumn(col));
            }
            return;
          }
          var w = widths[col];
          if (col === 'relation' && (w <= DEFAULT_NARROW_COL_MAX || w === LEGACY_RELATION_COL_WIDTH)) {
            applyColumnWidthConfig(table, th, defaultWidthConfigForColumn(col));
            return;
          }
          if (col === 'lastCommentUpdate' && w === LEGACY_LAST_COMMENT_COL_WIDTH) {
            applyColumnWidthConfig(table, th, defaultWidthConfigForColumn(col));
            return;
          }
          var minW = isWideTextColumn(col) ? Math.max(w, DEFAULT_WIDE_COL_MIN) : COL_RESIZE_MIN;
          applyColumnWidthConfig(table, th, { width: w, minWidth: minW, maxWidth: null });
        });
      } catch (e) {
        applyDefaultColumnWidths(table);
      }
    });
  }


  // ── Comments (ref cell toggle + viewport modals) ──

  var commentContext = null;

  function bindCommentButtons() {
    var commentsModal = document.getElementById('raid-comments-modal');
    var addModal = document.getElementById('raid-comment-add-modal');
    if (!commentsModal || !addModal) return;

    document.addEventListener('click', function (e) {
      var viewAll = e.target.closest('.raid-ss-last-comment-view-all');
      if (viewAll) {
        e.preventDefault();
        e.stopPropagation();
        var entityVa = viewAll.getAttribute('data-entity');
        var idVa = viewAll.getAttribute('data-id');
        var refVa = viewAll.getAttribute('data-ref') || '';
        var rowVa = viewAll.closest('tr.raid-ss-row');
        var toggleVa = rowVa ? rowVa.querySelector('.raid-ss-comment-toggle') : null;
        openCommentsModal(entityVa, idVa, toggleVa, refVa);
        return;
      }

      var toggle = e.target.closest('.raid-ss-comment-toggle');
      if (!toggle) return;
      e.preventDefault();
      e.stopPropagation();

      var entity = toggle.getAttribute('data-entity');
      var id = toggle.getAttribute('data-id');
      var refCell = toggle.closest('.raid-ss-ref-cell');
      var refLink = refCell ? refCell.querySelector('.raid-ss-ref-link') : null;
      var refLabel = refLink ? refLink.textContent.trim() : '';
      openCommentsModal(entity, id, toggle, refLabel);
    });

    commentsModal.querySelectorAll('[data-raid-comments-close]').forEach(function (btn) {
      btn.addEventListener('click', closeCommentsModal);
    });

    addModal.querySelectorAll('[data-raid-comment-add-close]').forEach(function (btn) {
      btn.addEventListener('click', closeAddCommentModal);
    });

    var addOpenBtn = document.getElementById('raid-comments-modal-add');
    if (addOpenBtn) {
      addOpenBtn.addEventListener('click', openAddCommentModal);
    }

    var saveBtn = document.getElementById('raid-comment-add-save');
    var addInput = document.getElementById('raid-comment-add-input');
    if (saveBtn && addInput) {
      saveBtn.addEventListener('click', saveCommentFromModal);
      addInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
          e.preventDefault();
          saveCommentFromModal();
        }
      });
    }
  }

  function openCommentsModal(entity, id, toggle, refLabel) {
    var modal = document.getElementById('raid-comments-modal');
    var grid = document.getElementById('raid-comments-modal-grid');
    var titleEl = document.getElementById('raid-comments-modal-title');
    if (!modal || !grid) return;

    commentContext = { entity: entity, id: id, toggle: toggle };

    document.querySelectorAll('.raid-ss-comment-toggle--open').forEach(function (b) {
      b.classList.remove('raid-ss-comment-toggle--open');
      b.setAttribute('aria-expanded', 'false');
    });
    if (toggle) {
      toggle.classList.add('raid-ss-comment-toggle--open');
      toggle.setAttribute('aria-expanded', 'true');
    }

    if (titleEl) {
      titleEl.textContent = refLabel ? 'Comments on ' + refLabel : 'Comments';
    }
    grid.innerHTML = '<div class="raid-ss-comment-loading">Loading comments\u2026</div>';
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';

    loadCommentsIntoGrid(entity, id, grid);
  }

  function closeCommentsModal() {
    var modal = document.getElementById('raid-comments-modal');
    if (modal) modal.style.display = 'none';
    if (commentContext && commentContext.toggle) {
      commentContext.toggle.classList.remove('raid-ss-comment-toggle--open');
      commentContext.toggle.setAttribute('aria-expanded', 'false');
    }
    commentContext = null;
    if (!isAddCommentModalOpen()) {
      document.body.style.overflow = '';
    }
  }

  function openAddCommentModal() {
    if (!commentContext) return;
    var modal = document.getElementById('raid-comment-add-modal');
    var input = document.getElementById('raid-comment-add-input');
    var titleEl = document.getElementById('raid-comment-add-modal-title');
    if (!modal || !input) return;

    if (titleEl && commentContext) {
      var refLabel = '';
      if (commentContext.toggle) {
        var refCell = commentContext.toggle.closest('.raid-ss-ref-cell');
        var refLink = refCell ? refCell.querySelector('.raid-ss-ref-link') : null;
        refLabel = refLink ? refLink.textContent.trim() : '';
      }
      if (!refLabel) {
        var row = document.querySelector('tr.raid-ss-row[data-entity="' + commentContext.entity + '"][data-id="' + commentContext.id + '"]');
        var lastCell = row ? row.querySelector('td.raid-ss-last-comment-update') : null;
        refLabel = lastCell ? (lastCell.getAttribute('data-ref') || '') : '';
      }
      titleEl.textContent = refLabel ? 'Add comment to ' + refLabel : 'Add comment';
    }

    input.value = '';
    modal.style.display = 'flex';
    input.focus();
  }

  function closeAddCommentModal() {
    var modal = document.getElementById('raid-comment-add-modal');
    if (modal) modal.style.display = 'none';
    if (!isCommentsModalOpen()) {
      document.body.style.overflow = '';
    }
  }

  function isCommentsModalOpen() {
    var modal = document.getElementById('raid-comments-modal');
    return modal && modal.style.display === 'flex';
  }

  function isAddCommentModalOpen() {
    var modal = document.getElementById('raid-comment-add-modal');
    return modal && modal.style.display === 'flex';
  }

  function saveCommentFromModal() {
    if (!commentContext) return;
    var input = document.getElementById('raid-comment-add-input');
    var saveBtn = document.getElementById('raid-comment-add-save');
    if (!input || !saveBtn) return;

    var text = input.value.trim();
    if (!text) { input.focus(); return; }

    var entity = commentContext.entity;
    var id = commentContext.id;
    var toggle = commentContext.toggle || document.querySelector(
      '.raid-ss-comment-toggle[data-entity="' + entity + '"][data-id="' + id + '"]'
    );

    saveBtn.disabled = true;
    saveBtn.textContent = 'Saving\u2026';

    fetch('/api/comments', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': csrfToken
      },
      body: JSON.stringify({ entityType: commentEntityType(entity), entityId: parseInt(id, 10), commentText: text })
    })
    .then(function (r) {
      if (!r.ok) throw new Error('Save failed');
      return r.json();
    })
    .then(function (comment) {
      saveBtn.disabled = false;
      saveBtn.textContent = 'Save comment';
      closeAddCommentModal();

      var current = toggle ? parseInt(toggle.getAttribute('data-comment-count'), 10) || 0 : 0;
      if (toggle) {
        setCommentCount(toggle, current + 1);
      } else if (entity === 'risk') {
        syncLastCommentCellCount(id, current + 1);
      }

      if (entity === 'risk' && comment && comment.createdAt) {
        setLastCommentUpdateForRisk(id, comment.commentText || text, comment.createdAt, 'Comment');
      }

      var grid = document.getElementById('raid-comments-modal-grid');
      if (grid && isCommentsModalOpen()) {
        appendCommentToGrid(grid, comment, true);
        grid.scrollTop = 0;
      }
    })
    .catch(function () {
      saveBtn.disabled = false;
      saveBtn.textContent = 'Save comment';
    });
  }

  function loadCommentsIntoGrid(entity, id, grid) {
    var url = entity === 'risk'
      ? baseUrl + '/api/risk/' + id + '/comment-timeline'
      : '/api/comments/' + commentEntityType(entity) + '/' + id;

    fetch(url)
      .then(function (r) { return r.json(); })
      .then(function (comments) {
        grid.innerHTML = '';
        if (!comments || comments.length === 0) {
          var emptyMsg = entity === 'risk'
            ? 'No comments or mitigation updates yet. Use Add comment to leave a note on this risk.'
            : 'No comments yet. Use Add comment to leave the first note.';
          grid.innerHTML = '<div class="raid-ss-comment-empty">' + emptyMsg + '</div>';
          return;
        }
        if (entity !== 'risk') {
          comments = comments.slice().sort(function (a, b) {
            return new Date(b.createdAt) - new Date(a.createdAt);
          });
        }
        comments.forEach(function (c) {
          appendCommentToGrid(grid, c);
        });
        grid.scrollTop = 0;
      })
      .catch(function () {
        grid.innerHTML = '<div class="raid-ss-comment-empty">Failed to load comments.</div>';
      });
  }

  function appendCommentToGrid(grid, comment, prepend) {
    var empty = grid.querySelector('.raid-ss-comment-empty, .raid-ss-comment-loading');
    if (empty) empty.remove();

    var isMitigationUpdate = comment.kind === 'mitigation-update';
    var div = document.createElement('div');
    div.className = 'raid-ss-comment-item' + (isMitigationUpdate ? ' raid-ss-comment-item--mitigation' : '');
    div.setAttribute('role', 'listitem');
    if (comment.id != null) {
      div.setAttribute('data-comment-id', comment.id);
    }

    var meta = document.createElement('div');
    meta.className = 'raid-ss-comment-meta';
    var who;
    if (isMitigationUpdate) {
      who = 'Mitigation update' + (comment.mitigationTitle ? ': ' + comment.mitigationTitle : '');
    } else {
      who = comment.createdByUser ? comment.createdByUser.name || comment.createdByUser.email : 'Unknown';
    }
    var when = comment.createdAt
      ? new Date(comment.createdAt).toLocaleString('en-GB', { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' })
      : (isMitigationUpdate ? 'Date unknown' : '');
    meta.textContent = when ? who + ' \u00b7 ' + when : who;

    var text = document.createElement('div');
    text.className = 'raid-ss-comment-text';
    text.textContent = comment.commentText;

    div.appendChild(meta);
    div.appendChild(text);

    var firstItem = grid.querySelector('.raid-ss-comment-item');
    if (prepend && firstItem) {
      grid.insertBefore(div, firstItem);
    } else {
      grid.appendChild(div);
    }
  }

  function setCommentCount(toggle, count) {
    if (!toggle) return;
    var n = Math.max(0, count);
    var label = n === 1 ? '1 comment' : n + ' comments';
    var entity = toggle.getAttribute('data-entity');
    var id = toggle.getAttribute('data-id');
    var parent = toggle.parentNode;
    var isRefToggle = toggle.classList.contains('raid-ss-comment-toggle');
    var replacement = createCellIconToggle({
      extraClass: isRefToggle ? 'raid-ss-comment-toggle' : 'raid-ss-last-comment-view-all',
      extraCountClass: isRefToggle ? 'raid-ss-comment-toggle--has-comments' : '',
      icon: 'chat_bubble_outline',
      count: n,
      countLabel: label,
      ariaLabel: n ? label : (isRefToggle ? 'Comments' : 'Comments for ' + (toggle.getAttribute('data-ref') || 'risk')),
      srText: n ? label : (isRefToggle ? 'Comments' : 'Comments'),
      ariaExpanded: isRefToggle ? (toggle.getAttribute('aria-expanded') || 'false') : null,
      dataAttrs: isRefToggle
        ? { 'data-entity': entity, 'data-id': id, 'data-comment-count': n }
        : { 'data-entity': entity, 'data-id': id, 'data-ref': toggle.getAttribute('data-ref') || '' }
    });
    if (isRefToggle && toggle.classList.contains('raid-ss-comment-toggle--open')) {
      replacement.classList.add('raid-ss-comment-toggle--open');
      replacement.setAttribute('aria-expanded', 'true');
    }
    parent.replaceChild(replacement, toggle);

    if (entity === 'risk' && id) {
      syncLastCommentCellCount(id, n);
    }
  }

  function syncLastCommentCellCount(riskId, count) {
    var row = document.querySelector('tr.raid-ss-row[data-entity="risk"][data-id="' + riskId + '"]');
    if (!row) return;
    var viewBtn = row.querySelector('.raid-ss-last-comment-view-all');
    if (!viewBtn) return;
    var n = Math.max(0, count);
    var label = n === 1 ? '1 comment' : n + ' comments';
    viewBtn.classList.toggle('raid-ss-cell-icon-toggle--has-count', n > 0);
    viewBtn.setAttribute('aria-label', n ? 'View ' + label + ' for ' + (viewBtn.getAttribute('data-ref') || 'risk') : 'Comments for ' + (viewBtn.getAttribute('data-ref') || 'risk'));
    var existing = viewBtn.querySelector('.dfe-f-count-badge');
    if (existing) existing.remove();
    if (n > 0) viewBtn.appendChild(createCountBadge(n, label));
  }

  function capitalise(str) {
    return str.charAt(0).toUpperCase() + str.slice(1);
  }

  // ── Word wrap toggle ──

  var TEXT_COLS = ['title', 'description', 'cause', 'impact', 'impactIfRealised', 'contingency', 'assurance', 'financialImpact', 'kris', 'mitigations', 'response'];
  var wrapEnabled = false;

  function bindWrapToggle() {
    document.querySelectorAll('.raid-ss-wrap-toggle-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        wrapEnabled = !wrapEnabled;
        btn.classList.toggle('raid-ss-btn-active', wrapEnabled);
        applyWrapState();
        try {
          localStorage.setItem(STORAGE_PREFIX + registerId + '-wrapText', wrapEnabled ? '1' : '0');
        } catch (e) { }
      });
    });
  }

  function restoreWrapState() {
    try {
      var stored = localStorage.getItem(STORAGE_PREFIX + registerId + '-wrapText');
      if (stored === '1') {
        wrapEnabled = true;
        document.querySelectorAll('.raid-ss-wrap-toggle-btn').forEach(function (btn) {
          btn.classList.add('raid-ss-btn-active');
        });
        applyWrapState();
      }
    } catch (e) { }
  }

  function applyWrapState() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      var headerRow = table.querySelector('thead .raid-ss-header-row');
      if (!headerRow) return;
      var headers = headerRow.querySelectorAll('th');
      headers.forEach(function (th, colIdx) {
        var col = th.getAttribute('data-col');
        if (!col || TEXT_COLS.indexOf(col) === -1) return;
        table.querySelectorAll('tbody tr').forEach(function (row) {
          var cell = row.children[colIdx];
          if (!cell) return;
          var val = cell.querySelector('.raid-ss-val');
          if (val) {
            if (wrapEnabled) {
              val.classList.remove('raid-ss-val-truncate');
            } else {
              val.classList.add('raid-ss-val-truncate');
            }
          }
        });
      });
    });
  }

  // ── Column reorder via drag-and-drop ──

  var reorderMode = false;

  function bindReorderToggle() {
    document.querySelectorAll('.raid-ss-reorder-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        reorderMode = !reorderMode;
        btn.classList.toggle('raid-ss-btn-active', reorderMode);
        document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
          var headers = table.querySelectorAll('thead .raid-ss-header-row th');
          headers.forEach(function (th) {
            if (th.classList.contains('raid-ss-sticky-col')) return;
            var lastTh = headers[headers.length - 1];
            if (th === lastTh) return;
            th.draggable = reorderMode;
            th.classList.toggle('raid-ss-draggable', reorderMode);
          });
        });
      });
    });

    document.addEventListener('dragstart', function (e) {
      var th = e.target.closest && e.target.closest('th.raid-ss-draggable');
      if (!th || !reorderMode) return;
      var table = th.closest('table');
      var headerRow = th.closest('.raid-ss-header-row');
      var colIdx = Array.from(headerRow.children).indexOf(th);
      e.dataTransfer.effectAllowed = 'move';
      e.dataTransfer.setData('text/plain', colIdx);
      th.classList.add('raid-ss-dragging');
      table._dragSourceIdx = colIdx;
    });

    document.addEventListener('dragover', function (e) {
      var th = e.target.closest && e.target.closest('th.raid-ss-draggable');
      if (!th) return;
      e.preventDefault();
      e.dataTransfer.dropEffect = 'move';
      document.querySelectorAll('.raid-ss-drag-over').forEach(function (el) {
        el.classList.remove('raid-ss-drag-over');
      });
      th.classList.add('raid-ss-drag-over');
    });

    document.addEventListener('dragleave', function (e) {
      var th = e.target.closest && e.target.closest('th.raid-ss-draggable');
      if (th) th.classList.remove('raid-ss-drag-over');
    });

    document.addEventListener('drop', function (e) {
      var th = e.target.closest && e.target.closest('th.raid-ss-draggable');
      if (!th) return;
      e.preventDefault();
      var table = th.closest('table');
      var headerRow = th.closest('.raid-ss-header-row');
      var fromIdx = table._dragSourceIdx;
      var toIdx = Array.from(headerRow.children).indexOf(th);
      if (fromIdx === undefined || fromIdx === toIdx) return;

      moveColumn(table, fromIdx, toIdx);
      saveColumnOrder(table);
      rebuildFilterRow(table);

      document.querySelectorAll('.raid-ss-dragging').forEach(function (el) {
        el.classList.remove('raid-ss-dragging');
      });
      document.querySelectorAll('.raid-ss-drag-over').forEach(function (el) {
        el.classList.remove('raid-ss-drag-over');
      });
      delete table._dragSourceIdx;
    });

    document.addEventListener('dragend', function () {
      document.querySelectorAll('.raid-ss-dragging').forEach(function (el) {
        el.classList.remove('raid-ss-dragging');
      });
      document.querySelectorAll('.raid-ss-drag-over').forEach(function (el) {
        el.classList.remove('raid-ss-drag-over');
      });
    });
  }

  function moveColumn(table, fromIdx, toIdx) {
    var allRows = table.querySelectorAll('thead tr, tbody tr, tfoot tr');
    allRows.forEach(function (row) {
      var cells = Array.from(row.children);
      if (fromIdx >= cells.length || toIdx >= cells.length) return;
      var moving = cells[fromIdx];
      var target = cells[toIdx];
      if (fromIdx < toIdx) {
        row.insertBefore(moving, target.nextSibling);
      } else {
        row.insertBefore(moving, target);
      }
    });
  }

  function saveColumnOrder(table) {
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    var order = [];
    headerRow.querySelectorAll('th').forEach(function (th) {
      var col = th.getAttribute('data-col');
      order.push(col || '');
    });
    try {
      localStorage.setItem(storageKey(table, 'colOrder'), JSON.stringify(order));
    } catch (e) { }
  }

  function restoreColumnOrder() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      try {
        var stored = localStorage.getItem(storageKey(table, 'colOrder'));
        var order = stored ? JSON.parse(stored) : null;
        if (!order && table.dataset.defaultColOrder) {
          order = JSON.parse(table.dataset.defaultColOrder);
        }
        if (!order || !order.length) return;

        var headerRow = table.querySelector('thead .raid-ss-header-row');
        var currentHeaders = headerRow.querySelectorAll('th');
        if (order.length !== currentHeaders.length) return;

        var currentOrder = [];
        currentHeaders.forEach(function (th) {
          currentOrder.push(th.getAttribute('data-col') || '');
        });

        var orderMatch = order.every(function (col, i) { return col === currentOrder[i]; });
        if (orderMatch) return;

        applyColumnOrder(table, order);
      } catch (e) { }
    });
  }

  function rebuildFilterRow(table) {
    var filterRow = table.querySelector('.raid-ss-filter-row');
    if (!filterRow) return;
    var headerRow = filterRow.previousElementSibling;
    if (!headerRow) return;
    var headers = headerRow.querySelectorAll('th');
    var wasVisible = filterRow.style.display !== 'none';
    filterRow.innerHTML = '';

    headers.forEach(function (th) {
      var td = document.createElement('td');
      td.className = 'govuk-table__cell raid-ss-filter-cell';
      td.style.padding = '4px 6px';
      populateFilterCell(td, th, table, headerRow);

      if (th.classList.contains('raid-ss-sticky-col')) {
        td.classList.add('raid-ss-sticky-col');
      }
      if (th.classList.contains('raid-ss-group-start')) {
        td.classList.add('raid-ss-group-start');
      }
      if (th.classList.contains('raid-ss-group-end')) {
        td.classList.add('raid-ss-group-end');
      }

      filterRow.appendChild(td);
    });

    filterRow.style.display = wasVisible ? '' : 'none';
    syncHeaderScrollMetrics(table);
  }

  function applyStickyHeaderCellStyles(th) {
    th.style.position = 'sticky';
    th.style.top = '0';
    if (th.classList.contains('raid-ss-sticky-col')) {
      th.style.left = '0';
      th.style.zIndex = '10';
    } else {
      th.style.left = '';
      th.style.zIndex = '5';
    }
  }

  function syncHeaderScrollMetrics(table) {
    var scroll = table.closest('.raid-spreadsheet-scroll');
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    if (!scroll || !headerRow) return;
    scroll.style.setProperty('--raid-ss-header-height', headerRow.offsetHeight + 'px');
  }

  function syncAllHeaderScrollMetrics() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(syncHeaderScrollMetrics);
  }

  function getTableScrollWrapper(table) {
    return table ? table.closest('.raid-spreadsheet-scroll') : null;
  }

  function getActiveSpreadsheetTable() {
    var panel = document.querySelector('.raid-ss-tab-panel.raid-ss-tab-active');
    if (panel) {
      var t = panel.querySelector('.raid-spreadsheet-table');
      if (t) return t;
    }
    return document.querySelector('.raid-spreadsheet-table');
  }

  function getReorderableHeaders(table) {
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    if (!headerRow) return [];
    return Array.from(headerRow.querySelectorAll('th')).filter(function (th) {
      if (th.classList.contains('raid-ss-sticky-col')) return false;
      if (!th.getAttribute('data-col')) return false;
      return true;
    });
  }

  function bindAccessibleReorder() {
    var modal = document.getElementById('raid-reorder-a11y-modal');
    if (!modal) return;

    var select = document.getElementById('raid-reorder-a11y-select');
    var live = document.getElementById('raid-reorder-a11y-live');
    var upBtn = document.getElementById('raid-reorder-a11y-up');
    var downBtn = document.getElementById('raid-reorder-a11y-down');
    var doneBtn = document.getElementById('raid-reorder-a11y-done');

    function closeModal() {
      modal.style.display = 'none';
      document.body.style.overflow = '';
      a11yReorderTable = null;
    }

    function refreshSelect() {
      if (!a11yReorderTable || !select) return;
      var headers = getReorderableHeaders(a11yReorderTable);
      var current = select.value;
      select.innerHTML = '';
      headers.forEach(function (th) {
        var opt = document.createElement('option');
        opt.value = th.getAttribute('data-col');
        opt.textContent = th.textContent.trim();
        select.appendChild(opt);
      });
      if (current) select.value = current;
      if (!select.value && select.options.length) select.selectedIndex = 0;
    }

    function moveSelected(direction) {
      if (!a11yReorderTable || !select || !select.value) return;
      var headerRow = a11yReorderTable.querySelector('thead .raid-ss-header-row');
      var headers = getReorderableHeaders(a11yReorderTable);
      var col = select.value;
      var idx = headers.findIndex(function (th) { return th.getAttribute('data-col') === col; });
      if (idx < 0) return;
      var targetIdx = direction === 'up' ? idx - 1 : idx + 1;
      if (targetIdx < 0 || targetIdx >= headers.length) return;
      var fromIdx = Array.from(headerRow.children).indexOf(headers[idx]);
      var toIdx = Array.from(headerRow.children).indexOf(headers[targetIdx]);
      moveColumn(a11yReorderTable, fromIdx, toIdx);
      saveColumnOrder(a11yReorderTable);
      rebuildFilterRow(a11yReorderTable);
      refreshSelect();
      select.value = col;
      if (live) live.textContent = select.options[select.selectedIndex].text + ' moved ' + direction + '.';
    }

    function openModal() {
      a11yReorderTable = getActiveSpreadsheetTable();
      if (!a11yReorderTable) return;
      refreshSelect();
      modal.style.display = 'flex';
      document.body.style.overflow = 'hidden';
      if (select) select.focus();
    }

    document.querySelectorAll('.raid-ss-reorder-a11y-btn').forEach(function (btn) {
      btn.addEventListener('click', openModal);
    });

    if (upBtn) upBtn.addEventListener('click', function () { moveSelected('up'); });
    if (downBtn) downBtn.addEventListener('click', function () { moveSelected('down'); });
    if (doneBtn) doneBtn.addEventListener('click', closeModal);
    modal.querySelectorAll('.raid-modal-close').forEach(function (btn) {
      btn.addEventListener('click', closeModal);
    });
  }

  function resizeColumnByDelta(table, th, delta) {
    var col = th.getAttribute('data-col');
    var floor = isWideTextColumn(col) ? DEFAULT_WIDE_COL_MIN : COL_RESIZE_MIN;
    var newW = Math.max(floor, th.offsetWidth + delta);
    applyResizedColumnWidth(table, th, newW);
    saveColumnWidths(table);
  }

  function setupHeaderKeyboardResize() {
    document.querySelectorAll('.raid-spreadsheet-table thead .raid-ss-header-row th').forEach(function (th) {
      if (!th.hasAttribute('tabindex')) th.setAttribute('tabindex', '0');
      th.addEventListener('keydown', function (e) {
        if (!e.altKey || (e.key !== 'ArrowLeft' && e.key !== 'ArrowRight')) return;
        var table = th.closest('table');
        if (!table) return;
        e.preventDefault();
        resizeColumnByDelta(table, th, e.key === 'ArrowRight' ? 20 : -20);
      });
    });
  }

  // ── Table zoom and reset ──

  var ZOOM_DEFAULT = 1;
  var ZOOM_MIN = 0.75;
  var ZOOM_MAX = 1.5;
  var ZOOM_STEP = 0.1;

  function getDefaultColumnOrderForTable(table) {
    var entityType = table.getAttribute('data-entity-type');
    if (entityType && serverTableLayouts[entityType] && serverTableLayouts[entityType].columnOrder) {
      return serverTableLayouts[entityType].columnOrder.slice();
    }
    var order = [];
    table.querySelectorAll('thead .raid-ss-header-row th').forEach(function (th) {
      order.push(th.getAttribute('data-col') || '');
    });
    return order;
  }

  function applyColumnOrder(table, order) {
    if (!order || !order.length) return;
    var headerRow = table.querySelector('thead .raid-ss-header-row');
    if (!headerRow) return;

    for (var i = 0; i < order.length; i++) {
      var col = order[i];
      var headers = Array.from(headerRow.querySelectorAll('th'));
      var currentIdx = -1;
      for (var j = i; j < headers.length; j++) {
        var hCol = headers[j].getAttribute('data-col') || '';
        if (hCol === col) { currentIdx = j; break; }
      }
      if (currentIdx > i) {
        moveColumn(table, currentIdx, i);
      }
    }
    rebuildFilterRow(table);
  }

  function captureDefaultColumnOrders() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      if (table.dataset.defaultColOrder) return;
      table.dataset.defaultColOrder = JSON.stringify(getDefaultColumnOrderForTable(table));
    });
  }

  function getTableZoom(table) {
    var wrapper = getTableScrollWrapper(table);
    if (!wrapper) return ZOOM_DEFAULT;
    var z = parseFloat(wrapper.style.zoom);
    return isNaN(z) || z <= 0 ? ZOOM_DEFAULT : z;
  }

  function applyTableZoom(table, zoom) {
    var wrapper = getTableScrollWrapper(table);
    if (!wrapper) return ZOOM_DEFAULT;
    var level = Math.round(zoom * 100) / 100;
    level = Math.max(ZOOM_MIN, Math.min(ZOOM_MAX, level));
    wrapper.style.zoom = level === ZOOM_DEFAULT ? '' : String(level);
    try {
      if (level === ZOOM_DEFAULT) {
        localStorage.removeItem(storageKey(table, 'zoom'));
      } else {
        localStorage.setItem(storageKey(table, 'zoom'), String(level));
      }
    } catch (e) { /* localStorage unavailable */ }
    syncHeaderScrollMetrics(table);
    return level;
  }

  function restoreTableZoom() {
    document.querySelectorAll('.raid-spreadsheet-table').forEach(function (table) {
      table.style.zoom = '';
      try {
        var stored = localStorage.getItem(storageKey(table, 'zoom'));
        if (stored) {
          var level = parseFloat(stored);
          if (!isNaN(level)) applyTableZoom(table, level);
        }
      } catch (e) { /* parse error */ }
    });
  }

  function clearColumnWidths(table) {
    try {
      localStorage.removeItem(storageKey(table, COL_WIDTHS_STORAGE));
      localStorage.removeItem(storageKey(table, 'colWidths'));
    } catch (e) { /* localStorage unavailable */ }
    table.querySelectorAll('thead .raid-ss-header-row th, tbody tr td, .raid-ss-filter-row td').forEach(function (cell) {
      cell.style.width = '';
      cell.style.minWidth = '';
      cell.style.maxWidth = '';
    });
    applyDefaultColumnWidths(table);
  }

  function resetColumnOrderToDefault(table) {
    var orderJson = table.dataset.defaultColOrder;
    if (!orderJson) {
      table.dataset.defaultColOrder = JSON.stringify(getDefaultColumnOrderForTable(table));
      orderJson = table.dataset.defaultColOrder;
    }
    if (!orderJson) return;
    try {
      var order = JSON.parse(orderJson);
      if (!order.length) return;
      applyColumnOrder(table, order);
      try {
        localStorage.removeItem(storageKey(table, 'colOrder'));
      } catch (e) { /* localStorage unavailable */ }
    } catch (e) { /* parse error */ }
  }

  function resetTableView(table) {
    if (!table) return;
    applyTableZoom(table, ZOOM_DEFAULT);
    clearColumnWidths(table);
    resetColumnOrderToDefault(table);
    syncHeaderScrollMetrics(table);
  }

  function bindClearFiltersButtons() {
    document.querySelectorAll('.raid-ss-clear-filters-btn').forEach(function (btn) {
      btn.addEventListener('click', clearFilters);
    });
  }

  function bindZoomControls() {
    document.querySelectorAll('.raid-ss-zoom-in-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var table = getActiveSpreadsheetTable();
        if (!table) return;
        applyTableZoom(table, getTableZoom(table) + ZOOM_STEP);
      });
    });

    document.querySelectorAll('.raid-ss-zoom-out-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var table = getActiveSpreadsheetTable();
        if (!table) return;
        applyTableZoom(table, getTableZoom(table) - ZOOM_STEP);
      });
    });
  }

  var resetPendingTable = null;

  function openResetTableModal(table) {
    resetPendingTable = table;
    var modal = document.getElementById('raid-reset-table-modal');
    if (!modal) return;
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    var confirmBtn = document.getElementById('raid-reset-table-confirm');
    if (confirmBtn) confirmBtn.focus();
  }

  function closeResetTableModal() {
    resetPendingTable = null;
    var modal = document.getElementById('raid-reset-table-modal');
    if (modal) modal.style.display = 'none';
    document.body.style.overflow = '';
  }

  function bindResetTableModal() {
    document.querySelectorAll('.raid-ss-reset-table-btn').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var table = getActiveSpreadsheetTable();
        if (!table) return;
        openResetTableModal(table);
      });
    });

    var confirmBtn = document.getElementById('raid-reset-table-confirm');
    if (confirmBtn) {
      confirmBtn.addEventListener('click', function () {
        if (resetPendingTable) resetTableView(resetPendingTable);
        closeResetTableModal();
      });
    }

    document.querySelectorAll('[data-raid-reset-table-close]').forEach(function (btn) {
      btn.addEventListener('click', closeResetTableModal);
    });

    var modal = document.getElementById('raid-reset-table-modal');
    if (modal) {
      modal.addEventListener('click', function (e) {
        if (e.target === modal) closeResetTableModal();
      });
    }
  }

  // ── Mitigations (register spreadsheet) ──

  var mitigationContext = null;
  var mitigationFormMode = 'add';
  var mitigationOwnerDebounce = null;

  function applyReadOnlySpreadsheetUi() {
    document.querySelectorAll('[id^="btn-add-"], #btn-link-risk-top').forEach(function (el) {
      el.style.display = 'none';
    });
    document.querySelectorAll('.raid-ss-add-btn, .raid-ss-add-row').forEach(function (el) {
      el.style.display = 'none';
    });
    document.querySelectorAll('.raid-ss-relation-change-btn').forEach(function (el) {
      el.style.display = 'none';
    });
    var mitAdd = document.getElementById('raid-mitigations-modal-add');
    if (mitAdd) mitAdd.style.display = 'none';
    var kriAdd = document.getElementById('raid-kris-modal-add');
    if (kriAdd) kriAdd.style.display = 'none';
  }

  function bindMitigationButtons() {
    var listModal = document.getElementById('raid-mitigations-modal');
    var formModal = document.getElementById('raid-mitigation-form-modal');
    if (!listModal || !formModal) return;

    document.addEventListener('click', function (e) {
      var openBtn = e.target.closest('.raid-ss-mitigation-open-btn');
      if (openBtn) {
        e.preventDefault();
        e.stopPropagation();
        var riskIdOpen = parseInt(openBtn.getAttribute('data-risk-id'), 10);
        var refOpen = openBtn.getAttribute('data-ref-label') || '';
        var mitCount = parseInt(openBtn.getAttribute('data-mitigation-count'), 10) || 0;
        openMitigationsModal(riskIdOpen, refOpen, openBtn);
        if (mitCount === 0 && !readOnly) {
          openMitigationFormModal('add');
        }
        return;
      }

      var editBtn = e.target.closest('[data-raid-mitigation-edit]');
      if (editBtn && mitigationContext && !readOnly) {
        e.preventDefault();
        openMitigationFormModal('edit', {
          id: parseInt(editBtn.getAttribute('data-action-id'), 10),
          title: editBtn.getAttribute('data-title') || '',
          assignedToUserId: editBtn.getAttribute('data-owner-id') || '',
          owner: editBtn.getAttribute('data-owner') || '',
          dueDate: editBtn.getAttribute('data-due') || '',
          status: editBtn.getAttribute('data-status') || 'Not started'
        });
      }
    });

    listModal.querySelectorAll('[data-raid-mitigations-close]').forEach(function (btn) {
      btn.addEventListener('click', closeMitigationsModal);
    });
    formModal.querySelectorAll('[data-raid-mitigation-form-close]').forEach(function (btn) {
      btn.addEventListener('click', closeMitigationFormModal);
    });

    var addBtn = document.getElementById('raid-mitigations-modal-add');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        if (!mitigationContext || readOnly) return;
        openMitigationFormModal('add');
      });
    }

    var saveBtn = document.getElementById('raid-mitigation-form-save');
    if (saveBtn) saveBtn.addEventListener('click', saveMitigationForm);

    var ownerSearch = document.getElementById('raid-mitigation-form-owner-search');
    if (ownerSearch) {
      ownerSearch.addEventListener('input', onMitigationOwnerSearchInput);
    }
  }

  function openMitigationsModal(riskId, refLabel, toggleBtn) {
    var modal = document.getElementById('raid-mitigations-modal');
    var list = document.getElementById('raid-mitigations-modal-list');
    var titleEl = document.getElementById('raid-mitigations-modal-title');
    if (!modal || !list) return;

    mitigationContext = { riskId: riskId, refLabel: refLabel, toggleBtn: toggleBtn };
    if (titleEl) {
      titleEl.textContent = refLabel ? 'Mitigations for ' + refLabel : 'Mitigations';
    }
    list.innerHTML = '<div class="raid-ss-mitigation-loading">Loading mitigations\u2026</div>';
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    loadMitigationsIntoList(riskId, list);
  }

  function closeMitigationsModal() {
    var modal = document.getElementById('raid-mitigations-modal');
    if (modal) modal.style.display = 'none';
    if (!isMitigationFormModalOpen()) {
      document.body.style.overflow = '';
    }
    mitigationContext = null;
  }

  function isMitigationsModalOpen() {
    var modal = document.getElementById('raid-mitigations-modal');
    return modal && modal.style.display === 'flex';
  }

  function isMitigationFormModalOpen() {
    var modal = document.getElementById('raid-mitigation-form-modal');
    return modal && modal.style.display === 'flex';
  }

  function loadMitigationsIntoList(riskId, list) {
    fetch(baseUrl + '/api/risk/' + riskId + '/mitigations')
      .then(function (r) { return parseJsonResponse(r); })
      .then(function (data) {
        list.innerHTML = '';
        var items = (data && data.mitigations) || [];
        if (!items.length) {
          list.innerHTML = '<div class="raid-ss-mitigation-empty">No mitigation actions yet. Use Add mitigation to create one.</div>';
          return;
        }
        items.forEach(function (m) {
          list.appendChild(renderMitigationListItem(m));
        });
      })
      .catch(function () {
        list.innerHTML = '<div class="raid-ss-mitigation-empty">Failed to load mitigations.</div>';
      });
  }

  function renderMitigationListItem(m) {
    var card = document.createElement('article');
    var status = m.effectiveStatus || m.status || 'Not started';
    card.className = 'raid-ss-mitigation-card ' + mitigationCardStatusModifier(status);

    var head = document.createElement('div');
    head.className = 'raid-ss-mitigation-card-head';

    var title = document.createElement('h3');
    title.className = 'govuk-heading-s govuk-!-margin-bottom-0 raid-ss-mitigation-card__title';
    title.textContent = m.title || '';

    var badge = document.createElement('span');
    badge.className = mitigationStatusBadgeClass(status);
    badge.textContent = status;

    head.appendChild(title);
    head.appendChild(badge);
    card.appendChild(head);

    var meta = document.createElement('p');
    meta.className = 'govuk-body-s raid-ss-mitigation-card__meta';
    var metaParts = [];
    if (m.owner) metaParts.push('Owner: ' + m.owner);
    if (m.dueDateDisplay) metaParts.push('Target: ' + m.dueDateDisplay);
    meta.textContent = metaParts.length ? metaParts.join(' · ') : '—';
    card.appendChild(meta);

    var noteLines = parseMitigationNoteLines(m.notes);
    if (noteLines.length) {
      var updates = document.createElement('div');
      updates.className = 'raid-ss-mitigation-updates';
      var updatesTitle = document.createElement('h4');
      updatesTitle.className = 'govuk-body-s govuk-!-font-weight-bold govuk-!-margin-bottom-1';
      updatesTitle.textContent = 'Progress updates';
      updates.appendChild(updatesTitle);

      var list = document.createElement('ol');
      list.className = 'raid-ss-mitigation-updates__list';
      noteLines.forEach(function (line) {
        var item = document.createElement('li');
        item.className = 'raid-ss-mitigation-updates__item';
        if (line.when) {
          var when = document.createElement('span');
          when.className = 'raid-ss-mitigation-updates__when';
          when.textContent = line.when;
          item.appendChild(when);
        }
        var text = document.createElement('span');
        text.className = 'raid-ss-mitigation-updates__text';
        text.textContent = line.text;
        item.appendChild(text);
        list.appendChild(item);
      });
      updates.appendChild(list);
      card.appendChild(updates);
    }

    if (!readOnly) {
      var editBtn = document.createElement('button');
      editBtn.type = 'button';
      editBtn.className = 'govuk-link govuk-body-s raid-ss-mitigation-card__edit';
      editBtn.setAttribute('data-raid-mitigation-edit', '');
      editBtn.setAttribute('data-action-id', String(m.id));
      editBtn.setAttribute('data-title', m.title || '');
      editBtn.setAttribute('data-owner-id', m.assignedToUserId ? String(m.assignedToUserId) : '');
      editBtn.setAttribute('data-owner', m.owner || '');
      editBtn.setAttribute('data-due', m.dueDate || '');
      editBtn.setAttribute('data-status', m.effectiveStatus || m.status || 'Not started');
      editBtn.textContent = 'Edit or add update';
      card.appendChild(editBtn);
    }

    return card;
  }

  function escapeHtmlAttr(s) {
    return String(s || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;');
  }

  function openMitigationFormModal(mode, mitigation) {
    if (!mitigationContext) return;
    mitigationFormMode = mode;
    var modal = document.getElementById('raid-mitigation-form-modal');
    var titleEl = document.getElementById('raid-mitigation-form-modal-title');
    var statusWrap = document.getElementById('raid-mitigation-form-status-wrap');
    var noteWrap = document.getElementById('raid-mitigation-form-note-wrap');
    var titleInput = document.getElementById('raid-mitigation-form-title');
    var ownerSearch = document.getElementById('raid-mitigation-form-owner-search');
    var ownerId = document.getElementById('raid-mitigation-form-owner-id');
    var dueInput = document.getElementById('raid-mitigation-form-due');
    var statusSelect = document.getElementById('raid-mitigation-form-status');
    var noteInput = document.getElementById('raid-mitigation-form-note');
    var errEl = document.getElementById('raid-mitigation-form-error');
    if (!modal || !titleInput || !ownerSearch || !ownerId || !dueInput) return;

    if (titleEl) {
      titleEl.textContent = mode === 'edit' ? 'Edit mitigation' : 'Add mitigation';
    }
    if (statusWrap) statusWrap.style.display = mode === 'edit' ? '' : 'none';
    if (noteWrap) noteWrap.style.display = mode === 'edit' ? '' : 'none';
    if (errEl) { errEl.style.display = 'none'; errEl.textContent = ''; }

    titleInput.value = mitigation && mitigation.title ? mitigation.title : '';
    ownerSearch.value = mitigation && mitigation.owner ? mitigation.owner : '';
    ownerId.value = mitigation && mitigation.assignedToUserId ? mitigation.assignedToUserId : '';
    dueInput.value = mitigation && mitigation.dueDate ? mitigation.dueDate : '';
    if (statusSelect && mitigation && mitigation.status) statusSelect.value = mitigation.status;
    if (noteInput) noteInput.value = '';

    modal.setAttribute('data-action-id', mitigation && mitigation.id ? mitigation.id : '');
    modal.style.display = 'flex';
    document.body.style.overflow = 'hidden';
    titleInput.focus();
  }

  function closeMitigationFormModal() {
    var modal = document.getElementById('raid-mitigation-form-modal');
    if (modal) modal.style.display = 'none';
    if (!isMitigationsModalOpen()) {
      document.body.style.overflow = '';
    }
  }

  function showMitigationFormError(msg) {
    var errEl = document.getElementById('raid-mitigation-form-error');
    if (!errEl) return;
    errEl.textContent = msg;
    errEl.style.display = msg ? '' : 'none';
  }

  function onMitigationOwnerSearchInput() {
    clearTimeout(mitigationOwnerDebounce);
    var searchInput = document.getElementById('raid-mitigation-form-owner-search');
    var listEl = document.getElementById('raid-mitigation-form-owner-list');
    var ownerId = document.getElementById('raid-mitigation-form-owner-id');
    var hintEl = document.getElementById('raid-mitigation-form-owner-hint');
    if (!searchInput || !listEl) return;

    ownerId.value = '';
    var q = searchInput.value.trim();
    if (q.length < 2) {
      listEl.style.display = 'none';
      listEl.innerHTML = '';
      if (hintEl) { hintEl.style.display = ''; hintEl.textContent = 'Type at least 2 characters to search.'; }
      return;
    }
    if (hintEl) hintEl.textContent = 'Searching\u2026';
    mitigationOwnerDebounce = setTimeout(function () {
      fetch('/api/users/search?q=' + encodeURIComponent(q) + '&top=10')
        .then(function (r) { return r.json(); })
        .then(function (users) {
          if (!Array.isArray(users)) users = users.users || users.value || [];
          listEl.innerHTML = '';
          if (!users.length) {
            listEl.style.display = 'none';
            if (hintEl) { hintEl.style.display = ''; hintEl.textContent = 'No users found.'; }
            return;
          }
          if (hintEl) hintEl.style.display = 'none';
          users.forEach(function (u) {
            var li = document.createElement('li');
            li.className = 'raid-ss-mitigation-owner-item';
            var displayName = u.displayName || u.name || u.email;
            li.textContent = displayName;
            li.addEventListener('click', function () {
              ownerId.value = u.id;
              searchInput.value = displayName;
              listEl.style.display = 'none';
            });
            listEl.appendChild(li);
          });
          listEl.style.display = '';
        })
        .catch(function () {
          listEl.style.display = 'none';
          if (hintEl) { hintEl.style.display = ''; hintEl.textContent = 'Search failed.'; }
        });
    }, 300);
  }

  function saveMitigationForm() {
    if (!mitigationContext) return;
    var saveBtn = document.getElementById('raid-mitigation-form-save');
    var titleInput = document.getElementById('raid-mitigation-form-title');
    var ownerId = document.getElementById('raid-mitigation-form-owner-id');
    var dueInput = document.getElementById('raid-mitigation-form-due');
    var statusSelect = document.getElementById('raid-mitigation-form-status');
    var noteInput = document.getElementById('raid-mitigation-form-note');
    var formModal = document.getElementById('raid-mitigation-form-modal');
    if (!saveBtn || !titleInput || !ownerId || !dueInput || !formModal) return;

    var title = titleInput.value.trim();
    var assignedToUserId = parseInt(ownerId.value, 10);
    var targetDate = dueInput.value;
    if (!title) { showMitigationFormError('Enter the mitigation action.'); titleInput.focus(); return; }
    if (!assignedToUserId) { showMitigationFormError('Select an owner.'); return; }
    if (!targetDate) { showMitigationFormError('Enter a target date.'); dueInput.focus(); return; }
    showMitigationFormError('');

    var riskId = mitigationContext.riskId;
    var isEdit = mitigationFormMode === 'edit';
    var actionId = formModal.getAttribute('data-action-id');
    var url = baseUrl + '/api/risk/' + riskId + '/mitigations';
    var body = {
      title: title,
      assignedToUserId: assignedToUserId,
      targetDate: targetDate
    };
    if (isEdit && actionId) {
      url += '/' + actionId;
      body.status = statusSelect ? statusSelect.value : 'Not started';
      body.updateNote = noteInput ? noteInput.value.trim() : '';
    }

    saveBtn.disabled = true;
    saveBtn.textContent = 'Saving\u2026';

    fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': csrfToken
      },
      body: JSON.stringify(body)
    })
      .then(function (r) { return parseJsonResponse(r); })
      .then(function (data) {
        saveBtn.disabled = false;
        saveBtn.textContent = 'Save';
        if (data.error) {
          showMitigationFormError(data.error);
          return;
        }
        closeMitigationFormModal();
        var count = data.mitigationCount;
        if (typeof count !== 'number' && isEdit) {
          count = parseInt(document.querySelector('.raid-ss-mitigation-open-btn[data-risk-id="' + riskId + '"]')?.getAttribute('data-mitigation-count') || '0', 10);
        }
        if (typeof count === 'number') {
          setMitigationCountForRisk(riskId, count);
        } else if (!isEdit) {
          var prev = parseInt(document.querySelector('.raid-ss-mitigation-open-btn[data-risk-id="' + riskId + '"]')?.getAttribute('data-mitigation-count') || '0', 10);
          setMitigationCountForRisk(riskId, prev + 1);
        }
        var list = document.getElementById('raid-mitigations-modal-list');
        if (list && isMitigationsModalOpen()) {
          loadMitigationsIntoList(riskId, list);
        }
        if (isEdit && body.updateNote) {
          var noteText = body.updateNote;
          setLastCommentUpdateForRisk(riskId, noteText, new Date().toISOString(), 'Mitigation update');
          var commentToggle = document.querySelector(
            '.raid-ss-comment-toggle[data-entity="risk"][data-id="' + riskId + '"]');
          if (commentToggle) {
            var commentN = parseInt(commentToggle.getAttribute('data-comment-count'), 10) || 0;
            setCommentCount(commentToggle, commentN + 1);
          }
          if (commentContext && commentContext.entity === 'risk' && String(commentContext.id) === String(riskId)) {
            var commentGrid = document.getElementById('raid-comments-modal-grid');
            if (commentGrid && isCommentsModalOpen()) {
              loadCommentsIntoGrid('risk', riskId, commentGrid);
            }
          }
        }
      })
      .catch(function (err) {
        saveBtn.disabled = false;
        saveBtn.textContent = 'Save';
        showMitigationFormError(err && err.message ? err.message : 'Save failed.');
      });
  }

  // ── Key risk indicators (register spreadsheet) ──

  var kriContext = null;
  var kriFormMode = 'add';

  function bindKriButtons() {
    var listModal = document.getElementById('raid-kris-modal');
    var formModal = document.getElementById('raid-kri-form-modal');
    if (!listModal || !formModal) return;

    document.addEventListener('click', function (e) {
      var openBtn = e.target.closest('.raid-ss-kri-open-btn');
      if (openBtn) {
        e.preventDefault();
        openKrisModal(
          parseInt(openBtn.getAttribute('data-risk-id'), 10),
          openBtn.getAttribute('data-ref-label') || '',
          openBtn
        );
        return;
      }
      var editBtn = e.target.closest('[data-raid-kri-edit]');
      if (editBtn && kriContext && !readOnly) {
        e.preventDefault();
        openKriFormModal('edit', {
          id: parseInt(editBtn.getAttribute('data-kri-id'), 10),
          title: editBtn.getAttribute('data-kri-title') || '',
          description: editBtn.getAttribute('data-kri-description') || '',
          metric: editBtn.getAttribute('data-kri-metric') || '',
          threshold: editBtn.getAttribute('data-kri-threshold') || ''
        });
      }
    });

    listModal.querySelectorAll('[data-raid-kris-close]').forEach(function (btn) {
      btn.addEventListener('click', closeKrisModal);
    });
    formModal.querySelectorAll('[data-raid-kri-form-close]').forEach(function (btn) {
      btn.addEventListener('click', closeKriFormModal);
    });

    var addBtn = document.getElementById('raid-kris-modal-add');
    if (addBtn) {
      addBtn.addEventListener('click', function () {
        if (!kriContext || readOnly) return;
        openKriFormModal('add');
      });
    }

    var saveBtn = document.getElementById('raid-kri-form-save');
    if (saveBtn) saveBtn.addEventListener('click', saveKriForm);

    var removeBtn = document.getElementById('raid-kri-form-remove');
    if (removeBtn) removeBtn.addEventListener('click', removeKriForm);
  }

  function openKrisModal(riskId, refLabel, toggleBtn) {
    var modal = document.getElementById('raid-kris-modal');
    var list = document.getElementById('raid-kris-modal-list');
    var titleEl = document.getElementById('raid-kris-modal-title');
    if (!modal || !list) return;

    kriContext = { riskId: riskId, refLabel: refLabel, toggleBtn: toggleBtn };
    if (titleEl) {
      titleEl.textContent = refLabel ? 'Key risk indicators for ' + refLabel : 'Key risk indicators';
    }
    list.innerHTML = '<div class="raid-ss-kri-loading">Loading key risk indicators\u2026</div>';
    modal.style.display = 'flex';
    document.body.classList.add('raid-modal-open');
    loadKrisIntoList(riskId, list);
  }

  function closeKrisModal() {
    var modal = document.getElementById('raid-kris-modal');
    if (modal) modal.style.display = 'none';
    if (!isKriFormModalOpen()) {
      document.body.classList.remove('raid-modal-open');
    }
    kriContext = null;
  }

  function isKrisModalOpen() {
    var modal = document.getElementById('raid-kris-modal');
    return modal && modal.style.display === 'flex';
  }

  function isKriFormModalOpen() {
    var modal = document.getElementById('raid-kri-form-modal');
    return modal && modal.style.display === 'flex';
  }

  function loadKrisIntoList(riskId, list) {
    fetch(baseUrl + '/api/risk/' + riskId + '/kris')
      .then(function (r) { return r.json(); })
      .then(function (data) {
        if (kriContext && kriContext.riskId === riskId && data.krisSummary !== undefined) {
          setKriCountForRisk(riskId, data.count || 0, data.krisSummary);
        }
        var items = (data && data.kris) || [];
        if (!items.length) {
          list.innerHTML = '<div class="raid-ss-kri-empty">No key risk indicators yet. Use Add KRI to create one.</div>';
          return;
        }
        list.innerHTML = '';
        items.forEach(function (k) {
          list.appendChild(renderKriListItem(k));
        });
      })
      .catch(function () {
        list.innerHTML = '<div class="raid-ss-kri-empty">Failed to load key risk indicators.</div>';
      });
  }

  function renderKriListItem(k) {
    var card = document.createElement('div');
    card.className = 'raid-ss-kri-card';
    var desc = (k.description || '').trim();
    var descHtml = desc
      ? '<p class="govuk-body-s govuk-!-margin-top-2 govuk-!-margin-bottom-0 raid-ss-kri-notes">' + escapeHtml(desc) + '</p>'
      : '';
    card.innerHTML =
      '<div class="raid-ss-kri-card-head">' +
      '<h3 class="govuk-heading-s govuk-!-margin-bottom-0">' + escapeHtml(k.title || 'KRI') + '</h3>' +
      '</div>' +
      '<dl class="govuk-summary-list govuk-summary-list--no-border raid-ss-kri-meta">' +
      '<div class="govuk-summary-list__row"><dt class="govuk-summary-list__key">Metric</dt><dd class="govuk-summary-list__value">' + escapeHtml(k.metric || '—') + '</dd></div>' +
      '<div class="govuk-summary-list__row"><dt class="govuk-summary-list__key">Threshold</dt><dd class="govuk-summary-list__value">' + escapeHtml(k.threshold || '—') + '</dd></div>' +
      '</dl>' +
      descHtml +
      (readOnly ? '' :
        '<button type="button" class="govuk-link govuk-!-margin-top-2 govuk-!-margin-bottom-0" data-raid-kri-edit' +
        ' data-kri-id="' + k.id + '"' +
        ' data-kri-title="' + escapeAttr(k.title || '') + '"' +
        ' data-kri-description="' + escapeAttr(k.description || '') + '"' +
        ' data-kri-metric="' + escapeAttr(k.metric || '') + '"' +
        ' data-kri-threshold="' + escapeAttr(k.threshold || '') + '">Edit</button>');
    return card;
  }

  function openKriFormModal(mode, kri) {
    if (!kriContext) return;
    kriFormMode = mode;
    var modal = document.getElementById('raid-kri-form-modal');
    var titleEl = document.getElementById('raid-kri-form-modal-title');
    var titleInput = document.getElementById('raid-kri-form-title');
    var descInput = document.getElementById('raid-kri-form-description');
    var metricInput = document.getElementById('raid-kri-form-metric');
    var thresholdInput = document.getElementById('raid-kri-form-threshold');
    var removeBtn = document.getElementById('raid-kri-form-remove');
    var errEl = document.getElementById('raid-kri-form-error');
    if (!modal || !titleInput) return;

    if (titleEl) titleEl.textContent = mode === 'edit' ? 'Edit KRI' : 'Add KRI';
    titleInput.value = kri && kri.title ? kri.title : '';
    if (descInput) descInput.value = kri && kri.description ? kri.description : '';
    if (metricInput) metricInput.value = kri && kri.metric ? kri.metric : '';
    if (thresholdInput) thresholdInput.value = kri && kri.threshold ? kri.threshold : '';
    if (removeBtn) removeBtn.style.display = mode === 'edit' ? '' : 'none';
    if (errEl) { errEl.style.display = 'none'; errEl.textContent = ''; }
    modal.setAttribute('data-kri-id', kri && kri.id ? kri.id : '');
    modal.style.display = 'flex';
    document.body.classList.add('raid-modal-open');
    titleInput.focus();
  }

  function closeKriFormModal() {
    var modal = document.getElementById('raid-kri-form-modal');
    if (modal) modal.style.display = 'none';
    if (!isKrisModalOpen()) {
      document.body.classList.remove('raid-modal-open');
    }
  }

  function showKriFormError(msg) {
    var errEl = document.getElementById('raid-kri-form-error');
    if (!errEl) return;
    if (msg) {
      errEl.textContent = msg;
      errEl.style.display = 'block';
    } else {
      errEl.textContent = '';
      errEl.style.display = 'none';
    }
  }

  function saveKriForm() {
    if (!kriContext) return;
    var saveBtn = document.getElementById('raid-kri-form-save');
    var titleInput = document.getElementById('raid-kri-form-title');
    var descInput = document.getElementById('raid-kri-form-description');
    var metricInput = document.getElementById('raid-kri-form-metric');
    var thresholdInput = document.getElementById('raid-kri-form-threshold');
    var formModal = document.getElementById('raid-kri-form-modal');
    var riskId = kriContext.riskId;
    var isEdit = kriFormMode === 'edit';
    var kriId = formModal ? formModal.getAttribute('data-kri-id') : '';
    var payload = {
      title: titleInput ? titleInput.value : '',
      description: descInput ? descInput.value : '',
      metric: metricInput ? metricInput.value : '',
      threshold: thresholdInput ? thresholdInput.value : ''
    };

    var url = baseUrl + '/api/risk/' + riskId + '/kris';
    if (isEdit && kriId) url += '/' + kriId;

    if (saveBtn) { saveBtn.disabled = true; saveBtn.textContent = 'Saving…'; }
    fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'RequestVerificationToken': csrfToken
      },
      body: JSON.stringify(payload)
    })
      .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, data: d }; }); })
      .then(function (res) {
        if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save'; }
        var data = res.data || {};
        if (!res.ok) {
          showKriFormError(data.error || 'Save failed.');
          return;
        }
        closeKriFormModal();
        if (data.kriCount !== undefined) {
          setKriCountForRisk(riskId, data.kriCount, data.krisSummary || '');
        }
        var list = document.getElementById('raid-kris-modal-list');
        if (list && isKrisModalOpen()) {
          loadKrisIntoList(riskId, list);
        }
      })
      .catch(function (err) {
        if (saveBtn) { saveBtn.disabled = false; saveBtn.textContent = 'Save'; }
        showKriFormError(err && err.message ? err.message : 'Save failed.');
      });
  }

  function removeKriForm() {
    if (!kriContext || kriFormMode !== 'edit') return;
    var formModal = document.getElementById('raid-kri-form-modal');
    var kriId = formModal ? formModal.getAttribute('data-kri-id') : '';
    if (!kriId) return;
    if (!window.confirm('Remove this key risk indicator? This cannot be undone.')) return;

    var removeBtn = document.getElementById('raid-kri-form-remove');
    var riskId = kriContext.riskId;
    if (removeBtn) { removeBtn.disabled = true; }
    fetch(baseUrl + '/api/risk/' + riskId + '/kris/' + kriId + '/remove', {
      method: 'POST',
      headers: { 'RequestVerificationToken': csrfToken }
    })
      .then(function (r) { return r.json().then(function (d) { return { ok: r.ok, data: d }; }); })
      .then(function (res) {
        if (removeBtn) { removeBtn.disabled = false; }
        var data = res.data || {};
        if (!res.ok) {
          showKriFormError(data.error || 'Remove failed.');
          return;
        }
        closeKriFormModal();
        if (data.kriCount !== undefined) {
          setKriCountForRisk(riskId, data.kriCount, data.krisSummary || '');
        }
        var list = document.getElementById('raid-kris-modal-list');
        if (list && isKrisModalOpen()) {
          loadKrisIntoList(riskId, list);
        }
      })
      .catch(function (err) {
        if (removeBtn) { removeBtn.disabled = false; }
        showKriFormError(err && err.message ? err.message : 'Remove failed.');
      });
  }

  function escapeAttr(s) {
    return String(s)
      .replace(/&/g, '&amp;')
      .replace(/"/g, '&quot;')
      .replace(/</g, '&lt;');
  }

  // ── Public API ──
  window.RaidSpreadsheet = { init: init, clearFilters: clearFilters, resetTable: resetTableView };
})();
