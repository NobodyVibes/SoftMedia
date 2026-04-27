import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import PdfHighlightOverlay from './PdfHighlightOverlay';
import type { Highlight } from '../../services/bookService';

function pdfHighlight(page: number, rects: Array<{ x: number; y: number; w: number; h: number }>): Highlight {
    return {
        id: `h-${page}-${rects.map((r) => r.x).join('-')}`,
        locationJson: JSON.stringify({ type: 'pdf', page, rects }),
        colour: 'yellow',
        quotedText: 'A striking passage',
        note: null,
        createdAt: '2026-04-20',
        updatedAt: '2026-04-20',
    };
}

describe('PdfHighlightOverlay', () => {
    it('paints one rect per stored rect on the matching page', () => {
        const highlights = [
            pdfHighlight(3, [
                { x: 0.1, y: 0.1, w: 0.8, h: 0.05 },
                { x: 0.1, y: 0.16, w: 0.6, h: 0.05 },
            ]),
            pdfHighlight(4, [{ x: 0, y: 0, w: 1, h: 0.05 }]),
        ];

        const { container } = render(
            <PdfHighlightOverlay highlights={highlights} pageNumber={3} />,
        );
        // Two rects for page 3; page-4 highlight is filtered out.
        expect(container.querySelectorAll('button[aria-label^="Highlight"]').length).toBe(2);
    });

    it('renders nothing when no highlights exist on this page', () => {
        const { container } = render(
            <PdfHighlightOverlay highlights={[pdfHighlight(1, [{ x: 0, y: 0, w: 1, h: 0.1 }])]} pageNumber={2} />,
        );
        expect(container.firstChild).toBeNull();
    });

    it('skips highlights with no rects (pre-polish schema) gracefully', () => {
        const h: Highlight = {
            id: 'old',
            locationJson: JSON.stringify({ type: 'pdf', page: 1 }), // no rects
            colour: 'blue',
            quotedText: 'pre-overlay highlight',
            note: null,
            createdAt: '2026-04-19',
            updatedAt: '2026-04-19',
        };
        const { container } = render(
            <PdfHighlightOverlay highlights={[h]} pageNumber={1} />,
        );
        // Overlay returns null when no paintable rects exist for the page.
        expect(container.firstChild).toBeNull();
    });

    it('fires onClick with the full highlight when a rect is clicked', () => {
        const onClick = vi.fn();
        const h = pdfHighlight(5, [{ x: 0.2, y: 0.3, w: 0.4, h: 0.05 }]);
        render(
            <PdfHighlightOverlay highlights={[h]} pageNumber={5} onClick={onClick} />,
        );
        fireEvent.click(screen.getByLabelText(/highlight/i));
        expect(onClick).toHaveBeenCalledTimes(1);
        expect(onClick.mock.calls[0][0]).toMatchObject({ id: h.id });
    });

    it('positions rects as percentages of the container so zoom-resize tracks', () => {
        const h = pdfHighlight(2, [{ x: 0.25, y: 0.5, w: 0.5, h: 0.1 }]);
        render(
            <PdfHighlightOverlay highlights={[h]} pageNumber={2} />,
        );
        const btn = screen.getByLabelText(/highlight/i) as HTMLButtonElement;
        expect(btn.style.left).toBe('25%');
        expect(btn.style.top).toBe('50%');
        expect(btn.style.width).toBe('50%');
        expect(btn.style.height).toBe('10%');
    });
});
