import type { HighlightColour } from '../../services/bookService';

/**
 * The highlight colour palette, shared by HighlightsDrawer (the picker),
 * BookReader (painting stored highlights), and PdfHighlightOverlay.
 *
 * Its own module rather than an export of HighlightsDrawer: a component file
 * that also exports plain values defeats Fast Refresh for the whole module.
 */
export const COLOUR_PALETTE: { value: HighlightColour; label: string; swatch: string }[] = [
    { value: 'yellow', label: 'Yellow', swatch: '#fde68a' },
    { value: 'green', label: 'Green', swatch: '#a7f3d0' },
    { value: 'blue', label: 'Blue', swatch: '#bfdbfe' },
    { value: 'pink', label: 'Pink', swatch: '#fbcfe8' },
    { value: 'orange', label: 'Orange', swatch: '#fed7aa' },
];

/**
 * Looks up the swatch colour for a stored highlight value. Unknown values
 * (e.g., a user migrating from a future palette) fall back to yellow so the
 * list still renders.
 */
export function swatchFor(colour: string): string {
    return COLOUR_PALETTE.find((p) => p.value === colour)?.swatch ?? '#fde68a';
}
