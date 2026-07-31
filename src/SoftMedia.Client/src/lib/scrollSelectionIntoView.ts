/**
 * Brings an auto-selected element into view by nudging the scroller it lives
 * in — and nothing else. Deliberately not `scrollIntoView`: the strips it
 * serves (TVDetailView's season/episode rails) usually sit below the fold on
 * load, and a resume selection must never yank the page around on arrival.
 * `boundary` (the section wrapper) stops the walk short of the page scroller,
 * so a strip that doesn't overflow simply does nothing instead of scrolling
 * the document to it.
 *
 * Its own module (not an export of TVDetailView) so the component file exports
 * only components and Fast Refresh keeps working there.
 */
export function scrollSelectionIntoView(el: HTMLElement, boundary: HTMLElement) {
    for (let c = el.parentElement; c && c !== boundary; c = c.parentElement) {
        const horizontal = c.scrollWidth > c.clientWidth + 1;
        const vertical = c.scrollHeight > c.clientHeight + 1;
        if (!horizontal && !vertical) continue;

        const er = el.getBoundingClientRect();
        const cr = c.getBoundingClientRect();
        if (horizontal) {
            c.scrollLeft += (er.left - cr.left) - (cr.width - er.width) / 2;
        } else {
            c.scrollTop += (er.top - cr.top) - (cr.height - er.height) / 2;
        }
        return;
    }
}
