'use strict';

// Bundled by value into the inspector WebView. Each completed result is an
// immutable sorted list of lines, usable as the input for a stricter query.
function createArtifactSearch(lines, now = () => performance.now(), schedule = callback => requestAnimationFrame(callback)) {
    let generation = 0, previousQuery = '', previousMatches = [];
    return (value, publish) => {
        const current = ++generation, query = value.toLowerCase();
        if (!query || query === previousQuery) {
            if (!query) { previousQuery = ''; previousMatches = []; }
            publish(previousMatches);
            return;
        }

        // Test the folded needles: Unicode case mapping can change when a
        // character is appended. Only an actual folded prefix proves a subset.
        const candidates = previousQuery && query.startsWith(previousQuery) ? previousMatches : undefined;
        const limit = candidates?.length ?? lines.length;
        const found = [];
        let cursor = 0;
        const scan = () => {
            if (current !== generation) return;
            const deadline = now() + 7;
            while (cursor < limit) {
                const line = candidates ? candidates[cursor] : cursor;
                if (lines[line].toLowerCase().includes(query)) found.push(line);
                if ((++cursor & 127) === 0 && cursor < limit && now() >= deadline) {
                    schedule(scan);
                    return;
                }
            }
            previousQuery = query;
            previousMatches = found;
            publish(found);
        };
        scan();
    };
}

module.exports = { createArtifactSearch };
