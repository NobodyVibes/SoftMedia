import { render, screen, fireEvent, act } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { SkipSegmentPill } from './SkipSegmentPill';

describe('SkipSegmentPill', () => {
    beforeEach(() => {
        vi.useFakeTimers();
    });

    afterEach(() => {
        vi.useRealTimers();
    });

    it('does not render when not visible', () => {
        const { container } = render(
            <SkipSegmentPill label="Skip Intro" visible={false} onSkip={() => {}} />
        );
        expect(container).toBeEmptyDOMElement();
    });

    it('renders the label and a keyboard hint when visible', () => {
        render(<SkipSegmentPill label="Skip Intro" visible={true} onSkip={() => {}} />);

        expect(screen.getByRole('button', { name: 'Skip Intro' })).toBeInTheDocument();
        expect(screen.getByText('S')).toBeInTheDocument();
    });

    it('uses ariaLabel override when provided', () => {
        render(
            <SkipSegmentPill
                label="Skip Credits"
                ariaLabel="Skip end credits and continue"
                visible={true}
                onSkip={() => {}}
            />
        );

        expect(
            screen.getByRole('button', { name: 'Skip end credits and continue' })
        ).toBeInTheDocument();
    });

    it('fires onSkip when clicked', () => {
        const onSkip = vi.fn();
        render(<SkipSegmentPill label="Skip Intro" visible={true} onSkip={onSkip} />);

        fireEvent.click(screen.getByRole('button'));

        expect(onSkip).toHaveBeenCalledTimes(1);
    });

    it('fires onSkip when the S key is pressed', () => {
        const onSkip = vi.fn();
        render(<SkipSegmentPill label="Skip Intro" visible={true} onSkip={onSkip} />);

        fireEvent.keyDown(window, { key: 's' });

        expect(onSkip).toHaveBeenCalledTimes(1);
    });

    it('ignores S key while typing in an input field', () => {
        const onSkip = vi.fn();
        render(
            <>
                <input data-testid="search-box" />
                <SkipSegmentPill label="Skip Intro" visible={true} onSkip={onSkip} />
            </>
        );

        const input = screen.getByTestId('search-box') as HTMLInputElement;
        input.focus();
        fireEvent.keyDown(input, { key: 's' });

        expect(onSkip).not.toHaveBeenCalled();
    });

    it('auto-fades after 8 seconds', () => {
        render(<SkipSegmentPill label="Skip Intro" visible={true} onSkip={() => {}} />);

        expect(screen.getByRole('button')).toBeInTheDocument();

        act(() => {
            vi.advanceTimersByTime(8000);
        });

        expect(screen.queryByRole('button')).not.toBeInTheDocument();
    });

    it('reappears when visible flips false then true', () => {
        const { rerender } = render(
            <SkipSegmentPill label="Skip Intro" visible={true} onSkip={() => {}} />
        );

        // Auto-hide after 8s
        act(() => { vi.advanceTimersByTime(8000); });
        expect(screen.queryByRole('button')).not.toBeInTheDocument();

        // Parent flips visible to false (segment exited)
        rerender(<SkipSegmentPill label="Skip Intro" visible={false} onSkip={() => {}} />);
        expect(screen.queryByRole('button')).not.toBeInTheDocument();

        // Parent flips visible back to true (re-entered segment via seek)
        rerender(<SkipSegmentPill label="Skip Intro" visible={true} onSkip={() => {}} />);
        expect(screen.getByRole('button')).toBeInTheDocument();
    });
});
