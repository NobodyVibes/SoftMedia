import { render, screen, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { CacheUsageCard } from './CacheUsageCard';
import { adminService, type CacheAreaStats } from '../../services/adminService';

vi.mock('../../services/adminService', () => ({
    adminService: {
        getCacheStats: vi.fn(),
    },
}));
vi.mock('react-i18next', () => ({
    useTranslation: () => ({ t: (key: string) => key }),
}));

const mocked = vi.mocked(adminService);

const stats: CacheAreaStats[] = [
    { area: 'Trickplay', files: 9383, bytes: 4 * 1024 ** 3 }, // 4 GB
    { area: 'Subtitles', files: 12, bytes: 50_000 },
    { area: 'Image proxy', files: 0, bytes: 0 },
];

function renderCard() {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(<QueryClientProvider client={qc}><CacheUsageCard /></QueryClientProvider>);
}

beforeEach(() => {
    vi.clearAllMocks();
    mocked.getCacheStats.mockResolvedValue(stats);
});

describe('CacheUsageCard', () => {
    it('renders one row per area with human-readable sizes and a total', async () => {
        renderCard();

        await waitFor(() => expect(screen.getByText('Trickplay')).toBeInTheDocument());
        expect(screen.getByText('9,383')).toBeInTheDocument();
        // "4.0 GB" appears twice: the Trickplay row and the Total row (4 GB + 50 KB
        // rounds to the same label).
        expect(screen.getAllByText('4.0 GB')).toHaveLength(2);
        expect(screen.getByText('Subtitles')).toBeInTheDocument();
        // Zero-byte area still gets a row (visibility of empty areas is the point).
        expect(screen.getByText('Image proxy')).toBeInTheDocument();

        // Total row aggregates files and bytes.
        expect(screen.getByText('Total')).toBeInTheDocument();
        expect(screen.getByText('9,395')).toBeInTheDocument();
    });

    it('points the admin at the cleanup task for reclaiming space', async () => {
        renderCard();

        await waitFor(() => expect(screen.getByText('Trickplay')).toBeInTheDocument());
        expect(screen.getByText(/Image Cache Cleanup/)).toBeInTheDocument();
    });
});
