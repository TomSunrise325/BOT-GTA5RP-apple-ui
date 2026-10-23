window.globalEventSource = null;

// Hide the splash on load, but never wait on slow external fonts for more than ~1.2s.
(function() {
    const hide = () => {
        const loader = document.getElementById('loader');
        if (loader) loader.classList.add('hidden');
    };
    window.addEventListener('load', hide);
    document.addEventListener('DOMContentLoaded', () => setTimeout(hide, 1200));
})();

window.logManager = {
    append: function(text) {
        const container = document.getElementById('log-container');
        if (!container) return;
        // The bot only acts while the game window has focus. Its raw messages read like "not found",
        // so say what is actually going on (display only; the real log text is unchanged).
        text = text
            .replace(/Окно (.+?) не активно\. Ожидание\.*/, 'Окно $1 открыто, но не в фокусе — переключитесь в игру, скрипт продолжит сам')
            .replace(/Окно (.+?) найдено\. Скрипт активен\.?/, 'Окно $1 в фокусе — скрипт работает')
            .replace(/Окно (.+?) не найдено\.?/, 'Окно $1 не найдено — запущена ли игра?');
        const div = document.createElement('div');
        div.textContent = text;

        if (text.includes("Сглаживание шрифтов: выключено")) {
            div.classList.add('log-highlight');
        } else if (/ошибк|error|fail/i.test(text)) {
            div.classList.add('log-err');
        } else if (/запущен|запустил|успеш/i.test(text)) {
            div.classList.add('log-ok');
        }

        container.appendChild(div);
        if (container.children.length > 100) container.removeChild(container.firstChild);
        container.scrollTop = container.scrollHeight;
    },
    handleSSE: function(event) {
        window.logManager.append(event.data);
        const isActive = event.data.includes("запущен") || event.data.includes("запустил");
        const isStopped = event.data.includes("остановлен");

        if (isActive && typeof window.updatePageUI === 'function') window.updatePageUI(true);
        if (isStopped && typeof window.updatePageUI === 'function') window.updatePageUI(false);
    }
};

window.updateModuleUI = function(btnId, active) {
    const btn = document.getElementById(btnId);
    if (!btn) return;
    btn.innerText = active ? 'Остановить' : 'Запустить';
    btn.classList.toggle('running', active);
    btn.style.background = '';
};

window.loadModuleLogs = function(moduleName) {
    const container = document.getElementById('log-container');
    if (container) container.innerHTML = '';

    fetch('/api/get_logs')
        .then(res => res.json())
        .then(data => {
            if (window.logManager && data.logs) {
                data.logs
                    .filter(l => l.includes(`[${moduleName}]`))
                    .forEach(l => window.logManager.append(l));
            }
        })
        .catch(err => console.error('Ошибка загрузки логов:', err));
};

function refreshUIConfig() {
    return fetch('/api/settings/get')
        .then(res => res.json())
        .then(settings => {
            window.UI_CONFIG = {
                hoverEnabled: settings.switch_hover,
                clickEnabled: settings.switch_click,
                hoverVol: settings.volume_hover / 100,
                clickVol: settings.volume_click / 100,
                background: settings.background || 'bot'
            };
        })
        .catch(err => console.error('Ошибка обновления UI_CONFIG:', err));
}

function loadPageContent(url) {
    const container = document.querySelector('.container');
    if (!container) return;

    container.classList.add('loading');

    fetch(url)
        .then(res => res.text())
        .then(html => {
            const parser = new DOMParser();
            const newDoc = parser.parseFromString(html, 'text/html');
            const newContent = newDoc.querySelector('.container');

            if (newContent) {
                document.querySelectorAll('.dynamic-script').forEach(s => s.remove());
                container.innerHTML = newContent.innerHTML;

                updateActiveLink();
                initSliders();
                initSounds();
                initTooltips();
                container.classList.remove('loading');

                requestAnimationFrame(() => {
                    newContent.querySelectorAll('script').forEach((oldScript, index) => {
                        setTimeout(() => {
                            const newScript = document.createElement('script');
                            newScript.classList.add('dynamic-script');
                            if (oldScript.src) {
                                newScript.src = oldScript.src;
                            } else {
                                newScript.textContent = oldScript.textContent;
                            }
                            document.body.appendChild(newScript);
                        }, index * 10);
                    });
                });

                const pathParts = window.location.pathname.split('/').filter(p => p);
                const pageName = pathParts[pathParts.length - 1] || 'index';
                fetch('/api/set_active_tab/' + pageName, { method: 'POST' }).catch(() => {});
            }
        })
        .catch(err => {
            console.error("Ошибка загрузки страницы:", err);
            container.classList.remove('loading');
        });
}

function initEventSource() {
    if (!window.globalEventSource) {
        window.globalEventSource = new EventSource('/api/events');
        window.globalEventSource.onmessage = window.logManager.handleSSE;
    }
}

function initSliders() {
    document.querySelectorAll('input[type="range"]').forEach(slider => {
        if (slider.dataset.initialized === 'true') return;

        const update = () => {
            const percent = ((slider.value - slider.min) / (slider.max - slider.min)) * 100;
            // Qt WebEngine repaint fix: set backgroundSize directly (numeric property repaints reliably)
            slider.style.backgroundSize = `${percent}% 100%`;
        };

        // Qt WebEngine repaint fix: drive hover/active via JS classes
        // because :hover/:active on ::-webkit-slider-thumb don't trigger repaint in Qt
        slider.addEventListener('mouseenter', () => {
            slider.classList.add('is-hover');
        });
        slider.addEventListener('mouseleave', () => {
            slider.classList.remove('is-hover', 'is-active');
        });
        slider.addEventListener('mousedown', () => {
            slider.classList.add('is-active');
        });
        slider.addEventListener('mouseup', () => {
            slider.classList.remove('is-active');
        });

        update();
        slider.addEventListener('input', update);
        slider.dataset.initialized = 'true';
    });
}

function updateActiveLink() {
    const currentPath = window.location.pathname;
    document.querySelectorAll('.nav-links a').forEach(link => {
        const on = link.getAttribute('href') === currentPath;
        link.classList.toggle('active', on);
        if (on) link.setAttribute('aria-current', 'page'); else link.removeAttribute('aria-current');
    });
}

function quickPlay(type, event) {
    const config = window.UI_CONFIG;
    if (!config) return;

    if (event && event.target) {
        const toggle = event.target.closest('.toggle-switch');
        if (toggle && (toggle.id === 'switch-hover' || toggle.id === 'switch-click')) {
            return;
        }
    }

    let audioId = (type === 'hover') ? 'hover-sound' : 'click-sound';
    let isEnabled = (type === 'hover') ? config.hoverEnabled : config.clickEnabled;
    let volume = (type === 'hover') ? config.hoverVol : config.clickVol;

    if (isEnabled) {
        const audio = document.getElementById(audioId);
        if (audio) {
            audio.volume = volume;
            audio.currentTime = 0;
            audio.play().catch(() => {});
        }
    }
}

const handleHover = (e) => quickPlay('hover', e);
const handleClick = (e) => quickPlay('click', e);

function initSounds() {
    const soundElements = document.querySelectorAll('.module-switch, .btn-action, .nav-links a, button, .slider');

    soundElements.forEach(el => {
        if (el.dataset.soundInit === 'true') return;

        el.addEventListener('mouseenter', handleHover);
        el.addEventListener('click', handleClick);
        el.dataset.soundInit = 'true';
    });
}

document.addEventListener("DOMContentLoaded", () => {
    initEventSource();

    document.addEventListener('click', (e) => {
        const link = e.target.closest('.nav-links a, a[data-nav]');
        if (link && link.getAttribute('href') && !link.getAttribute('href').startsWith('http')) {
            e.preventDefault();
            const targetUrl = link.href;

            if (window.location.href !== targetUrl) {
                window.history.pushState({}, '', targetUrl);
                loadPageContent(targetUrl);
            }
        }
    });

    window.onpopstate = () => loadPageContent(window.location.pathname);

    updateActiveLink();
    initSounds();
    initSliders();
    initTooltips();
});

function initTooltips() {
    let tooltipContainer = document.getElementById('global-tooltip');
    if (!tooltipContainer) {
        tooltipContainer = document.createElement('div');
        tooltipContainer.id = 'global-tooltip';
        tooltipContainer.className = 'custom-tooltip';
        document.body.appendChild(tooltipContainer);
    }

    const navItems = document.querySelectorAll('.nav-item[data-tooltip]');

    navItems.forEach(item => {
        const tooltipText = item.getAttribute('data-tooltip');
        if (!tooltipText) return;

        item.addEventListener('mouseenter', (e) => {
            const isClosed = !document.body.classList.contains('sidebar-open');
            if (!isClosed) return;

            const rect = item.getBoundingClientRect();
            tooltipContainer.textContent = tooltipText;
            tooltipContainer.style.left = (rect.right + 10) + 'px';
            tooltipContainer.style.top = (rect.top + rect.height / 2) + 'px';
            tooltipContainer.style.setProperty('opacity', '1', 'important');
            tooltipContainer.style.setProperty('visibility', 'visible', 'important');
        });

        item.addEventListener('mouseleave', () => {
            tooltipContainer.style.setProperty('opacity', '0', 'important');
            tooltipContainer.style.setProperty('visibility', 'hidden', 'important');
        });
    });
}

function toggleSidebar() {
    const open = document.body.classList.toggle('sidebar-open');
    try { localStorage.setItem('sidebar', open ? 'open' : 'closed'); } catch (e) {}
}

// Narrow window: fold the sidebar into the icon rail; wide again: restore the person's choice.
window.addEventListener('resize', () => {
    if (window.innerWidth < 900) {
        document.body.classList.remove('sidebar-open');
    } else {
        let pref = 'open';
        try { pref = localStorage.getItem('sidebar') || 'open'; } catch (e) {}
        if (pref !== 'closed') document.body.classList.add('sidebar-open');
    }
});

// Qt WebEngine repaint fix: animate checkbox ::after via JS class instead of :checked CSS trigger
document.addEventListener('change', function(e) {
    const el = e.target;
    if (!el.classList.contains('checkbox-input')) return;
    el.classList.remove('is-checked', 'is-unchecked');
    requestAnimationFrame(() => {
        el.classList.add(el.checked ? 'is-checked' : 'is-unchecked');
    });
}, true);

// Copies the visible log to the clipboard (the old "send to Telegram" button now just copies).
window.sendLogsToTelegram = function(ev) {
    const container = document.getElementById('log-container');
    const btn = (ev && ev.currentTarget) || document.querySelector('.floating-send-btn');
    if (!container || container.innerText.trim() === "") return;

    const done = () => {
        if (!btn) return;
        btn.classList.add('done');
        btn.innerHTML = '<svg class="ico"><use href="#i-check"/></svg>';
        setTimeout(() => {
            btn.classList.remove('done');
            btn.innerHTML = '<svg class="ico"><use href="#i-copy"/></svg>';
        }, 1400);
    };
    const fallback = () => {
        const ta = document.createElement('textarea');
        ta.value = container.innerText;
        ta.style.cssText = 'position:fixed;opacity:0';
        document.body.appendChild(ta);
        ta.select();
        try { document.execCommand('copy'); } catch (e) {}
        ta.remove();
        done();
    };
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(container.innerText).then(done, fallback);
    } else {
        fallback();
    }
};

window.openVpn = function(e) {
    if (e) e.preventDefault();
    window.open('https://t.me/XmartVPN_bot', '_blank');
};

/* ===== Emoji -> SF Symbols-style icons ===== */
(function() {
    const MAP = {
        '🏠': 'home', '🕹': 'gamepad', '🎮': 'gamepad', '⛓': 'lock', '⛏': 'hammer', '⚓': 'anchor',
        '🐄': 'leaf', '🏋': 'dumbbell', '🍜': 'utensils', '🍔': 'utensils', '📱': 'phone', '🏆': 'trophy',
        '⌨': 'keyboard', '⚙': 'sliders', '📋': 'terminal', 'ℹ': 'info', '🔊': 'volume', '🔔': 'bell',
        '🖼': 'image', '📊': 'chart', '⚠': 'warning', '📥': 'download', '⬇': 'download', '🔄': 'refresh',
        '👆': 'pointer', '✊': 'grip', '✨': 'sparkles', '✕': 'close', '✏': 'pencil', '🗑': 'trash',
        '▶': 'play', '⏹': 'stop', '📤': 'copy', '🛡': 'shield'
    };
    const RE = new RegExp('(' + Object.keys(MAP).join('|') + ')[\\uFE0E\\uFE0F]?', 'g');
    const SKIP = new Set(['SCRIPT', 'STYLE', 'TEXTAREA', 'INPUT', 'OPTION', 'TITLE', 'SVG']);

    function iconNode(name, inline) {
        const s = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
        s.setAttribute('class', 'ico' + (inline ? ' ico-inline' : ''));
        const u = document.createElementNS('http://www.w3.org/2000/svg', 'use');
        u.setAttribute('href', '#i-' + name);
        s.appendChild(u);
        return s;
    }

    function decorate(root) {
        if (!root || root.nodeType === 8) return;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
            acceptNode(n) {
                if (!n.nodeValue || !RE.test(n.nodeValue)) { RE.lastIndex = 0; return NodeFilter.FILTER_REJECT; }
                RE.lastIndex = 0;
                const p = n.parentNode;
                return (p && SKIP.has((p.nodeName || '').toUpperCase())) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
            }
        });
        const nodes = [];
        while (walker.nextNode()) nodes.push(walker.currentNode);
        nodes.forEach(node => {
            const text = node.nodeValue;
            const frag = document.createDocumentFragment();
            let last = 0, m;
            RE.lastIndex = 0;
            while ((m = RE.exec(text)) !== null) {
                if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
                const name = MAP[m[1]];
                const after = text.slice(RE.lastIndex);
                const inline = !(m[1] === '✕' || m[1] === '▶' || m[1] === '⏹' || m[1] === '✏' || m[1] === '🗑') && /^\s*\S/.test(after);
                frag.appendChild(iconNode(name, inline));
                last = RE.lastIndex;
                // swallow one trailing space after an inline icon (margin does the spacing)
                if (inline && text[last] === ' ') last++;
            }
            if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
            node.parentNode.replaceChild(frag, node);
        });
    }

    // Icons are decoration; icon-only buttons get their title as an accessible name.
    function a11y(root) {
        (root.querySelectorAll ? root.querySelectorAll('svg.ico') : []).forEach(s => s.setAttribute('aria-hidden', 'true'));
        (root.querySelectorAll ? root.querySelectorAll('button[title]:not([aria-label])') : []).forEach(b => {
            if (!b.textContent.trim()) b.setAttribute('aria-label', b.getAttribute('title'));
        });
    }

    window.decorateIcons = decorate;
    const start = () => {
        decorate(document.body);
        a11y(document.body);
        new MutationObserver(muts => {
            muts.forEach(mu => mu.addedNodes.forEach(n => {
                if (n.nodeType === 1) { decorate(n); a11y(n); }
                else if (n.nodeType === 3 && n.parentNode) decorate(n.parentNode);
            }));
        }).observe(document.body, { childList: true, subtree: true });
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
    else start();
})();
