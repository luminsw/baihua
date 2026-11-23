// SmartSelect 点击外部关闭支持
window.smartSelectInit = function (element, dotNetRef) {
    function onClickOutside(e) {
        if (!element.contains(e.target)) {
            dotNetRef.invokeMethodAsync('Close');
        }
    }
    document.addEventListener('click', onClickOutside);
    // 保存引用以便后续清理
    element._smartSelectCleanup = function () {
        document.removeEventListener('click', onClickOutside);
    };
};

window.smartSelectFocus = function (element) {
    if (element) {
        element.focus();
    }
};
