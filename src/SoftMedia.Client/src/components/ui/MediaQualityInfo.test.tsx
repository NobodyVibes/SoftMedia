import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import MediaQualityInfo from './MediaQualityInfo';
import api from '../../services/api';
import type { MediaItem } from '../../types';

vi.mock('../../services/api', () => ({
    default: { get: vi.fn() },
}));

const mockedGet = vi.mocked(api.get);

// The page shows the 4K HEVC/Atmos primary; a 1080p H264/AC3 sibling exists.
const primary = {
    id: 'v-4k',
    type: 'Movie',
    title: 'Goldmember',
    width: 3840, height: 1608, hdrFormat: 'HDR10', bitDepth: 10,
    videoCodec: 'hevc', audioCodec: 'truehd', audioChannels: 8, bitrate: 25_000_000,
    versions: [
        { id: 'v-4k', label: '4K HDR10', size: 1, isPrimary: true, preferred: false, watched: false },
        { id: 'v-hd', label: '1080p', size: 1, isPrimary: false, preferred: false, watched: false },
    ],
} as unknown as MediaItem;

const sibling = {
    id: 'v-hd',
    type: 'Movie',
    title: 'Goldmember',
    width: 1920, height: 816, bitDepth: 8,
    videoCodec: 'h264', audioCodec: 'ac3', audioChannels: 6, bitrate: 8_000_000,
} as unknown as MediaItem;

function renderPanel(item: MediaItem = primary, extra?: {
    selectedVersionId?: string | null;
    onVersionSelect?: (id: string) => void;
}) {
    const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return render(
        <QueryClientProvider client={qc}>
            <MediaQualityInfo item={item} {...extra} />
        </QueryClientProvider>
    );
}

beforeEach(() => {
    vi.clearAllMocks();
    mockedGet.mockResolvedValue({ data: sibling });
});

describe('MediaQualityInfo version inspection', () => {
    it('the Video value is a version dropdown only for multi-copy titles', () => {
        const { unmount } = renderPanel();
        const picker = screen.getByLabelText('Video version');
        expect(picker).toHaveValue('v-4k'); // current copy selected
        expect(screen.getByRole('option', { name: '4K HDR10 [Default]' })).toBeInTheDocument();
        unmount();

        renderPanel({ ...primary, id: 'single', versions: undefined } as MediaItem);
        expect(screen.queryByLabelText('Video version')).not.toBeInTheDocument();
        expect(screen.getByText('4K HDR10')).toBeInTheDocument(); // plain text value instead
    });

    it('renders the current copy specs by default (no fetch)', () => {
        renderPanel();

        expect(screen.getByText('(HEVC)')).toBeInTheDocument();
        expect(screen.getByText('10-bit')).toBeInTheDocument();
        expect(screen.getByText('7.1 Atmos')).toBeInTheDocument();
        expect(mockedGet).not.toHaveBeenCalled();
    });

    it('swaps the whole panel to the picked copys metadata (uncontrolled)', async () => {
        renderPanel();

        fireEvent.change(screen.getByLabelText('Video version'), { target: { value: 'v-hd' } });

        await waitFor(() => expect(screen.getByText('(H264)')).toBeInTheDocument());
        expect(mockedGet).toHaveBeenCalledWith('/media/v-hd');
        expect(screen.getByText('8-bit')).toBeInTheDocument();
        expect(screen.getByText('5.1 Dolby Digital')).toBeInTheDocument();
    });

    it('controlled mode reports picks upward and follows the prop', async () => {
        const onVersionSelect = vi.fn();
        const { rerender } = renderPanel(primary, { selectedVersionId: null, onVersionSelect });

        fireEvent.change(screen.getByLabelText('Video version'), { target: { value: 'v-hd' } });
        expect(onVersionSelect).toHaveBeenCalledWith('v-hd'); // page owns the state (Play target)
        expect(mockedGet).not.toHaveBeenCalled();             // nothing fetched until the prop moves

        const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
        rerender(
            <QueryClientProvider client={qc}>
                <MediaQualityInfo item={primary} selectedVersionId="v-hd" onVersionSelect={onVersionSelect} />
            </QueryClientProvider>
        );
        await waitFor(() => expect(screen.getByText('(H264)')).toBeInTheDocument());
    });

    it('switching back to the current copy needs no fetch (uncontrolled)', async () => {
        renderPanel();
        const picker = screen.getByLabelText('Video version');

        fireEvent.change(picker, { target: { value: 'v-hd' } });
        await waitFor(() => expect(screen.getByText('(H264)')).toBeInTheDocument());
        fireEvent.change(picker, { target: { value: 'v-4k' } });

        expect(screen.getByText('(HEVC)')).toBeInTheDocument();
        expect(mockedGet).toHaveBeenCalledTimes(1);
    });
});
