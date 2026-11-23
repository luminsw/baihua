// 移动端侧边栏：导航后自动关闭
(function() {
    var observer = new MutationObserver(function() {
        var sidebar = document.querySelector('.sidebar');
        if (sidebar && sidebar.classList.contains('open') && window.innerWidth <= 768) {
            var links = sidebar.querySelectorAll('a[href]');
            links.forEach(function(link) {
                link.removeEventListener('click', closeSidebar);
                link.addEventListener('click', closeSidebar);
            });
        }
    });
    function closeSidebar() {
        var sidebar = document.querySelector('.sidebar');
        if (sidebar) sidebar.classList.remove('open');
    }
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function() {
            var sidebar = document.querySelector('.sidebar');
            if (sidebar) {
                sidebar.querySelectorAll('a[href]').forEach(function(link) {
                    link.addEventListener('click', closeSidebar);
                });
            }
            observer.observe(document.body, { childList: true, subtree: true });
        });
    } else {
        observer.observe(document.body, { childList: true, subtree: true });
    }
})();

// FAB 系统菜单 - 使用全局函数，onclick 属性直接调用，避免 Blazor 重新渲染丢失事件
function toggleFabMenu(e) {
    e.stopPropagation();
    var menu = document.getElementById('fab-menu');
    var btn = document.getElementById('fab-btn');
    if (!menu || !btn) return;
    var isOpen = menu.style.display !== 'none';
    menu.style.display = isOpen ? 'none' : 'block';
    btn.classList.toggle('active', !isOpen);
}

function closeFabMenu() {
    var menu = document.getElementById('fab-menu');
    var btn = document.getElementById('fab-btn');
    if (menu) menu.style.display = 'none';
    if (btn) btn.classList.remove('active');
}

// 点击页面其他区域关闭菜单
document.addEventListener('click', function(e) {
    var container = document.getElementById('fab-container');
    if (container && !container.contains(e.target)) {
        closeFabMenu();
    }
});

// 登出
function logoutAndRedirect() {
    fetch('/api/auth/logout', { method: 'POST' })
        .finally(function() { window.location.href = '/login'; });
}
