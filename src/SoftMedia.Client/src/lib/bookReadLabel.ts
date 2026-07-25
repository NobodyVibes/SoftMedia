import type { BookProgress } from '../services/bookService';

/**
 * Label for a book's read action ("Read Now" / resume variants).
 *
 * Resume detection differs by format: PDF/CBZ track a page number (page 1 is the
 * start, so it is not a resume), while EPUB is reflowable and tracks an opaque
 * CFI location instead — a stored location is itself the resume signal.
 *
 * @param container file extension carried by the media DTO (SR-WI-063: `path` left
 *                  the DTO, `container` is the guaranteed extension for book items)
 */
export function bookReadLabel(
    container: string | null | undefined,
    // Only the two positional fields matter — accepting a narrowed shape keeps the
    // helper (and its tests) independent of the rest of the progress payload.
    progress: Pick<BookProgress, 'position' | 'bookLocation'> | null | undefined,
): string {
    const ext = (container ?? '').toLowerCase();
    const resumePage = progress && progress.position > 0 ? Math.floor(progress.position) : 0;
    const hasEpubResume = !!progress?.bookLocation;

    const showResume = (ext === 'pdf' || ext === 'cbz') ? resumePage > 1 : hasEpubResume;
    if (!showResume) return 'Read Now';
    return ext === 'epub' ? 'Continue Reading' : `Continue from page ${resumePage}`;
}
