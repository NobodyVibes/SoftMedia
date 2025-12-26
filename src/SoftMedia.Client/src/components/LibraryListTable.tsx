import { Edit2, Trash2, Folder, ArrowUp, ArrowDown, RefreshCw, Clock, Loader2 } from 'lucide-react';
import type { Library, LibraryScanJob } from '../types';

interface LibraryListTableProps {
    libraries: Library[];
    scanJobs: LibraryScanJob[];
    onEdit: (library: Library) => void;
    onDelete: (library: Library) => void;
    onReorder: (orderedIds: string[]) => void;
    onScan: (library: Library) => void;
}

export function LibraryListTable({ libraries, scanJobs, onEdit, onDelete, onReorder, onScan }: LibraryListTableProps) {
    const getLibraryScanJob = (libraryId: string): LibraryScanJob | undefined => {
        return scanJobs.find(job =>
            job.libraryId === libraryId &&
            (job.status === 'Queued' || job.status === 'Running')
        );
    };

    if (libraries.length === 0) {
        return (
            <div className="text-center py-12 bg-white/5 rounded-xl border border-white/10">
                <Folder className="w-12 h-12 text-gray-500 mx-auto mb-3" />
                <h3 className="text-lg font-medium text-white">No libraries found</h3>
                <p className="text-gray-400">Add a library to start scanning your media.</p>
            </div>
        );
    }

    const moveLibrary = (index: number, direction: 'up' | 'down') => {
        const newLibraries = [...libraries];
        const targetIndex = direction === 'up' ? index - 1 : index + 1;

        if (targetIndex >= 0 && targetIndex < newLibraries.length) {
            const [movedItem] = newLibraries.splice(index, 1);
            newLibraries.splice(targetIndex, 0, movedItem);
            onReorder(newLibraries.map(l => l.id));
        }
    };

    const getTypeColor = (type: Library['type']) => {
        const colors: Record<Library['type'], string> = {
            'Movie': 'bg-blue-500/20 text-blue-300 border-blue-500/30',
            'TV': 'bg-purple-500/20 text-purple-300 border-purple-500/30',
            'Music': 'bg-green-500/20 text-green-300 border-green-500/30',
            'Book': 'bg-yellow-500/20 text-yellow-300 border-yellow-500/30',
            'Game': 'bg-red-500/20 text-red-300 border-red-500/30',
            'Photo': 'bg-pink-500/20 text-pink-300 border-pink-500/30',
        };
        return colors[type] || 'bg-gray-500/20 text-gray-300';
    };

    const getTypeLabel = (type: Library['type']) => {
        return type;
    };

    return (
        <div className="overflow-hidden rounded-xl border border-white/10">
            <table className="w-full text-left border-collapse">
                <thead>
                    <tr className="bg-white/5 border-b border-white/10">
                        <th className="p-4 text-sm font-medium text-gray-400 w-16">Order</th>
                        <th className="p-4 text-sm font-medium text-gray-400">Name</th>
                        <th className="p-4 text-sm font-medium text-gray-400">Type</th>
                        <th className="p-4 text-sm font-medium text-gray-400">Status</th>
                        <th className="p-4 text-sm font-medium text-gray-400">Folders</th>
                        <th className="p-4 text-sm font-medium text-gray-400 text-right">Actions</th>
                    </tr>
                </thead>
                <tbody className="divide-y divide-white/5">
                    {libraries.map((library, index) => {
                        const scanJob = getLibraryScanJob(library.id);
                        const isScanning = scanJob?.status === 'Running';
                        const isQueued = scanJob?.status === 'Queued';
                        const isBusy = isScanning || isQueued;

                        return (
                            <tr key={library.id} className="hover:bg-white/5 transition-colors group">
                                <td className="p-4">
                                    <div className="flex flex-col gap-1 opacity-0 group-hover:opacity-100 transition-opacity">
                                        <button
                                            onClick={() => moveLibrary(index, 'up')}
                                            disabled={index === 0}
                                            className="text-gray-400 hover:text-white disabled:opacity-30"
                                        >
                                            <ArrowUp size={14} />
                                        </button>
                                        <button
                                            onClick={() => moveLibrary(index, 'down')}
                                            disabled={index === libraries.length - 1}
                                            className="text-gray-400 hover:text-white disabled:opacity-30"
                                        >
                                            <ArrowDown size={14} />
                                        </button>
                                    </div>
                                </td>
                                <td className="p-4 font-medium text-white">{library.name}</td>
                                <td className="p-4">
                                    <span className={`px-2 py-1 rounded-md text-xs border ${getTypeColor(library.type)}`}>
                                        {getTypeLabel(library.type)}
                                    </span>
                                </td>
                                <td className="p-4">
                                    {isScanning && scanJob && (
                                        <div className="flex items-center gap-2">
                                            <Loader2 className="w-4 h-4 text-blue-400 animate-spin" />
                                            <div className="flex flex-col">
                                                <span className="text-xs text-blue-400">Scanning...</span>
                                                <div className="w-20 h-1.5 bg-white/10 rounded-full overflow-hidden mt-0.5">
                                                    <div
                                                        className="h-full bg-gradient-to-r from-blue-500 to-violet-500 rounded-full transition-all duration-300"
                                                        style={{ width: `${scanJob.progressPercent}%` }}
                                                    />
                                                </div>
                                            </div>
                                        </div>
                                    )}
                                    {isQueued && scanJob && (
                                        <div className="flex items-center gap-2">
                                            <Clock className="w-4 h-4 text-yellow-400" />
                                            <span className="text-xs text-yellow-400">
                                                Queue #{scanJob.queuePosition}
                                            </span>
                                        </div>
                                    )}
                                    {!isBusy && (
                                        <span className="text-xs text-gray-500">Idle</span>
                                    )}
                                </td>
                                <td className="p-4 text-gray-400 text-sm">
                                    <div className="flex flex-col gap-1">
                                        {library.paths.map(path => (
                                            <span key={path} className="truncate max-w-[200px]" title={path}>
                                                {path}
                                            </span>
                                        ))}
                                    </div>
                                </td>
                                <td className="p-4 text-right">
                                    <div className="flex items-center justify-end gap-2">
                                        <button
                                            onClick={() => onScan(library)}
                                            disabled={isBusy}
                                            className={`p-2 rounded-lg transition-colors ${isBusy
                                                    ? 'text-gray-500 cursor-not-allowed'
                                                    : 'text-blue-400 hover:text-blue-300 hover:bg-blue-500/10'
                                                }`}
                                            title={isBusy ? 'Scan in progress' : 'Scan Library'}
                                        >
                                            {isScanning ? (
                                                <Loader2 size={16} className="animate-spin" />
                                            ) : (
                                                <RefreshCw size={16} />
                                            )}
                                        </button>
                                        <button
                                            onClick={() => onEdit(library)}
                                            className="p-2 text-gray-400 hover:text-white hover:bg-white/10 rounded-lg transition-colors"
                                            title="Edit"
                                        >
                                            <Edit2 size={16} />
                                        </button>
                                        <button
                                            onClick={() => onDelete(library)}
                                            className="p-2 text-red-400 hover:text-red-300 hover:bg-red-500/10 rounded-lg transition-colors"
                                            title="Delete"
                                        >
                                            <Trash2 size={16} />
                                        </button>
                                    </div>
                                </td>
                            </tr>
                        );
                    })}
                </tbody>
            </table>
        </div>
    );
}
