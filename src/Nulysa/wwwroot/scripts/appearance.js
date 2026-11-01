(() => {
    const root = document.documentElement;
    const system = matchMedia('(prefers-color-scheme: dark)');
    function applyMode() {
        root.dataset.bsTheme = root.dataset.mode === 'system'
            ? (system.matches ? 'dark' : 'light') : root.dataset.mode;
    }
    applyMode();
    system.addEventListener('change', applyMode);

    function navigation() {
        const button = document.querySelector('.navigation-toggle');
        if (!button) return;
        button.hidden = false;
        const container = button.closest('.navigation-container');
        container.classList.add('navigation-ready');
    }
    function close(container) {
        container.querySelector('.navigation-toggle').setAttribute('aria-expanded', 'false');
        container.classList.remove('navigation-open');
    }
    document.addEventListener('click', event => {
        const button = event.target.closest('.navigation-toggle');
        if (button) {
            const container = button.closest('.navigation-container');
            const open = button.getAttribute('aria-expanded') !== 'true';
            button.setAttribute('aria-expanded', String(open));
            container.classList.toggle('navigation-open', open);
        } else if (event.target.closest('.sidebar a')) {
            close(event.target.closest('.navigation-container'));
        }
    });
    document.addEventListener('keydown', event => {
        const container = event.target.closest('.sidebar, .navigation-toggle')?.closest('.navigation-container');
        if (event.key === 'Escape' && container) {
            close(container);
            container.querySelector('.navigation-toggle').focus();
        }
    });
    document.addEventListener('DOMContentLoaded', () => {
        navigation();
        if (window.Blazor) Blazor.addEventListener('enhancedload', navigation);
    });
})();
