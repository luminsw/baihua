/**
 * Enhanced Select - 将原生 select 替换为更易用的 UI
 * 规则：
 *   - 选项 <= 6 个：渲染为水平 chip 按钮组（一眼可见，点击一次选中）
 *   - 选项 > 6 个：保持可搜索下拉框（解决选项过多无法平铺的问题）
 * 自动扫描所有 .form-select 和 .form-control select，保持与 Blazor 绑定兼容
 */
(function () {
    'use strict';

    const CHIP_THRESHOLD = 6;

    function initEnhancedSelects(root) {
        const selects = root.querySelectorAll('select.form-select, select.form-control');
        selects.forEach(enhanceSelect);
    }

    function enhanceSelect(originalSelect) {
        if (originalSelect.dataset.enhanced === 'true') return;
        if (originalSelect.closest('.enhanced-select-wrapper')) return;

        const optionCount = originalSelect.options.length;
        const isDisabled = originalSelect.disabled;

        const wrapper = document.createElement('div');
        wrapper.className = 'enhanced-select-wrapper';
        originalSelect.parentNode.insertBefore(wrapper, originalSelect);
        wrapper.appendChild(originalSelect);

        if (optionCount <= CHIP_THRESHOLD) {
            renderChips(wrapper, originalSelect, isDisabled);
        } else {
            renderDropdown(wrapper, originalSelect, isDisabled);
        }

        originalSelect.style.display = 'none';
        originalSelect.dataset.enhanced = 'true';
    }

    // ========== Chip 模式（选项少，平铺显示）==========
    function renderChips(wrapper, originalSelect, isDisabled) {
        wrapper.classList.add('enhanced-select-chips-mode');

        const chipsContainer = document.createElement('div');
        chipsContainer.className = 'enhanced-select-chips' + (isDisabled ? ' disabled' : '');
        wrapper.appendChild(chipsContainer);

        function buildChips() {
            chipsContainer.innerHTML = '';
            for (let i = 0; i < originalSelect.options.length; i++) {
                const opt = originalSelect.options[i];
                const chip = document.createElement('button');
                chip.type = 'button';
                chip.className = 'enhanced-select-chip' + (opt.selected ? ' selected' : '');
                chip.textContent = opt.text;
                chip.disabled = isDisabled;
                chip.addEventListener('click', function () {
                    if (isDisabled) return;
                    originalSelect.value = opt.value;
                    originalSelect.dispatchEvent(new Event('change', { bubbles: true }));
                    buildChips();
                });
                chipsContainer.appendChild(chip);
            }
        }

        originalSelect.addEventListener('change', buildChips);

        // 监听 value 变化和子节点变化（Blazor 重新渲染 option 列表）
        const observer = new MutationObserver(buildChips);
        observer.observe(originalSelect, { attributes: true, attributeFilter: ['value'], childList: true, subtree: true });

        buildChips();
    }

    // ========== 下拉框模式（选项多，可搜索）==========
    function renderDropdown(wrapper, originalSelect, isDisabled) {
        wrapper.classList.add('enhanced-select-dropdown-mode');

        const trigger = document.createElement('div');
        trigger.className = 'enhanced-select-trigger' + (isDisabled ? ' disabled' : '');
        wrapper.appendChild(trigger);

        const dropdown = document.createElement('div');
        dropdown.className = 'enhanced-select-dropdown';
        wrapper.appendChild(dropdown);

        const searchBox = document.createElement('input');
        searchBox.type = 'text';
        searchBox.className = 'enhanced-select-search';
        searchBox.placeholder = '搜索...';
        dropdown.appendChild(searchBox);

        const optionsContainer = document.createElement('div');
        optionsContainer.className = 'enhanced-select-options';
        dropdown.appendChild(optionsContainer);

        let isOpen = false;

        function updateTrigger() {
            const selected = originalSelect.options[originalSelect.selectedIndex];
            trigger.textContent = selected ? selected.text : '请选择...';
        }

        function buildOptions(filter) {
            optionsContainer.innerHTML = '';
            const filterLower = (filter || '').toLowerCase();
            let hasMatch = false;

            for (let i = 0; i < originalSelect.options.length; i++) {
                const opt = originalSelect.options[i];
                const text = opt.text;
                if (filterLower && !text.toLowerCase().includes(filterLower)) continue;

                hasMatch = true;
                const item = document.createElement('div');
                item.className = 'enhanced-select-option' + (opt.selected ? ' selected' : '');
                item.textContent = text;
                item.dataset.value = opt.value;
                item.addEventListener('click', function (e) {
                    e.stopPropagation();
                    originalSelect.value = opt.value;
                    originalSelect.dispatchEvent(new Event('change', { bubbles: true }));
                    updateTrigger();
                    closeDropdown();
                });
                optionsContainer.appendChild(item);
            }

            if (!hasMatch) {
                const empty = document.createElement('div');
                empty.className = 'enhanced-select-empty';
                empty.textContent = '无匹配选项';
                optionsContainer.appendChild(empty);
            }
        }

        function openDropdown() {
            if (isOpen || isDisabled) return;
            isOpen = true;
            wrapper.classList.add('open');
            searchBox.value = '';
            buildOptions('');
            dropdown.style.display = 'block';
            setTimeout(() => searchBox.focus(), 10);
        }

        function closeDropdown() {
            if (!isOpen) return;
            isOpen = false;
            wrapper.classList.remove('open');
            dropdown.style.display = 'none';
        }

        trigger.addEventListener('click', function (e) {
            e.stopPropagation();
            if (isOpen) {
                closeDropdown();
            } else {
                openDropdown();
            }
        });

        searchBox.addEventListener('input', function () {
            buildOptions(this.value);
        });

        searchBox.addEventListener('click', function (e) {
            e.stopPropagation();
        });

        searchBox.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') {
                closeDropdown();
                return;
            }
            if (e.key === 'Enter') {
                const first = optionsContainer.querySelector('.enhanced-select-option');
                if (first) first.click();
                return;
            }
        });

        // 监听 value 变化和子节点变化（Blazor 重新渲染 option 列表）
        const observer = new MutationObserver(function () {
            updateTrigger();
            buildOptions('');
        });
        observer.observe(originalSelect, { attributes: true, attributeFilter: ['value'], childList: true, subtree: true });

        originalSelect.addEventListener('change', updateTrigger);

        document.addEventListener('click', function (e) {
            if (!wrapper.contains(e.target)) {
                closeDropdown();
            }
        });

        updateTrigger();
        buildOptions('');
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            initEnhancedSelects(document);
        });
    } else {
        initEnhancedSelects(document);
    }

    const bodyObserver = new MutationObserver(function (mutations) {
        mutations.forEach(function (m) {
            m.addedNodes.forEach(function (node) {
                if (node.nodeType === 1) {
                    initEnhancedSelects(node);
                }
            });
        });
    });
    bodyObserver.observe(document.body, { childList: true, subtree: true });
})();
