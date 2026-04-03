/**
 * Azure Clean Room Governance UI - Main JavaScript
 * Handles navigation, responsive behavior, and UI interactions
 */

document.addEventListener("DOMContentLoaded", function () {
  // Initialize all components
  initializeSidebar();
  initializeTooltips();
  initializeLoadingStates();
  initializeAnimations();
  setActiveNavigation();

  console.log("Clean Room Governance UI initialized");
});

/**
 * Sidebar functionality for mobile and desktop
 */
function initializeSidebar() {
  const sidebar = document.querySelector(".sidebar");
  const mobileToggle = document.querySelector(".mobile-menu-toggle");
  const sidebarToggle = document.querySelector(".sidebar-toggle");
  const overlay = document.createElement("div");

  // Create mobile overlay
  overlay.className = "mobile-overlay";
  overlay.style.cssText = `
        position: fixed;
        top: 0;
        left: 0;
        width: 100%;
        height: 100%;
        background: rgba(0, 0, 0, 0.5);
        z-index: 999;
        display: none;
        opacity: 0;
        transition: opacity 0.3s ease;
    `;
  document.body.appendChild(overlay);

  // Mobile menu toggle
  if (mobileToggle && sidebar) {
    mobileToggle.addEventListener("click", function () {
      const isVisible = sidebar.classList.contains("show");

      if (isVisible) {
        hideSidebar();
      } else {
        showSidebar();
      }
    });
  }

  // Desktop sidebar toggle (collapse/expand)
  if (sidebarToggle && sidebar) {
    sidebarToggle.addEventListener("click", function () {
      sidebar.classList.toggle("collapsed");
      localStorage.setItem(
        "sidebarCollapsed",
        sidebar.classList.contains("collapsed")
      );
    });

    // Restore sidebar state
    const isCollapsed = localStorage.getItem("sidebarCollapsed") === "true";
    if (isCollapsed) {
      sidebar.classList.add("collapsed");
    }
  }

  // Close sidebar when clicking overlay
  overlay.addEventListener("click", hideSidebar);

  // Close sidebar on escape key
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && sidebar.classList.contains("show")) {
      hideSidebar();
    }
  });

  // Handle window resize
  window.addEventListener("resize", function () {
    if (window.innerWidth > 992) {
      hideSidebar();
    }
  });

  function showSidebar() {
    sidebar.classList.add("show");
    overlay.style.display = "block";
    setTimeout(() => (overlay.style.opacity = "1"), 10);
    document.body.style.overflow = "hidden";
  }

  function hideSidebar() {
    sidebar.classList.remove("show");
    overlay.style.opacity = "0";
    setTimeout(() => (overlay.style.display = "none"), 300);
    document.body.style.overflow = "";
  }
}

/**
 * Set active navigation item based on current page
 */
function setActiveNavigation() {
  const currentPath = window.location.pathname.toLowerCase();
  const navLinks = document.querySelectorAll(".sidebar-link");

  navLinks.forEach((link) => {
    link.classList.remove("active");

    const href = link.getAttribute("href");
    if (href && currentPath.includes(href.toLowerCase())) {
      link.classList.add("active");
    }
  });

  // Special case for home page
  if (currentPath === "/" || currentPath === "/home") {
    const homeLink =
      document.querySelector('.sidebar-link[href="/"]') ||
      document.querySelector('.sidebar-link[href="/Home"]');
    if (homeLink) {
      homeLink.classList.add("active");
    }
  }
}

/**
 * Initialize Bootstrap tooltips and popovers
 */
function initializeTooltips() {
  // Initialize tooltips
  const tooltipTriggerList = [].slice.call(
    document.querySelectorAll('[data-bs-toggle="tooltip"]')
  );
  tooltipTriggerList.map(function (tooltipTriggerEl) {
    return new bootstrap.Tooltip(tooltipTriggerEl);
  });

  // Initialize popovers
  const popoverTriggerList = [].slice.call(
    document.querySelectorAll('[data-bs-toggle="popover"]')
  );
  popoverTriggerList.map(function (popoverTriggerEl) {
    return new bootstrap.Popover(popoverTriggerEl);
  });
}

/**
 * Loading states for buttons and forms
 */
function initializeLoadingStates() {
  // Add loading state to buttons with data-loading attribute
  document.addEventListener("click", function (e) {
    const button = e.target.closest("[data-loading]");
    if (button && !button.disabled) {
      showButtonLoading(button);
    }
  });

  // Add loading state to forms
  document.addEventListener("submit", function (e) {
    const form = e.target;
    const submitButton = form.querySelector(
      'button[type="submit"], input[type="submit"]'
    );

    if (submitButton) {
      showButtonLoading(submitButton);
    }

    showLoadingOverlay();
  });
}

/**
 * Show loading state on button
 */
function showButtonLoading(button) {
  const originalText = button.innerHTML;
  const loadingText = button.dataset.loading || "Loading...";

  button.dataset.originalText = originalText;
  button.innerHTML = `<i class="fas fa-spinner fa-spin me-2"></i>${loadingText}`;
  button.disabled = true;

  // Auto-restore after 30 seconds (failsafe)
  setTimeout(() => {
    hideButtonLoading(button);
  }, 30000);
}

/**
 * Hide loading state on button
 */
function hideButtonLoading(button) {
  if (button.dataset.originalText) {
    button.innerHTML = button.dataset.originalText;
    button.disabled = false;
    delete button.dataset.originalText;
  }
}

/**
 * Show page loading overlay
 */
function showLoadingOverlay() {
  let overlay = document.querySelector(".loading-overlay");
  if (!overlay) {
    overlay = document.createElement("div");
    overlay.className = "loading-overlay";
    overlay.innerHTML = `
            <div class="loading-spinner">
                <div class="spinner"></div>
                <div>Processing request...</div>
            </div>
        `;
    document.body.appendChild(overlay);
  }
  overlay.style.display = "flex";
}

/**
 * Hide page loading overlay
 */
function hideLoadingOverlay() {
  const overlay = document.querySelector(".loading-overlay");
  if (overlay) {
    overlay.style.display = "none";
  }
}

/**
 * Initialize animations and smooth scrolling
 */
function initializeAnimations() {
  // Add fade-in animation to cards when they come into view
  const observerOptions = {
    threshold: 0.1,
    rootMargin: "0px 0px -50px 0px"
  };

  const observer = new IntersectionObserver(function (entries) {
    entries.forEach((entry) => {
      if (entry.isIntersecting) {
        entry.target.classList.add("fade-in");
        observer.unobserve(entry.target);
      }
    });
  }, observerOptions);

  // Observe all cards and stat cards
  document.querySelectorAll(".card, .stat-card").forEach((card) => {
    observer.observe(card);
  });

  // Smooth scrolling for anchor links
  document.querySelectorAll('a[href^="#"]').forEach((anchor) => {
    anchor.addEventListener("click", function (e) {
      e.preventDefault();
      const target = document.querySelector(this.getAttribute("href"));
      if (target) {
        target.scrollIntoView({
          behavior: "smooth",
          block: "start"
        });
      }
    });
  });
}

/**
 * Format numbers with appropriate suffixes
 */
function formatNumber(num) {
  if (num >= 1000000) {
    return (num / 1000000).toFixed(1) + "M";
  } else if (num >= 1000) {
    return (num / 1000).toFixed(1) + "K";
  }
  return num.toString();
}

/**
 * Format date in a user-friendly way
 */
function formatDate(dateString) {
  const date = new Date(dateString);
  const now = new Date();
  const diffTime = Math.abs(now - date);
  const diffDays = Math.ceil(diffTime / (1000 * 60 * 60 * 24));

  if (diffDays === 0) {
    return "Today";
  } else if (diffDays === 1) {
    return "Yesterday";
  } else if (diffDays < 7) {
    return `${diffDays} days ago`;
  } else {
    return date.toLocaleDateString();
  }
}

/**
 * Show toast notification
 */
function showToast(message, type = "info", duration = 5000) {
  const toast = document.createElement("div");
  toast.className = `toast align-items-center text-white bg-${type} border-0`;
  toast.setAttribute("role", "alert");
  toast.setAttribute("aria-live", "assertive");
  toast.setAttribute("aria-atomic", "true");

  toast.innerHTML = `
        <div class="d-flex">
            <div class="toast-body">
                ${message}
            </div>
            <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>
        </div>
    `;

  // Create toast container if it doesn't exist
  let container = document.querySelector(".toast-container");
  if (!container) {
    container = document.createElement("div");
    container.className = "toast-container position-fixed top-0 end-0 p-3";
    container.style.zIndex = "1055";
    document.body.appendChild(container);
  }

  container.appendChild(toast);

  const bsToast = new bootstrap.Toast(toast, { delay: duration });
  bsToast.show();

  // Remove toast element after it's hidden
  toast.addEventListener("hidden.bs.toast", () => {
    toast.remove();
  });
}

/**
 * Utility functions for common operations
 */
const Utils = {
  formatNumber,
  formatDate,
  showToast,
  showLoadingOverlay,
  hideLoadingOverlay,
  showButtonLoading,
  hideButtonLoading
};

// Expose utils globally for use in other scripts
window.CleanroomUI = Utils;
