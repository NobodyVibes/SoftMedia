import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import PlayVersionMenu from './PlayVersionMenu';
import { adminService } from '../../services/adminService';
import { useAuthStore } from '../../store/authStore';
import type { MediaItem } from '../../types';

vi.mock('../../services/adminService', () => ({
    adminService: {
        setPreferredVersion: vi.fn(),
    },
}));
vi.mock('react-i18next', () => ({
    useTranslation: () => ({
        t: (key: string, opts?: Record<string, unknown>) =>
            opts?.label ? key.replace('{{label}}', String(opts.label)) : key,
    }),
}));

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => ({
    ...(await importOriginal<typeof import('react-router-dom')>()),
    useNavigate: () => mockNavigate,
}));

const mocked = vi.mocked(adminService);

// The page shows the 4K primary; a watched 1080p sibling exists.
const baseItem = {
    id: 'v-4k',
    title: 'Goldmember',
    type: 'Movie',
    versionCount: 2,
    versions: [
        {
            id: 'v-4k', label: '4K HDR10', height: 2160, size: 20 * 1024 ** 3,
            container: 'mkv', isPrimary: true, preferred: false, watched: false,
        },
        {
            id: 'v-hd', label: '1080p', height: 1080, size: 4 * 1024 ** 3,
            container: 'mp4', isPrimary: false, preferred: false, watched: true,
        },
    ],
} as unknown as MediaItem;

function renderMenu(item: MediaItem = baseItem) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MemoryRouter>
                <PlayVersionMenu item={item} />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ user: null } as never);
});

describe('PlayVersionMenu', () => {
    it('renders as a chevron segment with the menu closed', () => {
        renderMenu();

        const trigger = screen.getByRole('button', { name: 'Play a specific version' });
        expect(trigger).toHaveAttribute('aria-expanded', 'false');
        expect(screen.queryByRole('menu')).not.toBeInTheDocument();
    });

    it('opens the menu listing every copy with Default marker and watched tick', () => {
        renderMenu();
        fireEvent.click(screen.getByRole('button', { name: 'Play a specific version' }));

        expect(screen.getByRole('menu')).toBeInTheDocument();
        expect(screen.getByText('1080p')).toBeInTheDocument();
        expect(screen.getByText('Default')).toBeInTheDocument();   // computed primary
        expect(screen.getByLabelText('Watched')).toBeInTheDocument(); // the 1080p copy only
    });

    it('plays the copy that was picked', () => {
        renderMenu();
        fireEvent.click(screen.getByRole('button', { name: 'Play a specific version' }));
        fireEvent.click(screen.getByRole('menuitem', { name: 'Play 1080p version' }));

        expect(mockNavigate).toHaveBeenCalledWith('/play/v-hd');
    });

    it('self-hides for single-file items and hides the prefer star from non-admins', () => {
        const { container } = renderMenu({ ...baseItem, versions: undefined } as MediaItem);
        expect(container).toBeEmptyDOMElement();

        renderMenu();
        fireEvent.click(screen.getByRole('button', { name: 'Play a specific version' }));
        expect(screen.queryByTitle('Prefer this version')).not.toBeInTheDocument();
    });

    it('lets an admin pin a preferred version without triggering playback', async () => {
        useAuthStore.setState({ user: { role: 'Admin' } } as never);
        mocked.setPreferredVersion.mockResolvedValue();

        renderMenu();
        fireEvent.click(screen.getByRole('button', { name: 'Play a specific version' }));
        fireEvent.click(screen.getAllByTitle('Prefer this version')[1]);

        await waitFor(() =>
            expect(mocked.setPreferredVersion).toHaveBeenCalledWith('v-hd', true));
        expect(mockNavigate).not.toHaveBeenCalled(); // star click must not also play
    });
});

