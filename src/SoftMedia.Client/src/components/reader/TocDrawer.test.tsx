import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi } from 'vitest';
import TocDrawer, { type TocItem } from './TocDrawer';

const items: TocItem[] = [
    { label: 'Chapter 1', href: 'ch1.xhtml' },
    {
        label: 'Chapter 2',
        href: 'ch2.xhtml',
        children: [
            { label: '2.1 Subsection', href: 'ch2.xhtml#a' },
            { label: '2.2 Another', href: 'ch2.xhtml#b' },
        ],
    },
    { label: 'Chapter 3', href: 'ch3.xhtml' },
];

describe('TocDrawer', () => {
    it('renders nested items when open', () => {
        render(
            <TocDrawer
                items={items}
                currentHref={null}
                open={true}
                onJump={() => {}}
                onClose={() => {}}
            />,
        );

        expect(screen.getByText('Chapter 1')).toBeInTheDocument();
        expect(screen.getByText('2.1 Subsection')).toBeInTheDocument();
        expect(screen.getByText('Chapter 3')).toBeInTheDocument();
    });

    it('fires onJump with the clicked item', () => {
        const onJump = vi.fn();
        render(
            <TocDrawer
                items={items}
                currentHref={null}
                open={true}
                onJump={onJump}
                onClose={() => {}}
            />,
        );

        fireEvent.click(screen.getByText('2.2 Another'));
        expect(onJump).toHaveBeenCalledTimes(1);
        expect(onJump.mock.calls[0][0]).toMatchObject({ label: '2.2 Another', href: 'ch2.xhtml#b' });
    });

    it('closes on Escape', () => {
        const onClose = vi.fn();
        render(
            <TocDrawer
                items={items}
                currentHref={null}
                open={true}
                onJump={() => {}}
                onClose={onClose}
            />,
        );

        fireEvent.keyDown(window, { key: 'Escape' });
        expect(onClose).toHaveBeenCalled();
    });

    it('shows empty state when items is empty', () => {
        render(
            <TocDrawer
                items={[]}
                currentHref={null}
                open={true}
                onJump={() => {}}
                onClose={() => {}}
            />,
        );
        expect(screen.getByText(/no table of contents/i)).toBeInTheDocument();
    });

    it('highlights the current chapter via hrefMatches base-path logic', () => {
        // `currentHref` comes with a fragment that the TOC href lacks — the
        // base-path match is what keeps highlighting stable mid-chapter.
        render(
            <TocDrawer
                items={items}
                currentHref="ch2.xhtml#mid-section"
                open={true}
                onJump={() => {}}
                onClose={() => {}}
            />,
        );
        const ch2 = screen.getByText('Chapter 2').closest('button')!;
        expect(ch2.className).toMatch(/bg-gradient-to-r/);
    });
});
