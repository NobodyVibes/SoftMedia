import { render, screen, fireEvent } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { FixMatchCard } from './FixMatchCard';
import type { MediaItem } from '../../types';

// The modal interactions under test never fire a mutation, but FixMatchCard imports these —
// mock them so rendering doesn't reach the real service / toast layer.
vi.mock('../../services/adminService', () => ({
    adminService: {
        searchMatch: vi.fn(),
        applyMatch: vi.fn(),
        manualEditMatch: vi.fn(),
        unlockMatch: vi.fn(),
    },
}));
vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const item = { id: 'm1', title: 'Test Movie', metadataLocked: false } as unknown as MediaItem;

function renderCard() {
    const queryClient = new QueryClient({
        defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
        <QueryClientProvider client={queryClient}>
            <FixMatchCard item={item} />
        </QueryClientProvider>,
    );
}

beforeEach(() => vi.clearAllMocks());

// Guards the accessibility refactor of the modal: the click-outside backdrop is a real <button>
// (not a <div onClick>), role="dialog" lives on the content, and closing works via the backdrop
// button + Escape, while clicks inside the content do not dismiss it.
describe('FixMatchCard modal (accessible refactor)', () => {
    it('opens the dialog from the trigger and exposes an accessible backdrop button', () => {
        renderCard();
        expect(screen.queryByRole('dialog')).toBeNull();

        fireEvent.click(screen.getByRole('button', { name: 'Fix match' }));

        expect(screen.getByRole('dialog')).toBeInTheDocument();
        expect(screen.getByRole('button', { name: 'Close dialog' })).toBeInTheDocument();
    });

    it('closes when the backdrop button is clicked', () => {
        renderCard();
        fireEvent.click(screen.getByRole('button', { name: 'Fix match' }));

        fireEvent.click(screen.getByRole('button', { name: 'Close dialog' }));

        expect(screen.queryByRole('dialog')).toBeNull();
    });

    it('does NOT close when clicking inside the dialog content', () => {
        renderCard();
        fireEvent.click(screen.getByRole('button', { name: 'Fix match' }));

        fireEvent.click(screen.getByRole('dialog'));
        fireEvent.click(screen.getByRole('heading', { name: 'Fix match' }));

        expect(screen.getByRole('dialog')).toBeInTheDocument();
    });

    it('closes on Escape', () => {
        renderCard();
        fireEvent.click(screen.getByRole('button', { name: 'Fix match' }));

        fireEvent.keyDown(document.body, { key: 'Escape' });

        expect(screen.queryByRole('dialog')).toBeNull();
    });
});
