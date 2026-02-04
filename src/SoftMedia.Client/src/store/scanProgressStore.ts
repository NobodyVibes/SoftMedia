import { create } from 'zustand';

interface ScanState {
    libraryId: string | null;
    libraryName: string | null;
    processed: number;
    total: number;
    status: string;
    isScanning: boolean;
}

interface ScanProgressStore extends ScanState {
    startScan: (libraryId: string, libraryName?: string) => void;
    updateProgress: (libraryId: string, processed: number, total: number, status: string) => void;
    stopScan: (libraryId: string) => void;
}

export const useScanProgressStore = create<ScanProgressStore>((set) => ({
    libraryId: null,
    libraryName: null,
    processed: 0,
    total: 0,
    status: '',
    isScanning: false,

    startScan: (libraryId, libraryName) => set({
        libraryId,
        libraryName: libraryName || 'Library',
        processed: 0,
        total: 0,
        status: 'Starting...',
        isScanning: true
    }),

    updateProgress: (libraryId, processed, total, status) => set((state) => {
        // Only update if it matches the current library or if we weren't scanning
        if (state.libraryId && state.libraryId !== libraryId) return state;
        return {
            libraryId,
            processed,
            total,
            status,
            isScanning: true
        };
    }),

    stopScan: (libraryId) => set((state) => {
        if (state.libraryId === libraryId) {
            return { isScanning: false, status: 'Complete' };
        }
        return state;
    }),
}));
