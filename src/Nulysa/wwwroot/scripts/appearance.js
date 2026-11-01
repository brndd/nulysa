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

    function dialogs() {
        root.classList.add('js-controls');
        document.querySelectorAll('.dialog-disclosure').forEach(details => {
            const dialog = details.querySelector('dialog');
            if (!dialog.dataset.ready) {
                dialog.dataset.ready = 'true';
                dialog.addEventListener('close', () => {
                    details.open = false;
                    if (details.isConnected) details.querySelector('summary').focus();
                });
            }
            if (!dialog.matches(':modal')) {
                dialog.removeAttribute('open');
                if (details.open) dialog.showModal();
            }
        });
    }
    document.addEventListener('toggle', event => {
        const details = event.target;
        if (!details.matches('.dialog-disclosure')) return;
        const dialog = details.querySelector('dialog');
        if (details.open && !dialog.matches(':modal')) {
            dialog.removeAttribute('open');
            dialog.showModal();
        } else if (!details.open && dialog.matches(':modal')) dialog.close();
    }, true);
    document.addEventListener('click', event => {
        const cancel = event.target.closest('.dialog-cancel');
        if (cancel) cancel.closest('dialog').close();
        const approve = event.target.closest('[data-confirm-approval]');
        if (!approve) return;
        const confirmed = approve.form.querySelector('[name="Input.Confirmed"]');
        if (approve.dataset.armed === 'true') {
            confirmed.checked = true;
            return;
        }
        event.preventDefault();
        approve.dataset.armed = 'true';
        approve.textContent = 'Confirm membership';
        approve.classList.add('confirm-action');
        approve.disabled = true;
        setTimeout(() => { if (approve.isConnected) approve.disabled = false; }, 700);
        setTimeout(() => {
            if (!approve.isConnected) return;
            approve.dataset.armed = 'false';
            approve.textContent = 'Approve membership';
            approve.classList.remove('confirm-action');
            confirmed.checked = false;
        }, 10000);
    });
    function preferenceForms() {
        document.querySelectorAll('[data-preference-form]').forEach(form => {
            const checkbox = form.querySelector('input[type="checkbox"]');
            const submit = form.querySelector('button[type="submit"], button:not([type])');
            submit.disabled = checkbox.checked === (form.dataset.saved === 'true');
        });
    }
    document.addEventListener('change', event => {
        if (event.target.closest('[data-preference-form]')) preferenceForms();
    });
    document.addEventListener('DOMContentLoaded', () => {
        navigation();
        dialogs();
        preferenceForms();
        if (window.Blazor) Blazor.addEventListener('enhancedload', () => { navigation(); dialogs(); preferenceForms(); });
    });
})();
