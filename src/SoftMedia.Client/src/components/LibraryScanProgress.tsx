import { CheckCircle, Loader2, AlertCircle, Clock, AlertTriangle, FolderSearch } from 'lucide-react';
import type { LibraryScanJob, LibraryScanStatus } from '../types';

interface LibraryScanProgressProps {
    job: LibraryScanJob;
    compact?: boolean;
}

const statusConfig: Record<LibraryScanStatus, { icon: React.ReactNode; color: string; bgColor: string; label: string }> = {
    Queued: {
        icon: <Clock className="w-4 h-4" />,
        color: 'text-yellow-400',
        bgColor: 'bg-yellow-500/10 border-yellow-500/20',
        label: 'Queued'
    },
    Running: {
        icon: <Loader2 className="w-4 h-4 animate-spin" />,
        color: 'text-blue-400',
        bgColor: 'bg-blue-500/10 border-blue-500/20',
        label: 'Scanning'
    },
    Completed: {
        icon: <CheckCircle className="w-4 h-4" />,
        color: 'text-green-400',
        bgColor: 'bg-green-500/10 border-green-500/20',
        label: 'Completed'
    },
    Failed: {
        icon: <AlertCircle className="w-4 h-4" />,
        color: 'text-red-400',
        bgColor: 'bg-red-500/10 border-red-500/20',
        label: 'Failed'
    },
    Cancelled: {
        icon: <AlertTriangle className="w-4 h-4" />,
        color: 'text-gray-400',
        bgColor: 'bg-gray-500/10 border-gray-500/20',
        label: 'Cancelled'
    }
};

export function LibraryScanProgress({ job, compact = false }: LibraryScanProgressProps) {
    const config = statusConfig[job.status];

    // Calculate actual progress percentage based on files processed
    const actualProgress = job.totalFiles > 0
        ? Math.round((job.processedFiles / job.totalFiles) * 100)
        : 0;

    if (compact) {
        return (
            <div className={`flex items-center gap-2 px-3 py-2 rounded-lg border ${config.bgColor}`}>
                <span className={config.color}>{config.icon}</span>
                <span className="text-sm text-white font-medium truncate max-w-[140px]">
                    {job.libraryName}
                </span>
                {job.status === 'Running' && job.totalFiles > 0 && (
                    <span className="text-xs text-gray-400 ml-auto">
                        {job.processedFiles}/{job.totalFiles}
                    </span>
                )}
                {job.status === 'Queued' && job.queuePosition > 0 && (
                    <span className="px-2 py-0.5 bg-yellow-500/20 text-yellow-300 text-xs rounded-full ml-auto">
                        #{job.queuePosition}
                    </span>
                )}
                {job.status === 'Completed' && (
                    <span className="text-xs text-green-400 ml-auto">
                        +{job.newItems} new
                    </span>
                )}
            </div>
        );
    }

    return (
        <div className={`rounded-xl border ${config.bgColor} p-4 space-y-3`}>
            {/* Header */}
            <div className="flex items-center justify-between">
                <div className="flex items-center gap-3">
                    <span className={`${config.color}`}>{config.icon}</span>
                    <div>
                        <h4 className="text-white font-medium">{job.libraryName}</h4>
                        <p className={`text-sm ${config.color}`}>
                            {job.status === 'Queued' && job.queuePosition > 0
                                ? `Waiting (position #${job.queuePosition})`
                                : config.label}
                        </p>
                    </div>
                </div>

                {/* Show file count for running scans */}
                {job.status === 'Running' && job.totalFiles > 0 && (
                    <div className="text-right">
                        <span className="text-xl font-bold text-white">
                            {job.processedFiles}
                        </span>
                        <span className="text-gray-400 text-sm"> / {job.totalFiles}</span>
                    </div>
                )}
            </div>

            {/* Progress Bar - only show when actually scanning with file counts */}
            {job.status === 'Running' && job.totalFiles > 0 && (
                <div className="space-y-1">
                    <div className="h-2 bg-white/10 rounded-full overflow-hidden">
                        <div
                            className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full transition-all duration-300"
                            style={{ width: `${actualProgress}%` }}
                        />
                    </div>
                    <div className="text-xs text-gray-500 text-right">
                        {actualProgress}%
                    </div>
                </div>
            )}

            {/* Discovery phase - no file count yet */}
            {job.status === 'Running' && job.totalFiles === 0 && (
                <div className="flex items-center gap-2 text-sm text-gray-400">
                    <FolderSearch className="w-4 h-4 animate-pulse" />
                    <span>Discovering files...</span>
                </div>
            )}

            {/* Current File */}
            {job.status === 'Running' && job.currentFile && (
                <div className="text-xs text-gray-500 truncate">
                    → {job.currentFile}
                </div>
            )}

            {/* Final Stats - only show when completed */}
            {job.status === 'Completed' && (
                <div className="flex items-center gap-4 text-sm">
                    <span className="text-green-400">
                        <span className="font-medium">{job.newItems}</span> new
                    </span>
                    {job.updatedItems > 0 && (
                        <span className="text-blue-400">
                            <span className="font-medium">{job.updatedItems}</span> updated
                        </span>
                    )}
                    {job.skippedItems > 0 && (
                        <span className="text-gray-400">
                            <span className="font-medium">{job.skippedItems}</span> skipped
                        </span>
                    )}
                    {job.errorCount > 0 && (
                        <span className="text-red-400">
                            <span className="font-medium">{job.errorCount}</span> errors
                        </span>
                    )}
                </div>
            )}

            {/* Error Message */}
            {job.status === 'Failed' && job.errorMessage && (
                <div className="p-3 bg-red-500/10 border border-red-500/20 rounded-lg">
                    <p className="text-sm text-red-300">{job.errorMessage}</p>
                </div>
            )}
        </div>
    );
}
