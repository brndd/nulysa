(() => {
    const root = document.documentElement;
    const system = matchMedia('(prefers-color-scheme: dark)');
    function syncModeSwitch() {
        const dark = root.dataset.bsTheme === 'dark';
        document.querySelectorAll('[data-mode-switch]').forEach(button => {
            button.setAttribute('aria-checked', String(dark));
            button.value = dark ? 'light' : 'dark';
            button.hidden = false;
            button.form.classList.add('mode-switch-ready');
        });
    }
    function applyMode() {
        root.dataset.bsTheme = root.dataset.mode === 'system'
            ? (system.matches ? 'dark' : 'light') : root.dataset.mode;
        syncModeSwitch();
    }
    applyMode();
    system.addEventListener('change', applyMode);

    document.addEventListener('toggle', event => {
        if (!event.target.matches('.header-dropdown') || !event.target.open) return;
        document.querySelectorAll('.header-dropdown[open]').forEach(dropdown => {
            if (dropdown !== event.target) dropdown.open = false;
        });
    }, true);
    document.addEventListener('click', event => {
        document.querySelectorAll('.header-dropdown[open]').forEach(dropdown => {
            if (!dropdown.contains(event.target)) dropdown.open = false;
        });
    });
    document.addEventListener('keydown', event => {
        const dropdown = event.target.closest('.header-dropdown');
        if (event.key === 'Escape' && dropdown?.open) {
            event.preventDefault();
            dropdown.open = false;
            dropdown.querySelector('summary').focus();
        }
    });
    document.addEventListener('submit', event => {
        const returnUrl = event.target.closest('.header-preferences') && event.target.querySelector('[name="ReturnUrl"]');
        if (returnUrl) returnUrl.value = location.pathname + location.search + location.hash;
    });

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
        approve.textContent = approve.dataset.confirmLabel;
        approve.classList.add('confirm-action');
        approve.disabled = true;
        setTimeout(() => { if (approve.isConnected) approve.disabled = false; }, 700);
        setTimeout(() => {
            if (!approve.isConnected) return;
            approve.dataset.armed = 'false';
            approve.textContent = approve.dataset.approveLabel;
            approve.classList.remove('confirm-action');
            confirmed.checked = false;
        }, 10000);
    });
    function focusErrors() {
        const dialog = document.querySelector('dialog:modal');
        const error = (dialog || document.querySelector('main'))?.querySelector('[role="alert"]');
        if (!error?.textContent.trim()) return;
        error.tabIndex = -1;
        error.focus();
    }
    function preferenceForms() {
        document.querySelectorAll('[data-preference-form]').forEach(form => {
            const checkboxes = form.querySelectorAll('input[type="checkbox"]');
            const submit = form.querySelector('button[type="submit"], button:not([type])');
            submit.disabled = Array.from(checkboxes).every(checkbox =>
                checkbox.checked === ((checkbox.dataset.saved ?? form.dataset.saved) === 'true'));
        });
    }
    document.addEventListener('change', event => {
        if (event.target.closest('[data-preference-form]')) preferenceForms();
    });
    document.addEventListener('DOMContentLoaded', () => {
        applyMode();
        navigation();
        dialogs();
        preferenceForms();
        focusErrors();
        if (window.Blazor) Blazor.addEventListener('enhancedload', () => { applyMode(); navigation(); dialogs(); preferenceForms(); focusErrors(); });
    });
})();
