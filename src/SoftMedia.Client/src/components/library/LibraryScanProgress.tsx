import { CheckCircle, Loader2, AlertCircle, Clock, AlertTriangle, FolderSearch, Sparkles, PauseCircle } from 'lucide-react';
import type { LibraryScanJob, LibraryScanStatus } from '../../types';

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
    },
    Paused: {
        icon: <PauseCircle className="w-4 h-4" />,
        color: 'text-amber-400',
        bgColor: 'bg-amber-500/10 border-amber-500/20',
        label: 'Paused'
    }
};

/** Human label for a running job, derived from its type and stage. */
function runningLabel(job: LibraryScanJob): string {
    if (job.type === 'IntroCreditsDetection') return 'Detecting intros & credits';
    switch (job.stage) {
        case 'Discovery': return 'Discovering files';
        case 'Metadata': return 'Enriching metadata';
        case 'Finishing': return 'Finalizing';
        default: return 'Scanning';
    }
}

export function LibraryScanProgress({ job, compact = false }: LibraryScanProgressProps) {
    const config = statusConfig[job.status];
    const isRunning = job.status === 'Running';
    const isDetection = job.type === 'IntroCreditsDetection';
    const inMetadata = isRunning && !isDetection && job.stage === 'Metadata';
    const inDiscovery = isRunning && !isDetection && job.stage === 'Discovery';

    // File-walk progress. During Discovery totalFiles is still a growing count,
    // so only Processing/Finishing stages get a determinate percentage.
    const fileProgress = !inDiscovery && job.totalFiles > 0
        ? Math.min(100, Math.round((job.processedFiles / job.totalFiles) * 100))
        : 0;

    // Metadata-stage progress: how much of the enrichment backlog has drained.
    const metadataDone = Math.max(0, job.metadataTotal - job.metadataRemaining);
    const metadataProgress = job.metadataTotal > 0
        ? Math.round((metadataDone / job.metadataTotal) * 100)
        : 0;

    if (compact) {
        return (
            <div className={`flex items-center gap-2 px-3 py-2 rounded-lg border ${config.bgColor}`}>
                <span className={config.color}>{config.icon}</span>
                <span className="text-sm text-white font-medium truncate max-w-[140px]">
                    {job.libraryName}
                </span>
                {inMetadata && (
                    <span className="text-xs text-violet-300 ml-auto">
                        {job.metadataRemaining} to enrich
                    </span>
                )}
                {isRunning && !inMetadata && job.totalFiles > 0 && (
                    <span className="text-xs text-gray-400 ml-auto">
                        {inDiscovery ? `${job.totalFiles} found` : `${job.processedFiles}/${job.totalFiles}`}
                    </span>
                )}
                {job.status === 'Queued' && job.queuePosition > 0 && (
                    <span className="px-2 py-0.5 bg-yellow-500/20 text-yellow-300 text-xs rounded-full ml-auto">
                        #{job.queuePosition}
                    </span>
                )}
                {job.status === 'Completed' && (
                    <span className="text-xs text-green-400 ml-auto">
                        {isDetection ? `${job.updatedItems} found` : `+${job.newItems} new`}
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
                                : isRunning ? runningLabel(job) : config.label}
                        </p>
                    </div>
                </div>

                {/* Show file count for running scans */}
                {isRunning && !inMetadata && !inDiscovery && job.totalFiles > 0 && (
                    <div className="text-right">
                        <span className="text-xl font-bold text-white">
                            {job.processedFiles}
                        </span>
                        <span className="text-gray-400 text-sm"> / {job.totalFiles}</span>
                    </div>
                )}
                {inMetadata && job.metadataTotal > 0 && (
                    <div className="text-right">
                        <span className="text-xl font-bold text-white">
                            {metadataDone}
                        </span>
                        <span className="text-gray-400 text-sm"> / {job.metadataTotal}</span>
                    </div>
                )}
            </div>

            {/* Discovery phase — file total still growing */}
            {inDiscovery && (
                <div className="flex items-center gap-2 text-sm text-gray-400">
                    <FolderSearch className="w-4 h-4 animate-pulse" />
                    <span>Discovering files{job.totalFiles > 0 ? ` — ${job.totalFiles} found` : '...'}</span>
                </div>
            )}

            {/* File-walk progress bar */}
            {isRunning && !inMetadata && !inDiscovery && job.totalFiles > 0 && (
                <div className="space-y-1">
                    <div className="h-2 bg-white/10 rounded-full overflow-hidden">
                        <div
                            className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full transition-all duration-300"
                            style={{ width: `${fileProgress}%` }}
                        />
                    </div>
                    <div className="text-xs text-gray-500 text-right">
                        {fileProgress}%
                    </div>
                </div>
            )}

            {/* Metadata enrichment progress bar */}
            {inMetadata && (
                <div className="space-y-1">
                    <div className="flex items-center gap-2 text-sm text-violet-300">
                        <Sparkles className="w-4 h-4" />
                        <span>Fetching metadata &amp; artwork — {job.metadataRemaining} remaining</span>
                    </div>
                    {job.metadataTotal > 0 && (
                        <div className="h-2 bg-white/10 rounded-full overflow-hidden">
                            <div
                                className="h-full bg-violet-500 rounded-full transition-all duration-300"
                                style={{ width: `${metadataProgress}%` }}
                            />
                        </div>
                    )}
                </div>
            )}

            {/* Paused detection: explain the yield so it doesn't look stuck */}
            {job.status === 'Paused' && isDetection && (
                <div className="flex items-center gap-2 text-sm text-amber-300/90">
                    <PauseCircle className="w-4 h-4" />
                    <span>
                        Waiting for library scans to finish — resumes automatically
                        {job.totalFiles > 0 ? ` (${job.processedFiles}/${job.totalFiles} series done)` : ''}
                    </span>
                </div>
            )}

            {/* Current File */}
            {isRunning && !inMetadata && job.currentFile && (
                <div className="text-xs text-gray-500 truncate">
                    → {job.currentFile}
                </div>
            )}

            {/* Live error counter while running */}
            {isRunning && job.errorCount > 0 && (
                <div className="text-xs text-red-400">
                    {job.errorCount} file{job.errorCount === 1 ? '' : 's'} failed so far
                </div>
            )}

            {/* Final Stats - only show when completed */}
            {job.status === 'Completed' && isDetection && (
                <div className="flex items-center gap-4 text-sm">
                    <span className="text-green-400">
                        <span className="font-medium">{job.updatedItems}</span> intro/credit segments found
                    </span>
                    <span className="text-gray-400">
                        <span className="font-medium">{job.totalFiles}</span> series checked
                    </span>
                    {job.errorCount > 0 && (
                        <span className="text-red-400">
                            <span className="font-medium">{job.errorCount}</span> failed
                        </span>
                    )}
                </div>
            )}
            {job.status === 'Completed' && !isDetection && (
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
