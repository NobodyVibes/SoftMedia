import { describe, it, expect } from 'vitest';
import { bookReadLabel } from './bookReadLabel';

// The read action moved to the detail page's primary (sidebar) button, so this label
// logic is shared rather than living inside BookDetailView. Pin the format split:
// PDF/CBZ resume on a page number, EPUB on an opaque CFI location.
describe('bookReadLabel', () => {
    it('says Read Now with no progress at all', () => {
        expect(bookReadLabel('epub', null)).toBe('Read Now');
        expect(bookReadLabel('pdf', undefined)).toBe('Read Now');
    });

    it('treats page 1 as the start, not a resume', () => {
        expect(bookReadLabel('pdf', { position: 1, bookLocation: null })).toBe('Read Now');
        expect(bookReadLabel('cbz', { position: 1, bookLocation: null })).toBe('Read Now');
    });

    it('names the page for paged formats', () => {
        expect(bookReadLabel('pdf', { position: 42, bookLocation: null })).toBe('Continue from page 42');
        expect(bookReadLabel('cbz', { position: 7.9, bookLocation: null })).toBe('Continue from page 7');
    });

    it('uses the CFI location for reflowable EPUBs (no page numbers exist)', () => {
        expect(bookReadLabel('epub', { position: 0, bookLocation: 'epubcfi(/6/4!/4/2)' })).toBe('Continue Reading');
        expect(bookReadLabel('epub', { position: 12, bookLocation: null })).toBe('Read Now');
    });

    it('is case-insensitive and survives a missing container', () => {
        expect(bookReadLabel('PDF', { position: 5, bookLocation: null })).toBe('Continue from page 5');
        expect(bookReadLabel(null, { position: 5, bookLocation: null })).toBe('Read Now');
    });
});
