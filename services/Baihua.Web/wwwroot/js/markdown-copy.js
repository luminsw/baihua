/**
 * 为 Markdown 渲染后的代码块添加复制按钮
 */

/**
 * 按容器范围添加复制按钮（供 StreamingMessage 组件在自身渲染后调用，
 * 替代父组件每次渲染对全页的扫描）。
 */
function addCopyButtonsToCodeBlocksIn(root) {
    if (!root || !root.querySelectorAll) return;
    root.querySelectorAll('.message-text pre').forEach(function(pre) {
        // 避免重复添加
        if (pre.querySelector('.code-copy-btn')) return;

        var btn = document.createElement('button');
        btn.className = 'code-copy-btn';
        btn.innerHTML = '📋 复制';
        btn.title = '复制代码';

        btn.addEventListener('click', function() {
            var code = pre.querySelector('code');
            var text = code ? code.innerText : pre.innerText;
            navigator.clipboard.writeText(text).then(function() {
                btn.innerHTML = '✅ 已复制';
                btn.classList.add('copied');
                setTimeout(function() {
                    btn.innerHTML = '📋 复制';
                    btn.classList.remove('copied');
                }, 2000);
            }).catch(function() {
                btn.innerHTML = '❌ 失败';
                setTimeout(function() {
                    btn.innerHTML = '📋 复制';
                }, 2000);
            });
        });

        pre.appendChild(btn);
    });
}

/**
 * 仅对某个消息容器内的代码块执行 hljs 高亮（替代全量 hljs.highlightAll，
 * hljs.highlightElement 自带 data-highlighted 去重，重复调用无害）。
 */
function highlightCodeBlocksIn(root) {
    if (!root || !root.querySelectorAll || typeof hljs === 'undefined') return;
    root.querySelectorAll('pre code').forEach(function(el) {
        hljs.highlightElement(el);
    });
}

// 兼容旧调用（整页范围）
function addCopyButtonsToCodeBlocks() {
    addCopyButtonsToCodeBlocksIn(document);
}

window.addCopyButtonsToCodeBlocks = addCopyButtonsToCodeBlocks;
window.addCopyButtonsToCodeBlocksIn = addCopyButtonsToCodeBlocksIn;
window.highlightCodeBlocksIn = highlightCodeBlocksIn;
