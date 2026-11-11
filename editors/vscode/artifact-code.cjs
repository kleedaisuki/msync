'use strict';

// Kept self-contained so the bundled extension can supply it to its isolated webview.
function createCodeRenderer() {
    function escape(value) {
        return String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[character]);
    }

    const CONTROL_OPERATIONS = new Set(['Jump', 'JumpIfTrue', 'JumpIfFalse', 'Return', 'IteratorNext', 'Barrier', 'Acquire',
        'If', 'Match', 'Loop', 'While', 'For', 'Break', 'Continue', 'Defer', 'DeferRange', 'Release']);
    const FIELD_OPERATIONS = new Set(['MakeScope', 'GetMember', 'SetMember', 'FieldGet', 'FieldSet', 'ScopeCreate',
        'Scope', 'MemberRead', 'MemberAssign']);

    function syntax(text, quotedRole = 'string') {
        const transfer = /^(\s*transfer\s+@p\d+n\d+\s+)([A-Za-z][A-Za-z0-9_]*)/.exec(text);
        if (transfer)
            return syntax(transfer[1]) + `<span class="opcode">${escape(transfer[2])}</span>` + syntax(text.slice(transfer[0].length));
        const operation = /^(\s*(?:\d+|@n\d+):\s+)([A-Za-z][A-Za-z0-9_]*)/.exec(text);
        if (operation) {
            const role = CONTROL_OPERATIONS.has(operation[2]) ? 'control' : 'opcode';
            const fields = FIELD_OPERATIONS.has(operation[2]) ? 'field' : quotedRole;
            return syntax(operation[1]) + `<span class="${role}">${escape(operation[2])}</span>` + syntax(text.slice(operation[0].length), fields);
        }
        const expression = /("(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(;.*$)|(\b(?:release|acquire|acqrel|before-and-after|before|after|region-start|join|if|match|loop|while|for|return|break|continue|defer)\b)|(\b(?:artifact|hir|context|execution_plan|transfer|batch|const|fn|block|entry|origin|format|language|optimization|semantics|captures|locals|successors|effects|pure|readonly|definition|binding|location|shape|template|instance|value|cell|mutable|closed|open)\b|\btype\b(?!\s*\())|(@(?:f|g)\d+)|([@%]c\d+)|(@b\d+_[0-9]+|\bb\d+\b|@n\d+|@p\d+(?:[nb]\d+)?)|(%\d+|@l\d+|\b(?:local|capture|arg)\d+\b)|(@t\d+|\b(?:Null|Bool|Number|String|Path|Scope|Function|Connection|Process|Range|Type|Never|Any)\b)|(@s\d+|\bpath(?=\s*\())|(\b(?:number|connection|closure|native|type|scope|range)(?=\s*[({]))|(\b(?:fields|slot|allocation-site|layout-checked|hint|raw|cwd|home|endpoint|bits|source|destination|read|write|predecessors|eliminated|subtree|exact|region|owner|transfers|item-type|items|result)\b)|(\b(?:null|true|false)\b)|((?<![A-Za-z0-9_])[+-]?(?:0x[0-9a-fA-F]+|(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?|Infinity|NaN)\b)/g;
        const roles = [quotedRole, 'comment', 'control', 'keyword', 'function', 'constant', 'block', 'local', 'type', 'path', 'constructor', 'layout', 'literal', 'number'];
        let result = '', last = 0;
        for (const match of text.matchAll(expression)) {
            result += escape(text.slice(last, match.index));
            let role;
            for (let index = 0; index < roles.length; index++)
                if (match[index + 1] !== undefined) { role = roles[index]; break; }
            result += `<span class="${role}">${escape(match[0])}</span>`;
            last = match.index + match[0].length;
        }
        return result + escape(text.slice(last));
    }

    return { escape, syntax };
}

module.exports = { createCodeRenderer };
