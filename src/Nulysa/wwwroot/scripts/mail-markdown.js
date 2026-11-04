import "../vendor/github-markdown-toolbar/index.js";

export function connectEditor(tabs, input) {
    if (!tabs || !(input instanceof HTMLTextAreaElement)) return;

    // Markdown headings need a line boundary. Let GitHub's control handle the actual edit.
    const heading = tabs.parentElement.querySelector("md-header");
    function selectHeadingLine() {
        const { value, selectionStart } = input;
        const start = selectionStart === 0 ? 0 : value.lastIndexOf("\n", selectionStart - 1) + 1;
        const end = value.indexOf("\n", selectionStart);
        input.setSelectionRange(start, end === -1 ? value.length : end);
    }
    heading.addEventListener("click", selectHeadingLine, { capture: true });
    heading.addEventListener("keydown", event => {
        if (event.key === "Enter" || event.key === " ") selectHeadingLine();
    }, { capture: true });

    // Preview links should not navigate away from the draft being edited.
    tabs.closest(".markdown-panel").addEventListener("click", event => {
        if (event.target.closest(".markdown-preview a")) event.preventDefault();
    });

    tabs.addEventListener("keydown", event => {
        const keys = ["ArrowLeft", "ArrowRight", "Home", "End"];
        if (!keys.includes(event.key)) return;

        const buttons = [...tabs.querySelectorAll('[role="tab"]')];
        const current = buttons.indexOf(event.target);
        if (current === -1) return;
        const next = event.key === "Home" ? 0 : event.key === "End" ? buttons.length - 1
            : (current + (event.key === "ArrowRight" ? 1 : -1) + buttons.length) % buttons.length;

        event.preventDefault();
        buttons[next].focus();
        buttons[next].click();
    });
}

export function insertOperationalDetails(input, marker) {
    if (!(input instanceof HTMLTextAreaElement)) return;

    // Insert a standalone paragraph without replacing selected prose.
    const newline = input.value.indexOf("\n", input.selectionEnd);
    const position = newline === -1 ? input.value.length : newline;
    const insertion = (position === 0 ? "" : "\n\n") + marker + "\n\n";
    input.setRangeText(insertion, position, position, "end");
    input.focus();
    input.dispatchEvent(new Event("input", { bubbles: true }));
}
