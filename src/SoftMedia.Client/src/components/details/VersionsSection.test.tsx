import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import VersionsSection from './VersionsSection';
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

const baseItem = {
    id: 'movie-1',
    title: 'Tenet',
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

function renderSection(item: MediaItem = baseItem) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MemoryRouter>
                <VersionsSection item={item} />
            </MemoryRouter>
        </QueryClientProvider>
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    useAuthStore.setState({ user: null } as never);
});

describe('VersionsSection', () => {
    it('lists every copy primary-first with label, default marker and per-copy watched state', () => {
        renderSection();

        expect(screen.getByText('4K HDR10')).toBeInTheDocument();
        expect(screen.getByText('1080p')).toBeInTheDocument();
        expect(screen.getByText('Default')).toBeInTheDocument(); // computed primary marked
        expect(screen.getByText('Watched')).toBeInTheDocument(); // only the 1080p copy
    });

    it('plays the specific copy that was clicked', () => {
        renderSection();

        fireEvent.click(screen.getByRole('button', { name: 'Play 1080p version' }));
        expect(mockNavigate).toHaveBeenCalledWith('/play/v-hd');
    });

    it('self-hides for single-file items and shows no prefer toggle to non-admins', () => {
        const { container } = renderSection({ ...baseItem, versions: undefined } as MediaItem);
        expect(container).toBeEmptyDOMElement();

        renderSection();
        expect(screen.queryByTitle('Prefer this version')).not.toBeInTheDocument();
    });

    it('lets an admin pin a preferred version', async () => {
        useAuthStore.setState({ user: { role: 'Admin' } } as never);
        mocked.setPreferredVersion.mockResolvedValue();

        renderSection();
        fireEvent.click(screen.getAllByTitle('Prefer this version')[1]);

        await waitFor(() =>
            expect(mocked.setPreferredVersion).toHaveBeenCalledWith('v-hd', true));
    });
});
