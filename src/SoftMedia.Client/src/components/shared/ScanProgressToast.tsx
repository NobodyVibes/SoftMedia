import { useEffect, useState } from 'react';
import { useScanProgressStore } from '../../store/scanProgressStore';
import { cn } from '../../lib/utils';
import { X, Loader2 } from 'lucide-react';
import { useLibraries } from '../../hooks/useLibrary';

export default function ScanProgressToast() {
    const { isScanning, libraryId, processed, total, status } = useScanProgressStore();
    const [isVisible, setIsVisible] = useState(false);
    const { data: libraries } = useLibraries();

    // Find library name
    const libraryName = libraries?.find(l => l.id === libraryId)?.name || 'Library';

    useEffect(() => {
        if (isScanning) {
            setIsVisible(true);
        } else {
            // Hide after a delay when complete
            const timer = setTimeout(() => setIsVisible(false), 3000);
            return () => clearTimeout(timer);
        }
    }, [isScanning]);

    if (!isVisible && !isScanning) return null;

    const percent = total > 0 ? Math.round((processed / total) * 100) : 0;

    return (
        <div className={cn(
            "fixed bottom-4 right-4 z-50 w-80 bg-gray-900 border border-gray-800 rounded-lg shadow-xl p-4 transition-all duration-300 transform",
            isVisible ? "translate-y-0 opacity-100" : "translate-y-4 opacity-0"
        )}>
            <div className="flex items-start justify-between">
                <div className="flex items-center gap-3">
                    <div className="p-2 bg-blue-500/10 rounded-full">
                        <Loader2 className="w-5 h-5 text-blue-400 animate-spin" />
                    </div>
                    <div>
                        <h4 className="text-sm font-medium text-white">Scanning {libraryName}</h4>
                        <p className="text-xs text-gray-400 mt-0.5">{status}</p>
                    </div>
                </div>
                <button
                    onClick={() => setIsVisible(false)}
                    className="text-gray-500 hover:text-white transition-colors"
                >
                    <X className="w-4 h-4" />
                </button>
            </div>

            <div className="mt-3">
                <div className="flex justify-between text-xs text-gray-400 mb-1">
                    <span>Progress</span>
                    <span>{percent}% ({processed}/{total})</span>
                </div>
                <div className="h-1.5 bg-gray-800 rounded-full overflow-hidden">
                    <div
                        className="h-full bg-blue-500 rounded-full transition-all duration-300 ease-out"
                        style={{ width: `${percent}%` }}
                    />
                </div>
            </div>
        </div>
    );
}
