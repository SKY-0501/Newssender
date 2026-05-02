document.addEventListener('DOMContentLoaded', () => {
    const sidebarHTML = `
    <nav class="sidebar" id="mainSidebar">
      <div class="sidebar-header">
        <div class="sidebar-logo"></div>
        <span class="sidebar-brand">ECHOMAIL</span>
      </div>
      <div class="nav-links">
        <a href="dashboard.html" id="nav-dashboard">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M11 4H4a2 2 0 00-2 2v14a2 2 0 002 2h14a2 2 0 002-2v-7M18.5 2.5a2.121 2.121 0 113 3L12 15l-4 1 1-4 9.5-9.5z" stroke-linecap="round" stroke-linejoin="round" /></svg>
          <span>Sender Tool</span>
        </a>
        <a href="analytics.html" id="nav-analytics">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" stroke-linecap="round" stroke-linejoin="round" /></svg>
          <span>Tracking</span>
        </a>
        <a href="history.html" id="nav-history">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" stroke-linecap="round" stroke-linejoin="round" /></svg>
          <span>History</span>
        </a>
        <a href="subscribers.html" id="nav-subscribers">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z" stroke-linecap="round" stroke-linejoin="round" /></svg>
          <span>Subscribers</span>
        </a>
        <a href="queries.html" id="nav-queries">
          <svg fill="none" stroke="currentColor" viewBox="0 0 24 24"><path d="M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z" stroke-linecap="round" stroke-linejoin="round" /></svg>
          <span>Queries</span>
        </a>
      </div>
      <div class="sidebar-footer">
        <img src="https://mcusercontent.com/201e76b8f447ee4c8b7fbe53e/images/c2e0273b-0dab-d392-fe35-965293bee40c.png" alt="Orchvate Logo">
      </div>
    </nav>
    `;

    const placeholder = document.getElementById('sidebar-placeholder');
    if (placeholder) {
        placeholder.innerHTML = sidebarHTML;
        
        // Auto-set active link based on current filename
        const currentPath = window.location.pathname;
        const filename = currentPath.substring(currentPath.lastIndexOf('/') + 1);
        
        const links = {
            'dashboard.html': 'nav-dashboard',
            'analytics.html': 'nav-analytics',
            'history.html': 'nav-history',
            'subscribers.html': 'nav-subscribers',
            'queries.html': 'nav-queries'
        };

        const activeId = links[filename] || (filename === '' ? 'nav-dashboard' : null);
        if (activeId) {
            const activeLink = document.getElementById(activeId);
            if (activeLink) activeLink.classList.add('active');
        }
    }
});
