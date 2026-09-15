/**
 * 二维码工具 - JS 辅助函数
 */
window.generateQRCode = function (container, text) {
    if (!container) return;
    container.innerHTML = '';
    try {
        new QRCode(container, {
            text: text,
            width: 256,
            height: 256,
            colorDark: '#000000',
            colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.M
        });
    } catch (e) {
        console.error('QRCode generation failed:', e);
        container.innerHTML = '<div style="color:red;padding:1rem;">生成二维码失败</div>';
    }
};

/**
 * 紧凑版二维码（180x180），用于节省空间的场景。
 * 兼容三种入参：元素 id 字符串 / 已解析的 HTMLElement / Blazor ElementReference。
 * Blazor Server 时序问题：条件块内的容器可能尚未渲染（ElementReference 未解析），
 * 传 id 时内部轮询等待元素出现（最多 2s），彻底避免 "appendChild is not a function" 崩溃。
 * 返回 Promise<boolean>（生成成功与否），Blazor 侧可用 InvokeAsync<bool> 接收。
 */
window.generateCompactQRCode = function (containerOrId, text) {
    return new Promise((resolve) => {
        let el = resolveQrElement(containerOrId);
        if (el) {
            resolve(renderCompactQR(el, text));
            return;
        }
        // 容器尚未渲染，等待重试
        let tries = 0;
        const timer = setInterval(() => {
            tries++;
            el = resolveQrElement(containerOrId);
            if (el || tries >= 20) {
                clearInterval(timer);
                if (el) {
                    resolve(renderCompactQR(el, text));
                } else {
                    console.error('QR container not found:', containerOrId);
                    resolve(false);
                }
            }
        }, 100);
    });
};

function resolveQrElement(refOrId) {
    if (typeof refOrId === 'string') {
        return document.getElementById(refOrId);
    }
    if (refOrId && typeof refOrId === 'object') {
        // Blazor ElementReference 已解析时是真实 DOM 元素；未解析时是 {__internalId} 占位
        if (refOrId instanceof HTMLElement) return refOrId;
        if (refOrId.__internalId !== undefined) return null;
        return null;
    }
    return null;
}

function renderCompactQR(el, text) {
    if (!el) return false;
    el.innerHTML = '';
    try {
        new QRCode(el, {
            text: text,
            width: 180,
            height: 180,
            colorDark: '#000000',
            colorLight: '#ffffff',
            correctLevel: QRCode.CorrectLevel.M
        });
        return true;
    } catch (e) {
        console.error('Compact QRCode generation failed:', e);
        const tooLong = e && e.message && String(e.message).includes('overflow');
        el.innerHTML = tooLong
            ? '<div style="color:red;padding:1rem;">内容过长，二维码无法生成，请缩短后重试</div>'
            : '<div style="color:red;padding:1rem;">生成二维码失败</div>';
        return false;
    }
}

/**
 * 全屏放大二维码（点击首页/二维码工具里的二维码时调用）。
 *
 * 背景：卡片里的二维码只有 180x180，手机常常凑很久才扫得上。
 * 这里用同一份内容按视口尺寸重绘一张大二维码，铺满屏幕中央，降低扫码难度。
 * 关闭方式：点击任意位置 / 按 Esc / 右上角 ✕。
 *
 * 覆盖层由 JS 直接挂到 document.body（不走 Blazor 渲染树）：既避免条件块渲染时序问题，
 * 也让点击后立刻出现、不等电路往返。样式见 wwwroot/app.css 的「二维码全屏放大」段。
 */
(function () {
    var OVERLAY_ID = 'baihua-qr-fullscreen';
    var BODY_OPEN_CLASS = 'baihua-qr-fullscreen-open';
    var lastFocused = null;

    function closeQrFullscreen() {
        var overlay = document.getElementById(OVERLAY_ID);
        if (overlay) overlay.remove();
        document.removeEventListener('keydown', onFullscreenKeydown);
        document.body.classList.remove(BODY_OPEN_CLASS);
        // 焦点还给触发元素，键盘用户不会丢失位置
        if (lastFocused && document.contains(lastFocused) && typeof lastFocused.focus === 'function') {
            lastFocused.focus();
        }
        lastFocused = null;
    }

    function onFullscreenKeydown(e) {
        if (e.key === 'Escape' || e.key === 'Esc') closeQrFullscreen();
    }

    /**
     * 按视口挑边长：预留白边（二维码静默区，按边长 10% 折算）、提示行与页面留白，
     * 高视口下封顶 900px，避免二维码大到手机镜头对不上焦。
     */
    function pickSize() {
        var available = Math.min(window.innerWidth - 96, (window.innerHeight - 80) / 1.2);
        return Math.max(240, Math.min(900, Math.floor(available)));
    }

    window.openQrFullscreen = function (text) {
        if (!text) return;
        closeQrFullscreen(); // 幂等：重复打开先清掉旧的

        var size = pickSize();
        var quiet = Math.max(16, Math.round(size * 0.1));
        var dpr = Math.min(window.devicePixelRatio || 1, 3); // 高分屏按物理像素绘制，放大后不糊

        var overlay = document.createElement('div');
        overlay.id = OVERLAY_ID;
        overlay.className = 'baihua-qr-fullscreen';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', '二维码放大显示');

        var box = document.createElement('div');
        box.className = 'baihua-qr-fullscreen-box';
        box.style.padding = quiet + 'px';

        var host = document.createElement('div');
        host.className = 'baihua-qr-fullscreen-code';

        var hint = document.createElement('div');
        hint.className = 'baihua-qr-fullscreen-hint';
        hint.textContent = '把手机对准二维码，点击任意位置或按 Esc 关闭';

        var closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'baihua-qr-fullscreen-close';
        closeBtn.setAttribute('aria-label', '关闭');
        closeBtn.textContent = '✕';

        box.appendChild(host);
        overlay.appendChild(box);
        overlay.appendChild(hint);
        overlay.appendChild(closeBtn);
        document.body.appendChild(overlay);
        document.body.classList.add(BODY_OPEN_CLASS);
        lastFocused = document.activeElement;

        try {
            new QRCode(host, {
                text: text,
                width: Math.round(size * dpr),
                height: Math.round(size * dpr),
                colorDark: '#000000',
                colorLight: '#ffffff',
                correctLevel: QRCode.CorrectLevel.M
            });
            // 库只写 canvas 的 width/height 属性，这里压回 CSS 尺寸显示（DPI 缩放的常规做法）
            var painted = host.querySelectorAll('canvas, img');
            for (var i = 0; i < painted.length; i++) {
                painted[i].style.width = size + 'px';
                painted[i].style.height = size + 'px';
            }
        } catch (e) {
            console.error('Fullscreen QRCode generation failed:', e);
            host.innerHTML = '<div style="color:#c00;padding:1rem;">二维码生成失败</div>';
        }

        overlay.addEventListener('click', closeQrFullscreen);
        document.addEventListener('keydown', onFullscreenKeydown);
        closeBtn.focus();
    };

    window.closeQrFullscreen = closeQrFullscreen;

    // 页面跳转（Blazor 增强导航 / 浏览器后退）时清掉覆盖层，避免残留遮挡整页
    if (window.Blazor && typeof window.Blazor.addEventListener === 'function') {
        window.Blazor.addEventListener('enhancedload', closeQrFullscreen);
    }
    window.addEventListener('popstate', closeQrFullscreen);
})();
