'use strict';

const styles = `
:root {
    --base: var(--vscode-editor-background);
    --fg: var(--vscode-editor-foreground);
    --muted: var(--vscode-descriptionForeground, var(--fg));
    --panel: var(--vscode-sideBar-background, var(--base));
    --line: var(--vscode-contrastBorder, var(--vscode-panel-border, var(--vscode-editorWidget-border, transparent)));
    --focus: var(--vscode-focusBorder, var(--fg));
    --link: var(--vscode-textLink-foreground, var(--fg));
    --selected: var(--vscode-list-activeSelectionBackground, var(--vscode-editor-lineHighlightBackground));
}
* { box-sizing: border-box; }
[hidden] { display: none !important; }
body {
    margin: 0;
    padding: 0;
    height: 100vh;
    display: flex;
    flex-direction: column;
    color: var(--fg);
    background: var(--base);
    font: var(--vscode-font-size, 13px)/1.4 var(--vscode-font-family, system-ui, sans-serif);
}
button, input, select { font: inherit; color: inherit; }
button {
    cursor: pointer;
    border: 1px solid var(--line);
    border-radius: 4px;
    padding: 4px 9px;
    background: var(--vscode-button-secondaryBackground, var(--base));
    color: var(--vscode-button-secondaryForeground, var(--fg));
}
button:hover:not(:disabled) { background: var(--vscode-button-secondaryHoverBackground, var(--vscode-list-hoverBackground)); }
button:disabled { cursor: default; color: var(--vscode-disabledForeground, var(--muted)); }
button.primary { color: var(--vscode-button-foreground); background: var(--vscode-button-background); }
button.primary:hover { background: var(--vscode-button-hoverBackground); }
input, select {
    min-height: 28px;
    padding: 3px 7px;
    border: 1px solid var(--vscode-input-border, var(--line));
    border-radius: 3px;
    background: var(--vscode-input-background, var(--base));
    color: var(--vscode-input-foreground, var(--fg));
}
select option { background: var(--vscode-dropdown-background, var(--base)); color: var(--vscode-dropdown-foreground, var(--fg)); }
input::placeholder { color: var(--vscode-input-placeholderForeground, var(--muted)); }
button:focus-visible, input:focus-visible, select:focus-visible, [tabindex="0"]:focus-visible {
    outline: 2px solid var(--focus);
    outline-offset: -2px;
}
.visually-hidden {
    position: absolute;
    width: 1px; height: 1px; overflow: hidden;
    clip-path: inset(50%); white-space: nowrap;
}
h1, h2, h3, p { margin: 0; }
.masthead {
    display: grid;
    grid-template-columns: minmax(0, 1fr) auto;
    gap: 8px 20px;
    align-items: center;
    padding: 14px 18px 11px;
    border-bottom: 1px solid var(--line);
}
.eyebrow, .pane-heading span, .listing-footer, .pager span { color: var(--muted); font-size: .88em; }
.eyebrow { letter-spacing: .06em; text-transform: uppercase; }
h1 { font-size: 1.42em; font-weight: 650; overflow-wrap: anywhere; }
h2 { font-size: 1em; font-weight: 650; }
h3 { font-size: .94em; font-weight: 650; }
.actions, .facts, .panel-controls, .search-controls, .goto, .pager { display: flex; align-items: center; gap: 6px; }
.actions, .facts { flex-wrap: wrap; }
.facts { grid-column: 1 / -1; gap: 6px 10px; font-size: .9em; }
.facts > span { color: var(--muted); }
.facts .stage {
    font-weight: 600;
    color: var(--vscode-badge-foreground, var(--fg));
    background: var(--vscode-badge-background, var(--panel));
    border: 1px solid var(--line);
    border-radius: 4px;
    padding: 2px 7px;
}
.facts .explanation { margin-inline-start: auto; }
.empty { margin: 18px; padding: 16px; border: 1px solid var(--line); background: var(--panel); }
.empty pre { margin-bottom: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.workspace { flex: 1; display: flex; min-height: 0; }
.outline, .detail {
    display: flex;
    flex-direction: column;
    min-width: 0;
    background: var(--panel);
}
.outline { flex: 0 0 clamp(168px, 21vw, 248px); border-inline-end: 1px solid var(--line); }
.pane-heading { padding: 10px 12px 7px; display: flex; justify-content: space-between; align-items: baseline; gap: 6px; }
.outline select { margin: 0 10px 8px; }
.outline-scroll, .detail-scroll { min-height: 0; overflow: auto; flex: 1; }
.outline-scroll ol { list-style: none; margin: 0; padding: 0 5px; }
.outline-entry {
    display: flex; gap: 5px; width: 100%; align-items: baseline;
    text-align: start; border-color: transparent; padding: 4px 6px;
    background: transparent; color: var(--fg);
}
.outline-entry .entry-name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.outline-entry.child { padding-inline-start: 16px; }
.outline-entry[aria-current="true"] { background: var(--selected); color: var(--vscode-list-activeSelectionForeground, var(--fg)); border-color: var(--line); }
.outline-entry[aria-current="true"]:hover:not(:disabled) { background: var(--selected); }
.outline > .pager { padding: 7px 9px; justify-content: space-between; border-top: 1px solid var(--line); }
.inspection, .listing { display: flex; flex-direction: column; min-width: 0; min-height: 0; flex: 1; }
.toolbar {
    display: flex; flex-wrap: wrap; align-items: center; gap: 6px 10px;
    padding: 7px 10px; border-bottom: 1px solid var(--line);
    background: var(--vscode-editorWidget-background, var(--base));
}
.search-controls { flex: 1 1 180px; min-width: 0; }
#find { flex: 1; min-width: 80px; }
#match-count { color: var(--muted); min-width: 5ch; font-size: .87em; }
#goto-line { width: 8ch; }
.panes { display: flex; min-width: 0; min-height: 0; flex: 1; }
.code-scroll {
    min-height: 0; flex: 1; overflow: auto; tab-size: 4;
    font: var(--vscode-editor-font-size, 13px)/1.55 var(--vscode-editor-font-family, monospace);
    padding: 6px 0;
}
.code-content { width: max-content; min-width: 100%; }
.line { display: flex; align-items: baseline; white-space: pre; padding-inline-end: 16px; }
.line.match { background: var(--vscode-editor-findMatchHighlightBackground, var(--vscode-editor-lineHighlightBackground)); }
.line.selected {
    background: var(--vscode-editor-lineHighlightBackground, var(--panel));
    box-shadow: inset 0 1px var(--focus), inset 0 -1px var(--focus);
}
.line-number {
    align-self: stretch; position: sticky; inset-inline-start: 0;
    color: var(--vscode-editorLineNumber-foreground, var(--muted)); border: 0; border-radius: 0;
    border-inline-start: 2px solid transparent;
    background: var(--base); min-width: 8ch; padding: 0 1.2ch 0 .4ch;
    text-align: end; user-select: none;
}
.selected .line-number { color: var(--vscode-editorLineNumber-activeForeground, var(--fg)); border-inline-start: 2px solid var(--focus); }
.line code { font: inherit; background: none; padding: 0; border-radius: 0; }
.code-link {
    border: 0; border-radius: 2px; font: inherit; color: inherit;
    display: inline; background: none; padding: 0; text-decoration: underline; text-decoration-color: var(--link);
    text-underline-offset: .15em;
}
.code-link:hover:not(:disabled) { background: var(--vscode-editor-hoverHighlightBackground, var(--vscode-list-hoverBackground)); }
.listing-footer { display: flex; justify-content: space-between; align-items: center; gap: 8px; padding: 6px 10px; border-top: 1px solid var(--line); }
.detail { flex: 0 0 clamp(210px, 24vw, 290px); border-inline-start: 1px solid var(--line); }
.detail-scroll { padding: 0 12px 12px; overflow-wrap: anywhere; }
.detail-scroll p, .detail-scroll section, .detail-scroll dl { margin: 8px 0; }
.detail-scroll pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: 4px 0; }
.detail-scroll code, .detail-scroll pre { font-family: var(--vscode-editor-font-family, monospace); }
.detail-scroll dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 2px 9px; }
.detail-scroll dt { color: var(--muted); }
.detail-scroll dd { margin: 0; }
.detail-scroll ul { list-style: none; margin: 4px 0; padding: 0; }
.detail-scroll li { padding: 4px 0; border-top: 1px solid var(--line); }
.detail-scroll li button { max-width: 100%; text-align: start; overflow-wrap: anywhere; }
.quiet { color: var(--muted); }
.keyword,.control { color: var(--vscode-symbolIcon-keywordForeground, var(--fg)); font-weight: 600; }
.opcode { color: var(--vscode-symbolIcon-operatorForeground, var(--fg)); }
.string,.path { color: var(--vscode-debugTokenExpression-string, var(--fg)); }
.comment { color: var(--muted); }
.function { color: var(--vscode-symbolIcon-functionForeground, var(--fg)); }
.constant { color: var(--vscode-symbolIcon-constantForeground, var(--fg)); }
.block { color: var(--vscode-symbolIcon-referenceForeground, var(--fg)); }
.local { color: var(--vscode-symbolIcon-variableForeground, var(--fg)); }
.type,.constructor { color: var(--vscode-symbolIcon-classForeground, var(--fg)); }
.field,.layout { color: var(--vscode-symbolIcon-propertyForeground, var(--fg)); }
.literal { color: var(--vscode-symbolIcon-booleanForeground, var(--fg)); }
.number { color: var(--vscode-symbolIcon-numberForeground, var(--fg)); }
@media (max-width: 1030px) {
    .panes { flex-direction: column; }
    .detail { flex: 0 1 auto; max-height: 27vh; border-inline-start: 0; border-top: 1px solid var(--line); }
}
@media (max-width: 660px) {
    .masthead { grid-template-columns: minmax(0, 1fr); padding: 8px 10px; }
    .facts .explanation { margin-inline-start: 0; }
    .workspace { flex-direction: column; }
    .outline { flex: 0 1 auto; max-height: 24vh; border-inline-end: 0; border-bottom: 1px solid var(--line); }
    .toolbar { gap: 4px 8px; padding: 5px; }
}
`;

module.exports = { styles };
