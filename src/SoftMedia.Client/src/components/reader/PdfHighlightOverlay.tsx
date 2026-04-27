import { swatchFor } from './HighlightsDrawer';
import { parseHighlightLocation, type Highlight } from '../../services/bookService';

interface PdfHighlightOverlayProps {
    /** All highlights the reader has loaded. Filtering to this page happens here. */
    highlights: Highlight[];
    /** Current page number; only highlights anchored to this page paint. */
    pageNumber: number;
    /** Click-through callback: fired when a rect is clicked. */
    onClick?: (highlight: Highlight) => void;
}

/**
 * ER-040 polish: absolutely-positioned rectangular highlight overlay on a
 * PDF page. Stored rects are normalised 0–1 against the page's bounding
 * rect at capture time, so positioning here is a straight percentage — no
 * re-measure on zoom or resize. Rendered inside the same `.react-pdf__Page`
 * container so `position: absolute; inset: 0` fills the page exactly.
 *
 * Painted *below* the pdf.js text layer (`.react-pdf__Page__textContent`)
 * by using a lower z-index so text remains selectable for new highlights.
 */
export default function PdfHighlightOverlay({ highlights, pageNumber, onClick }: PdfHighlightOverlayProps) {
    const onThisPage = highlights.filter((h) => {
        const loc = parseHighlightLocation(h.locationJson);
        return loc?.type === 'pdf' && loc.page === pageNumber && loc.rects && loc.rects.length > 0;
    });

    if (onThisPage.length === 0) return null;

    return (
        <div
            className="absolute inset-0 pointer-events-none"
            style={{ zIndex: 1 }}
            aria-hidden
        >
            {onThisPage.flatMap((h) => {
                const loc = parseHighlightLocation(h.locationJson);
                if (loc?.type !== 'pdf' || !loc.rects) return [];
                const swatch = swatchFor(h.colour);
                return loc.rects.map((r, i) => (
                    <button
                        type="button"
                        key={`${h.id}-${i}`}
                        title={h.quotedText}
                        onClick={(e) => {
                            if (!onClick) return;
                            e.stopPropagation();
                            onClick(h);
                        }}
                        className="absolute pointer-events-auto border-0 p-0 cursor-pointer"
                        style={{
                            left: `${r.x * 100}%`,
                            top: `${r.y * 100}%`,
                            width: `${r.w * 100}%`,
                            height: `${r.h * 100}%`,
                            background: swatch,
                            opacity: 0.45,
                            // `mix-blend-mode: multiply` keeps the PDF text
                            // visible through the overlay instead of covering
                            // it with a flat colour block.
                            mixBlendMode: 'multiply',
                        }}
                        aria-label={`Highlight: ${h.quotedText.slice(0, 40)}`}
                    />
                ));
            })}
        </div>
    );
}
