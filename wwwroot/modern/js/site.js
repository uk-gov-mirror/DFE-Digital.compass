//
// For guidance on how to add JavaScript see:
// https://prototype-kit.service.gov.uk/docs/adding-css-javascript-and-images
//

// Application Insights helper functions
function trackEvent(eventName, properties) {
  if (window.appInsights) {
    window.appInsights.trackEvent(eventName, properties);
  }
}

function trackPageView(pageName, url) {
  if (window.appInsights) {
    window.appInsights.trackPageView(pageName, url);
  }
}

// Track page load
document.addEventListener('DOMContentLoaded', function () {
  // Track page view
  trackPageView(document.title, window.location.href);

  // Track user interactions
  trackEvent('PageLoaded', {
    page: window.location.pathname,
    referrer: document.referrer,
    userAgent: navigator.userAgent,
    timestamp: new Date().toISOString()
  });
});

// Feedback form functionality
document.addEventListener('DOMContentLoaded', function () {
  const feedbackLink = document.getElementById('feedback-link');
  const feedbackPanel = document.getElementById('feedback-panel');
  const thanksMessage = document.getElementById('thanksMessage');
  const feedbackForm = document.getElementById('feedback-form');
  const cancelButton = document.getElementById('cancelButton');

  if (feedbackLink && feedbackPanel && thanksMessage && feedbackForm && cancelButton) {
    // Show feedback panel when link is clicked
    feedbackLink.addEventListener('click', function (e) {
      e.preventDefault();
      feedbackPanel.classList.add('show');
      feedbackPanel.setAttribute('aria-hidden', 'false');
      thanksMessage.classList.remove('show');

      // Clear any validation errors when opening the panel
      const formGroup = document.getElementById('feedback_form_group');
      const errorSummary = document.getElementById('feedback-error-summary');
      const errorMessage = document.getElementById('feedback_form_input-error');
      const textarea = document.getElementById('feedback_form_input');

      if (formGroup && errorSummary && errorMessage && textarea) {
        formGroup.classList.remove('dfe-c-form__group--error');
        textarea.classList.remove('dfe-c-form__input--error');
        errorSummary.style.display = 'none';
        errorMessage.style.display = 'none';

        // Reset aria-describedby
        textarea.setAttribute('aria-describedby', 'feedback_form_input-info');
      }

      // Track feedback panel opened
      trackEvent('FeedbackPanelOpened', {
        page: window.location.pathname,
        timestamp: new Date().toISOString()
      });

      // Focus on the textarea
      if (textarea) {
        textarea.focus();
      }
    });

    // Hide feedback panel when cancel button is clicked
    cancelButton.addEventListener('click', function (e) {
      e.preventDefault();
      feedbackPanel.classList.remove('show');
      feedbackPanel.setAttribute('aria-hidden', 'true');

      // Clear any validation errors
      const formGroup = document.getElementById('feedback_form_group');
      const errorSummary = document.getElementById('feedback-error-summary');
      const errorMessage = document.getElementById('feedback_form_input-error');
      const textarea = document.getElementById('feedback_form_input');

      if (formGroup && errorSummary && errorMessage && textarea) {
        formGroup.classList.remove('dfe-c-form__group--error');
        textarea.classList.remove('dfe-c-form__input--error');
        errorSummary.style.display = 'none';
        errorMessage.style.display = 'none';

        // Reset aria-describedby
        textarea.setAttribute('aria-describedby', 'feedback_form_input-info');
      }

      // Track feedback panel cancelled
      trackEvent('FeedbackPanelCancelled', {
        page: window.location.pathname,
        timestamp: new Date().toISOString()
      });
    });

    // Function to show validation errors
    function showFeedbackError() {
      const formGroup = document.getElementById('feedback_form_group');
      const errorSummary = document.getElementById('feedback-error-summary');
      const errorMessage = document.getElementById('feedback_form_input-error');
      const textarea = document.getElementById('feedback_form_input');

      if (formGroup && errorSummary && errorMessage && textarea) {
        formGroup.classList.add('dfe-c-form__group--error');
        textarea.classList.add('dfe-c-form__input--error');
        errorSummary.style.display = 'block';
        errorMessage.style.display = 'block';

        // Update aria-describedby to include error message
        const currentDescribedBy = textarea.getAttribute('aria-describedby') || '';
        if (!currentDescribedBy.includes('feedback_form_input-error')) {
          textarea.setAttribute('aria-describedby', 'feedback_form_input-error ' + currentDescribedBy);
        }

        // Focus on error summary for screen readers
        errorSummary.focus();
      }
    }

    // Function to hide validation errors
    function hideFeedbackError() {
      const formGroup = document.getElementById('feedback_form_group');
      const errorSummary = document.getElementById('feedback-error-summary');
      const errorMessage = document.getElementById('feedback_form_input-error');
      const textarea = document.getElementById('feedback_form_input');

      if (formGroup && errorSummary && errorMessage && textarea) {
        formGroup.classList.remove('dfe-c-form__group--error');
        textarea.classList.remove('dfe-c-form__input--error');
        errorSummary.style.display = 'none';
        errorMessage.style.display = 'none';

        // Update aria-describedby to remove error message
        const currentDescribedBy = textarea.getAttribute('aria-describedby') || '';
        textarea.setAttribute('aria-describedby', currentDescribedBy.replace('feedback_form_input-error', '').trim());
      }
    }

    // Clear errors when user starts typing and is under limit
    const textarea = feedbackForm.querySelector('textarea');
    if (textarea) {
      textarea.addEventListener('input', function () {
        if (this.value.length <= 1000) {
          hideFeedbackError();
        }
      });
    }

    // Handle form submission
    feedbackForm.addEventListener('submit', function (e) {
      e.preventDefault();

      const textarea = feedbackForm.querySelector('textarea');
      const feedbackText = textarea.value.trim();

      // Validate character count
      if (feedbackText.length > 1000) {
        showFeedbackError();

        // Track validation error
        trackEvent('FeedbackValidationError', {
          page: window.location.pathname,
          characterCount: feedbackText.length,
          timestamp: new Date().toISOString()
        });

        return;
      }

      // Clear any existing errors
      hideFeedbackError();

      if (feedbackText) {
        // Track feedback submission
        trackEvent('FeedbackSubmitted', {
          page: window.location.pathname,
          feedbackLength: feedbackText.length,
          timestamp: new Date().toISOString()
        });

        // Send feedback to the server
        const submitButton = feedbackForm.querySelector('button[type="submit"]');
        const originalButtonText = submitButton.textContent;
        submitButton.textContent = 'Submitting...';
        submitButton.disabled = true;

        fetch('/Contact/SubmitFeedback', {
          method: 'POST',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({
            feedbackFormInput: feedbackText
          })
        })
          .then(response => response.json())
          .then(data => {
            if (data.success) {
              // Show success message
              feedbackPanel.classList.remove('show');
              feedbackPanel.setAttribute('aria-hidden', 'true');
              thanksMessage.classList.add('show');

              // Clear the form and errors
              textarea.value = '';
              hideFeedbackError();

              // Focus on the thank you message for screen readers
              thanksMessage.focus();

              console.log('Feedback submitted successfully');
            } else {
              // Show error message
              alert('Sorry, there was an error submitting your feedback. Please try again.');
              console.error('Feedback submission failed:', data.message);
            }
          })
          .catch(error => {
            // Show error message
            alert('Sorry, there was an error submitting your feedback. Please try again.');
            console.error('Feedback submission error:', error);
          })
          .finally(() => {
            // Reset button state
            submitButton.textContent = originalButtonText;
            submitButton.disabled = false;
          });
      }
    });
  }
});

// Header search: icon + "Search" toggle, expandable panel, close button (GOV.UK style)
document.addEventListener('DOMContentLoaded', function () {
  const header = document.querySelector('.govuk-js-header-search');
  const toggle = document.querySelector('.govuk-js-header-search-toggle');
  const closeBtn = document.querySelector('.govuk-js-header-search-close');
  const panel = document.getElementById('header-search-panel');
  const input = document.getElementById('header-search-input');

  if (!header || !toggle || !panel) return;

  function openSearch() {
    panel.hidden = false;
    panel.setAttribute('aria-hidden', 'false');
    toggle.setAttribute('aria-expanded', 'true');
    if (input) {
      input.focus();
    }
  }

  function closeSearch() {
    panel.hidden = true;
    panel.setAttribute('aria-hidden', 'true');
    toggle.setAttribute('aria-expanded', 'false');
    toggle.focus();
  }

  function isOpen() {
    return toggle.getAttribute('aria-expanded') === 'true';
  }

  toggle.addEventListener('click', function () {
    if (isOpen()) {
      closeSearch();
    } else {
      openSearch();
    }
  });

  if (closeBtn) {
    closeBtn.addEventListener('click', closeSearch);
  }

  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape' && isOpen()) {
      closeSearch();
    }
  });

  document.addEventListener('click', function (e) {
    if (!isOpen()) return;
    if (!header.contains(e.target)) {
      closeSearch();
    }
  });
});

// Track search interactions
document.addEventListener('DOMContentLoaded', function () {
  const searchForms = document.querySelectorAll('form[action*="search"], form[action*="Search"]');
  searchForms.forEach(function (form) {
    form.addEventListener('submit', function (e) {
      const searchInput = form.querySelector('input[type="search"], input[name*="search"], input[name*="Search"]');
      if (searchInput && searchInput.value.trim()) {
        trackEvent('SearchPerformed', {
          searchTerm: searchInput.value.trim(),
          page: window.location.pathname,
          timestamp: new Date().toISOString()
        });
      }
    });
  });
});

// Track product clicks
document.addEventListener('DOMContentLoaded', function () {
  const productLinks = document.querySelectorAll('a[href*="/products/"], a[href*="/product/"]');
  productLinks.forEach(function (link) {
    link.addEventListener('click', function (e) {
      trackEvent('ProductClicked', {
        productUrl: link.href,
        productText: link.textContent.trim(),
        page: window.location.pathname,
        timestamp: new Date().toISOString()
      });
    });
  });
});

// Track category clicks
document.addEventListener('DOMContentLoaded', function () {
  const categoryLinks = document.querySelectorAll('a[href*="/categories/"], a[href*="/category/"]');
  categoryLinks.forEach(function (link) {
    link.addEventListener('click', function (e) {
      trackEvent('CategoryClicked', {
        categoryUrl: link.href,
        categoryText: link.textContent.trim(),
        page: window.location.pathname,
        timestamp: new Date().toISOString()
      });
    });
  });
});

// Compass sub-navigation: show sub-nav when a main nav item is selected
(function () {
  var SUBNAV_MAP = {
    'demand': 'subnav-demand',
    'work': 'subnav-work',
    'raid': 'subnav-raid',
    'products-services': 'subnav-products-services',
    'performance': 'subnav-performance',
    'manage': 'subnav-manage',
    'operations': 'subnav-operations',
    'risks': 'subnav-risks',
    'standards': 'subnav-standards',
    'reporting': 'subnav-reporting',
    'admin': 'subnav-admin',
    'planning': 'subnav-planning',
    'work-reporting': 'subnav-work-reporting',
    'issues': 'subnav-issues'
  };

  function showSubnav(group) {
    document.querySelectorAll('.dfe-f-sub-navigation.dfe-c-sub-nav').forEach(function (n) {
      n.classList.remove('dfe-c-sub-nav--visible');
    });
    var subId = SUBNAV_MAP[group];
    if (subId) {
      var subEl = document.getElementById(subId);
      if (subEl) subEl.classList.add('dfe-c-sub-nav--visible');
    }
    document.querySelectorAll('.dfe-f-header__service-nav a[data-nav]').forEach(function (a) {
      var match = a.getAttribute('data-nav') === group;
      if (match) {
        a.setAttribute('aria-current', 'page');
      } else {
        a.removeAttribute('aria-current');
      }
    });
  }

  function setSubNavActive(subId) {
    if (!subId) return;
    document.querySelectorAll('.dfe-f-sub-navigation__link[data-sub]').forEach(function (a) {
      var dataSub = (a.getAttribute('data-sub') || '').trim();
      var ids = dataSub ? dataSub.split(/\s+/).filter(Boolean) : [];
      var match = ids.indexOf(subId) !== -1;
      if (match) {
        a.setAttribute('aria-current', 'page');
      } else {
        a.removeAttribute('aria-current');
      }
    });
  }

  document.addEventListener('DOMContentLoaded', function () {
    var body = document.body;
    var mainNav = (body.getAttribute('data-main-nav') || '').trim();
    var subNav = (body.getAttribute('data-sub-nav') || '').trim();
    if (mainNav) showSubnav(mainNav);
    if (subNav) setSubNavActive(subNav);

    var mainNavList = document.getElementById('main-nav');
    if (!mainNavList) return;
    mainNavList.addEventListener('click', function (e) {
      var link = e.target && e.target.closest && e.target.closest('a[data-nav]');
      if (!link) return;
      var group = link.getAttribute('data-nav');
      if (!group) return;
      if (link.getAttribute('href') === '#') {
        e.preventDefault();
        showSubnav(group);
      }
    });
  });
})();

// Products & services: filter tables as the user types (tables expose data-live-search-input / data-live-search-count).
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('table[data-live-search-input]').forEach(function (table) {
    var inputId = table.getAttribute('data-live-search-input');
    var countId = table.getAttribute('data-live-search-count');
    if (!inputId) return;
    var input = document.getElementById(inputId);
    if (!input) return;
    var tbody = table.querySelector('tbody');
    if (!tbody) return;
    var rows = tbody.querySelectorAll('tr');
    var countIds = (countId || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean);
    var fmt = table.getAttribute('data-live-search-format');

    function applyFilter() {
      var q = (input.value || '').trim().toLowerCase().replace(/\s+/g, ' ');
      var n = 0;
      for (var i = 0; i < rows.length; i++) {
        var tr = rows[i];
        var extra = tr.getAttribute('data-products-search-extras') || '';
        var text = ((tr.textContent || '') + ' ' + extra).replace(/\s+/g, ' ').trim().toLowerCase();
        var show = !q || text.indexOf(q) !== -1;
        tr.hidden = !show;
        if (show) n++;
      }
      var totalRows = rows.length;
      var msg;
      if (fmt === 'showing') {
        msg = 'Showing ' + n + ' of ' + totalRows;
      } else {
        msg = n + ' product(s) shown.';
      }
      for (var j = 0; j < countIds.length; j++) {
        var el = document.getElementById(countIds[j]);
        if (el) el.textContent = msg;
      }
    }

    input.addEventListener('input', applyFilter);
    input.addEventListener('search', applyFilter);
    applyFilter();
  });
});

// Track errors
window.addEventListener('error', function (e) {
  trackEvent('JavaScriptError', {
    errorMessage: e.message,
    errorSource: e.filename,
    errorLine: e.lineno,
    errorColumn: e.colno,
    page: window.location.pathname,
    timestamp: new Date().toISOString()
  });
});

// Track unhandled promise rejections
window.addEventListener('unhandledrejection', function (e) {
  trackEvent('UnhandledPromiseRejection', {
    errorMessage: e.reason ? e.reason.toString() : 'Unknown error',
    page: window.location.pathname,
    timestamp: new Date().toISOString()
  });
});



// Header Navigation and Search Functionality
document.addEventListener('DOMContentLoaded', function () {
  const navigationToggle = document.getElementById('super-navigation-menu-toggle');
  const searchToggle = document.getElementById('super-search-menu-toggle');
  const navigationMenu = document.getElementById('super-navigation-menu');
  const searchMenu = document.getElementById('super-search-menu');
  const searchForm = document.getElementById('search');

  // Function to close all menus and reset button states
  function closeAllMenus() {
    // Close navigation
    if (navigationToggle && navigationMenu) {
      navigationToggle.setAttribute('aria-expanded', 'false');
      navigationMenu.setAttribute('hidden', 'hidden');
      navigationToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
    }

    // Close search
    if (searchToggle && searchMenu) {
      searchToggle.setAttribute('aria-expanded', 'false');
      searchMenu.setAttribute('hidden', 'hidden');
      searchToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
    }
  }

  // Toggle navigation menu
  if (navigationToggle && navigationMenu) {
    navigationToggle.addEventListener('click', function () {
      const isExpanded = navigationToggle.getAttribute('aria-expanded') === 'true';
      const isHidden = navigationMenu.hasAttribute('hidden');

      if (isHidden) {
        // Open navigation menu
        navigationToggle.setAttribute('aria-expanded', 'true');
        navigationMenu.removeAttribute('hidden');
        navigationToggle.classList.add('gem-c-layout-super-navigation-header__open-button');

        // Close search if open
        if (searchToggle && searchMenu) {
          searchToggle.setAttribute('aria-expanded', 'false');
          searchMenu.setAttribute('hidden', 'hidden');
          searchToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
        }
      } else {
        // Close navigation menu
        navigationToggle.setAttribute('aria-expanded', 'false');
        navigationMenu.setAttribute('hidden', 'hidden');
        navigationToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
      }
    });
  }

  // Toggle search panel
  if (searchToggle && searchMenu) {
    searchToggle.addEventListener('click', function () {
      const isExpanded = searchToggle.getAttribute('aria-expanded') === 'true';
      const isHidden = searchMenu.hasAttribute('hidden');

      if (isHidden) {
        // Open search menu
        searchToggle.setAttribute('aria-expanded', 'true');
        searchMenu.removeAttribute('hidden');
        searchToggle.classList.add('gem-c-layout-super-navigation-header__open-button');

        // Close navigation if open
        if (navigationToggle && navigationMenu) {
          navigationToggle.setAttribute('aria-expanded', 'false');
          navigationMenu.setAttribute('hidden', 'hidden');
          navigationToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
        }
      } else {
        // Close search menu
        searchToggle.setAttribute('aria-expanded', 'false');
        searchMenu.setAttribute('hidden', 'hidden');
        searchToggle.classList.remove('gem-c-layout-super-navigation-header__open-button');
      }
    });
  }

  // Header search form submits normally to /search/all?keywords=...

  // Close panels on escape key
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Escape') {
      closeAllMenus();
    }
  });
});

// Work list filter panel toggle (used by Index, MyWork, Watching, ByPriority, Flagship)
window.toggleFilterPanel = function (btnEl, panelId) {
  var panel = document.getElementById(panelId);
  if (!panel) return;
  var isOpen = panel.classList.contains('open');
  panel.classList.toggle('open', !isOpen);
  btnEl.classList.toggle('active', !isOpen);
  btnEl.setAttribute('aria-expanded', !isOpen);
  var label = btnEl.querySelector('.filter-toggle-label');
  if (label) label.textContent = isOpen ? 'Filter' : 'Hide filters';
};

// Filter toolbar: open/close panel via aria-controls (no inline onclick; Index uses <summary> for native <details> instead)
document.addEventListener('click', function (e) {
  var btn = e.target.closest('.filter-toggle-btn, .dfe-c-search-filter__toggle');
  if (!btn || btn.tagName === 'SUMMARY') return;
  var panelId = btn.getAttribute('aria-controls');
  if (!panelId || !window.toggleFilterPanel) return;
  e.preventDefault();
  toggleFilterPanel(btn, panelId);
});

// Live filter for data-table rows when user types in .filter-search (Demand, Risks, Issues list views)
document.addEventListener('DOMContentLoaded', function () {
  document.querySelectorAll('.filter-search').forEach(function (searchEl) {
    var form = searchEl.closest('form');
    var scope = (form && form.closest('.dfe-c-page')) || form || document.body;
    var table = scope.querySelector('.data-table');
    if (!table) return;
    var rows = table.querySelectorAll('tbody tr[data-searchable]');
    if (!rows.length) return;
    function filterRows() {
      var q = (searchEl.value || '').trim().toLowerCase();
      rows.forEach(function (tr) {
        var text = (tr.getAttribute('data-searchable') || tr.textContent || '').toLowerCase();
        tr.style.display = (q === '' || text.indexOf(q) !== -1) ? '' : 'none';
      });
    }
    searchEl.addEventListener('input', filterRows);
    searchEl.addEventListener('keyup', filterRows);
    if ((searchEl.value || '').trim()) filterRows();
  });
});

// Guide/Collection meta: expand/collapse audience "+N more"
document.addEventListener('DOMContentLoaded', function () {
  document.body.addEventListener('click', function (e) {
    var btn = e.target && e.target.closest && e.target.closest('.guide-meta__audience-toggle');
    if (!btn) return;
    e.preventDefault();
    var wrapper = btn.closest('.guide-meta__audience');
    if (!wrapper) return;
    var extra = wrapper.querySelector('.guide-meta__audience-extra');
    var extraCount = btn.getAttribute('data-extra-count') || '';
    var isExpanded = wrapper.classList.contains('guide-meta__audience--expanded');
    if (isExpanded) {
      wrapper.classList.remove('guide-meta__audience--expanded');
      btn.textContent = '+' + extraCount + ' more';
      btn.setAttribute('aria-expanded', 'false');
      if (extra) extra.setAttribute('aria-hidden', 'true');
    } else {
      wrapper.classList.add('guide-meta__audience--expanded');
      btn.textContent = 'Show less';
      btn.setAttribute('aria-expanded', 'true');
      if (extra) extra.setAttribute('aria-hidden', 'false');
    }
  });
});

// TempData success/error banner: Dismiss button hides the banner for this page view
document.addEventListener('DOMContentLoaded', function () {
  document.body.addEventListener('click', function (e) {
    var btn = e.target && e.target.closest && e.target.closest('.js-dismiss-tempdata-banner');
    if (!btn) return;
    e.preventDefault();
    var banner = btn.closest('.js-tempdata-banner');
    if (banner) banner.remove();
  });
});

// Pipeline view section: when present, one toggle for whole "Pipeline view" section (Demand register, Pipeline page)
document.addEventListener('DOMContentLoaded', function () {
  var section = document.getElementById('pipeline-view-section');
  var sectionHeader = document.getElementById('pipeline-view-section-header');
  var sectionBody = document.getElementById('pipeline-view-section-body');
  if (section && sectionHeader && sectionBody) {
    var storageKey = section.getAttribute('data-storage-key') || 'pipeline-tracker-expanded';
    var isExpanded = function () { return localStorage.getItem(storageKey) !== 'false'; };
    var applyState = function () {
      var open = isExpanded();
      if (open) {
        section.classList.remove('collapsed');
        sectionHeader.setAttribute('aria-expanded', 'true');
      } else {
        section.classList.add('collapsed');
        sectionHeader.setAttribute('aria-expanded', 'false');
      }
    };
    applyState();
    sectionHeader.addEventListener('click', function () {
      var open = isExpanded();
      localStorage.setItem(storageKey, (!open).toString());
      applyState();
    });
    sectionHeader.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        sectionHeader.click();
      }
    });
    return;
  }

  // Pipeline tracker (standalone): collapsible, state in localStorage (default open)
  var tracker = document.getElementById('pipeline-tracker');
  var header = document.getElementById('pipeline-tracker-header');
  var body = document.getElementById('pipeline-tracker-body');
  if (tracker && header && body) {
    var storageKey = tracker.getAttribute('data-storage-key') || 'pipeline-tracker-expanded';
    var isExpanded = function () { return localStorage.getItem(storageKey) !== 'false'; };
    var applyState = function () {
      var open = isExpanded();
      if (open) {
        tracker.classList.remove('collapsed');
        header.setAttribute('aria-expanded', 'true');
      } else {
        tracker.classList.add('collapsed');
        header.setAttribute('aria-expanded', 'false');
      }
    };
    applyState();
    header.addEventListener('click', function () {
      var open = isExpanded();
      localStorage.setItem(storageKey, (!open).toString());
      applyState();
    });
    header.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        header.click();
      }
    });
  }
});

// Modern work item detail: tab panels + URL hash (tabs are <a href="...#wd-…">; hashchange alone is unreliable in some cases)
(function () {
  var WD_ALL_PANES = ['wd-overview', 'wd-weekly-updates', 'wd-updates', 'wd-risks', 'wd-issues', 'wd-milestones', 'wd-contacts', 'wd-service-register', 'wd-strategic-alignment', 'wd-dependencies', 'wd-assumptions', 'wd-history'];

  function isWorkDetailPage() {
    return !!document.querySelector('.wd-work-detail-tabs');
  }

  /** Click targets can be a Text node (no .closest); resolve to an Element. */
  function wdEventElement(ev) {
    var t = ev && ev.target;
    if (!t) return null;
    if (t.nodeType === 3) t = t.parentElement;
    return t;
  }

  var wdMoreOutsideClose = null;
  var wdMoreScrollClose = null;
  var wdMoreResizeReposition = null;

  /** Place “More” dropdown under the button, right-aligned to the button (last tab), clamped to the viewport. */
  function positionWdMoreMenu() {
    var btn = document.getElementById('wd-more-btn');
    var menu = document.getElementById('wd-more-menu');
    if (!btn || !menu) return;
    var r = btn.getBoundingClientRect();
    var gap = 4;
    var pad = 8;
    var vw = window.innerWidth;
    var vh = window.innerHeight;

    menu.style.position = 'fixed';
    menu.style.right = 'auto';
    menu.style.bottom = 'auto';
    menu.style.zIndex = '10050';
    menu.style.minWidth = Math.max(200, r.width) + 'px';

    var mw = menu.offsetWidth || Math.max(200, r.width);
    var mh = menu.offsetHeight || 0;

    // Right-align panel to trigger (matches last-tab dropdown UX; avoids hanging off the right edge).
    var left = r.right - mw;
    if (left < pad) left = pad;
    if (left + mw > vw - pad) left = Math.max(pad, vw - mw - pad);

    var top = r.bottom + gap;
    if (mh > 0 && top + mh > vh - pad && r.top > mh + gap) {
      top = r.top - mh - gap;
    }
    if (top < pad) top = pad;

    menu.style.left = left + 'px';
    menu.style.top = top + 'px';
  }

  function clearWdMoreListeners() {
    if (wdMoreOutsideClose) {
      document.removeEventListener('click', wdMoreOutsideClose);
      wdMoreOutsideClose = null;
    }
    if (wdMoreScrollClose) {
      window.removeEventListener('scroll', wdMoreScrollClose, true);
      wdMoreScrollClose = null;
    }
    if (wdMoreResizeReposition) {
      window.removeEventListener('resize', wdMoreResizeReposition);
      wdMoreResizeReposition = null;
    }
  }

  function setWdMoreOpen(open) {
    var menu = document.getElementById('wd-more-menu');
    var btn = document.getElementById('wd-more-btn');
    if (!menu) return;
    clearWdMoreListeners();
    if (open) {
      menu.style.display = 'block';
      menu.classList.add('wd-more-menu--open');
      positionWdMoreMenu();
      requestAnimationFrame(function () {
        positionWdMoreMenu();
      });
      if (btn) {
        btn.setAttribute('aria-expanded', 'true');
      }
      wdMoreOutsideClose = function (ev) {
        var wrap = document.getElementById('wd-more-wrap');
        if (wrap && wrap.contains(ev.target)) return;
        setWdMoreOpen(false);
      };
      wdMoreScrollClose = function () {
        setWdMoreOpen(false);
      };
      wdMoreResizeReposition = function () {
        positionWdMoreMenu();
      };
      setTimeout(function () {
        document.addEventListener('click', wdMoreOutsideClose);
      }, 0);
      window.addEventListener('scroll', wdMoreScrollClose, true);
      window.addEventListener('resize', wdMoreResizeReposition);
    } else {
      menu.style.display = 'none';
      menu.classList.remove('wd-more-menu--open');
      menu.style.position = '';
      menu.style.left = '';
      menu.style.top = '';
      menu.style.right = '';
      menu.style.bottom = '';
      menu.style.zIndex = '';
      menu.style.minWidth = '';
      if (btn) {
        btn.setAttribute('aria-expanded', 'false');
      }
    }
  }

  window.toggleWdMore = function (e) {
    if (e) {
      e.preventDefault();
      e.stopPropagation();
    }
    var menu = document.getElementById('wd-more-menu');
    if (!menu) return;
    var isOpen = menu.style.display === 'block' || menu.classList.contains('wd-more-menu--open');
    setWdMoreOpen(!isOpen);
  };

  document.addEventListener(
    'click',
    function (e) {
      var el = wdEventElement(e);
      var btn = el && el.closest && el.closest('#wd-more-btn');
      if (!btn || !isWorkDetailPage()) return;
      e.preventDefault();
      e.stopPropagation();
      window.toggleWdMore(e);
    },
    true
  );

  function syncWorkDetailTab(panelId) {
    if (!isWorkDetailPage() || WD_ALL_PANES.indexOf(panelId) < 0) return;
    var pane = document.getElementById(panelId);
    if (!pane) return;

    WD_ALL_PANES.forEach(function (id) {
      var p = document.getElementById(id);
      if (p) p.classList.remove('on');
    });
    pane.classList.add('on');

    var nav = document.querySelector('.wd-work-detail-tabs');
    if (nav) {
      nav.querySelectorAll('.dfe-f-side-navigation__item--current').forEach(function (li) {
        li.classList.remove('dfe-f-side-navigation__item--current');
      });
      nav.querySelectorAll('.dfe-f-sub-navigation__item--active').forEach(function (li) {
        li.classList.remove('dfe-f-sub-navigation__item--active');
      });
    }

    var suffix = panelId.replace('wd-', '');
    var tabLink = document.getElementById('wd-tab-' + suffix);
    if (tabLink && nav) {
      var li = tabLink.closest('.dfe-f-side-navigation__item') || tabLink.closest('.dfe-f-sub-navigation__item');
      if (li) {
        if (li.classList.contains('dfe-f-side-navigation__item')) {
          li.classList.add('dfe-f-side-navigation__item--current');
        } else {
          li.classList.add('dfe-f-sub-navigation__item--active');
        }
      }
    }

    var moreBtn = document.getElementById('wd-more-btn');
    if (moreBtn) moreBtn.classList.remove('on');
    var moreLabel = document.getElementById('wd-more-active-label');
    if (moreLabel) moreLabel.style.display = 'none';

    setWdMoreOpen(false);
  }

  function applyHashFromLocation() {
    if (!isWorkDetailPage()) return;
    var raw = (window.location.hash || '').replace(/^#/, '').trim();
    if (raw === 'wd-governance') {
      raw = 'wd-strategic-alignment';
      try {
        history.replaceState(null, '', '#' + raw);
      } catch (e) { /* ignore */ }
    }
    var panelId = raw && WD_ALL_PANES.indexOf(raw) >= 0 ? raw : 'wd-overview';
    syncWorkDetailTab(panelId);
  }

  window.wdTab = function (_el, panelId) {
    if (WD_ALL_PANES.indexOf(panelId) < 0) return;
    if (window.location.hash !== '#' + panelId) {
      window.location.hash = panelId;
    } else {
      syncWorkDetailTab(panelId);
    }
  };

  window.addEventListener('hashchange', applyHashFromLocation);
  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', applyHashFromLocation);
  } else {
    applyHashFromLocation();
  }

  document.addEventListener('click', function (e) {
    var el = wdEventElement(e);
    var a = el && el.closest && el.closest('a[href]');
    // Otherwise a tab link (…#wd-risks) above in the tree could steal clicks meant for real navigations.
    var a = el && el.closest && el.closest('a[href]');
    if (!a || !isWorkDetailPage()) return;

    var hrefAttr = a.getAttribute('href');
    if (!hrefAttr || hrefAttr.indexOf('#') < 0) return;

    var hashIdx = hrefAttr.indexOf('#');
    var frag = hrefAttr.slice(hashIdx + 1).split('&')[0].trim();
    if (WD_ALL_PANES.indexOf(frag) < 0) return;

    var pathMatches = true;
    try {
      var resolved = new URL(a.href, window.location.href);
      pathMatches = resolved.pathname === window.location.pathname;
    } catch (err) {
      pathMatches = false;
    }
    if (!pathMatches) return;

    e.preventDefault();
    if (window.location.hash !== '#' + frag) {
      window.location.hash = frag;
    } else {
      syncWorkDetailTab(frag);
    }
  }, false);
})();

// Demand request detail: scroll to pipeline actions (native #fragment scroll is flaky with sticky sidebars / unchanged hash)
document.addEventListener(
  'click',
  function (e) {
    var a = e.target.closest && e.target.closest('#sc-demand-request a[href="#demand-pipeline-actions"]');
    if (!a) return;
    var el = document.getElementById('demand-pipeline-actions');
    if (!el) return;
    e.preventDefault();
    el.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    try {
      history.replaceState(
        null,
        '',
        window.location.pathname + window.location.search + '#demand-pipeline-actions'
      );
    } catch (err) {
      window.location.hash = 'demand-pipeline-actions';
    }
    try {
      el.focus({ preventScroll: true });
    } catch (err2) {
      try {
        el.focus();
      } catch (err3) { /* ignore */ }
    }
  },
  false
);

/* Admin hub — illustrative feature-flag toggles (panel=flags); client-side only, not persisted. */
document.addEventListener('click', function (e) {
  var wrap = e.target.closest && e.target.closest('#panel-flags .flag-row__toggle');
  if (!wrap) return;
  var btn = wrap.querySelector('.flag-toggle');
  var label = wrap.querySelector('.flag-toggle__label');
  if (!btn || !label) return;
  e.preventDefault();
  var turningOn = !btn.classList.contains('flag-toggle--on');
  btn.classList.toggle('flag-toggle--on', turningOn);
  btn.classList.toggle('flag-toggle--off', !turningOn);
  label.textContent = turningOn ? 'ON' : 'OFF';
  label.classList.toggle('flag-on-label', turningOn);
  label.classList.toggle('flag-off-label', !turningOn);
  btn.setAttribute('aria-pressed', turningOn ? 'true' : 'false');
  var row = wrap.closest('.flag-row');
  var nameEl = row && row.querySelector('.flag-row__name');
  var name = nameEl ? nameEl.textContent.trim() : 'Feature';
  btn.setAttribute('aria-label', name + ': ' + (turningOn ? 'on' : 'off'));
}, false);

document.addEventListener('click', function (e) {
  var openBtn = e.target.closest('[data-dialog-open]');
  if (openBtn) {
    var dlg = document.getElementById(openBtn.getAttribute('data-dialog-open'));
    if (dlg && dlg.showModal) dlg.showModal();
    return;
  }
  var closeBtn = e.target.closest('[data-dialog-close]');
  if (closeBtn) {
    var dlg = document.getElementById(closeBtn.getAttribute('data-dialog-close'));
    if (dlg && dlg.close) dlg.close();
  }
});

document.addEventListener('click', function (e) {
  var btn = e.target.closest('[data-confirm]');
  if (btn) {
    var msg = btn.getAttribute('data-confirm');
    if (msg && !confirm(msg)) {
      e.preventDefault();
      e.stopImmediatePropagation();
    }
  }
});

document.addEventListener('change', function (e) {
  var sel = e.target.closest('#syn-group');
  if (!sel) return;
  var form = sel.closest('form[data-synonym-base-url]');
  if (!form) return;
  var base = form.getAttribute('data-synonym-base-url');
  form.action = base.replace('/0/', '/' + sel.value + '/');
});
