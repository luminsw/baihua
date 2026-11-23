window.scrollIntoView = function (element) {
    if (element && element.scrollIntoView) {
        element.scrollIntoView({ behavior: 'smooth', block: 'center' });
    }
};

window.getTheme = function () {
    return document.documentElement.getAttribute('data-theme') || 'light';
};

window.setTheme = function (theme) {
    document.documentElement.setAttribute('data-theme', theme);
    localStorage.setItem('baihua-theme', theme);
};

window.toggleTheme = function () {
    var current = document.documentElement.getAttribute('data-theme') || 'light';
    var next = current === 'dark' ? 'light' : 'dark';
    window.setTheme(next);
    return next;
};
