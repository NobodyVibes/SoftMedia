import { describe, it, expect } from 'vitest';
import { mergeSettingsPreservingEdits, type AppSetting } from './settingsService';

const s = (key: string, value: string, group = 'General'): AppSetting => ({ key, value, group });

/**
 * R-WI-010 regression guard. The DLNA card and the settings page share the ['settings'] query and
 * the page is NOT remounted across section navigation, so a card save that invalidates ['settings']
 * must not silently revert an admin's unsaved edits in another group. mergeSettingsPreservingEdits
 * is the 3-way merge that prevents that clobber.
 */
describe('mergeSettingsPreservingEdits', () => {
    it('adopts the server copy wholesale on first load (no prior snapshot)', () => {
        const server = [s('AllowUserSignup', 'false'), s('DlnaMaxContentRatings', '', 'DLNA')];
        expect(mergeSettingsPreservingEdits(null, server, [])).toEqual(server);
    });

    it('keeps an unsaved edit when a DIFFERENT key changed on the server (the reported clobber)', () => {
        // Admin toggled signup on locally (not yet saved) ...
        const prevServer = [s('AllowUserSignup', 'false'), s('DlnaMaxContentRatings', '', 'DLNA')];
        const local = [s('AllowUserSignup', 'true'), s('DlnaMaxContentRatings', '', 'DLNA')];
        // ... then the DLNA card saved, so ONLY the DLNA key changed server-side.
        const nextServer = [s('AllowUserSignup', 'false'), s('DlnaMaxContentRatings', '{"Movie":"PG-13"}', 'DLNA')];

        const merged = mergeSettingsPreservingEdits(prevServer, nextServer, local);
        const byKey = Object.fromEntries(merged.map(m => [m.key, m.value]));
        expect(byKey['AllowUserSignup']).toBe('true'); // unsaved edit preserved
        expect(byKey['DlnaMaxContentRatings']).toBe('{"Movie":"PG-13"}'); // server change picked up
    });

    it('adopts a server change for a key the admin did NOT edit', () => {
        const prevServer = [s('EnableTranscoding', 'true')];
        const local = [s('EnableTranscoding', 'true')];
        const nextServer = [s('EnableTranscoding', 'false')]; // changed elsewhere
        expect(mergeSettingsPreservingEdits(prevServer, nextServer, local)[0].value).toBe('false');
    });

    it('includes brand-new server keys and drops keys the server removed', () => {
        const prevServer = [s('A', '1'), s('Gone', 'x')];
        const local = [s('A', '1'), s('Gone', 'x')];
        const nextServer = [s('A', '1'), s('New', '2')];
        const keys = mergeSettingsPreservingEdits(prevServer, nextServer, local).map(m => m.key).sort();
        expect(keys).toEqual(['A', 'New']);
    });

    it('refreshes metadata (description/group) even while keeping the local value', () => {
        const prevServer = [s('A', '1')];
        const local = [{ ...s('A', '99'), description: 'old' }];
        const nextServer = [{ ...s('A', '1'), description: 'new', group: 'DLNA' }];
        const merged = mergeSettingsPreservingEdits(prevServer, nextServer, local);
        expect(merged[0].value).toBe('99'); // local edit kept
        expect(merged[0].description).toBe('new'); // fresh metadata
        expect(merged[0].group).toBe('DLNA');
    });
});
