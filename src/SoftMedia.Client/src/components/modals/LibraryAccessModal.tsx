import React, { useEffect, useMemo, useState } from 'react';
import { toast } from 'sonner';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Check, Square, Library as LibraryIcon } from 'lucide-react';
import { userService, type UserDto } from '../../services/userService';
import { libraryService } from '../../services/libraryService';
import type { Library } from '../../types';

interface LibraryAccessModalProps {
    isOpen: boolean;
    onClose: () => void;
    user: UserDto | null;
}

/**
 * Wave C — admin-only modal for setting per-user library access. Sibling of
 * RatingsModal in shape and intent: opens from a UserListTable row, scoped
 * to a single concern, closes via Cancel/Save.
 *
 * Default semantics (mirrored from the backend):
 *   - Saving an empty selection clears all rows  =>  user sees every library.
 *   - Saving with libraries ticked stores those exact ids  =>  user sees only those.
 *   - Admins always bypass on the server; the modal renders a disabled
 *     placeholder when invoked on an admin row.
 */
export const LibraryAccessModal: React.FC<LibraryAccessModalProps> = ({ isOpen, onClose, user }) => {
    const queryClient = useQueryClient();
    const [selected, setSelected] = useState<Set<string>>(new Set());

    const { data: libraries = [], isLoading: librariesLoading } = useQuery<Library[]>({
        queryKey: ['libraries-for-acl'],
        queryFn: libraryService.getAll,
        enabled: isOpen,
    });

    const { data: currentAccess, isLoading: currentLoading } = useQuery<string[]>({
        queryKey: ['user-library-access', user?.id],
        queryFn: () => userService.getUserLibraryAccess(user!.id),
        enabled: isOpen && !!user && user.role !== 'Admin',
    });

    // Hydrate selection state when the modal opens for a fresh user.
    useEffect(() => {
        if (isOpen && currentAccess) {
            setSelected(new Set(currentAccess));
        } else if (!isOpen) {
            setSelected(new Set());
        }
    }, [isOpen, currentAccess]);

    const updateMutation = useMutation({
        mutationFn: ({ userId, libraryIds }: { userId: string; libraryIds: string[] }) =>
            userService.setUserLibraryAccess(userId, libraryIds),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            queryClient.invalidateQueries({ queryKey: ['user-library-access', user?.id] });
            toast.success('Library access updated');
            onClose();
        },
        onError: (error: unknown) => {
            const message = error instanceof Error ? error.message : 'Failed to update library access';
            toast.error(message);
        },
    });

    const isUnrestricted = useMemo(() => selected.size === 0, [selected]);

    const toggle = (libraryId: string) => {
        setSelected(prev => {
            const next = new Set(prev);
            if (next.has(libraryId)) {
                next.delete(libraryId);
            } else {
                next.add(libraryId);
            }
            return next;
        });
    };

    const clearAll = () => setSelected(new Set());

    const handleSave = (e: React.FormEvent) => {
        e.preventDefault();
        if (!user) return;
        updateMutation.mutate({
            userId: user.id,
            libraryIds: Array.from(selected),
        });
    };

    if (!isOpen || !user) return null;

    // Admin guard — the server rejects setting ACL on admins; reflect that in the UI.
    if (user.role === 'Admin') {
        return (
            <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
                <div className="bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700">
                    <h2 className="text-xl font-bold text-white mb-3">Library Access for {user.username}</h2>
                    <div className="rounded-lg border border-white/5 bg-white/5 px-4 py-6 text-sm text-gray-300">
                        Admins always have access to all libraries. There is no per-library
                        restriction to configure.
                    </div>
                    <div className="flex justify-end pt-4">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                        >
                            Close
                        </button>
                    </div>
                </div>
            </div>
        );
    }

    const isLoading = librariesLoading || currentLoading;

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
            <div className="bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700">
                <h2 className="text-xl font-bold text-white mb-3">
                    Library Access for {user.username}
                </h2>

                <p className="text-sm text-gray-400 mb-4">
                    <span className="font-medium text-white">No selection</span> means this user can
                    see every library (default). Tick only the libraries this user should have access
                    to. Admins always bypass this restriction.
                </p>

                {isLoading ? (
                    <div className="py-8 text-center text-sm text-gray-400">Loading…</div>
                ) : (
                    <form onSubmit={handleSave} className="space-y-3">
                        <ul className="max-h-64 overflow-y-auto space-y-1 rounded-lg border border-white/5 bg-black/20 p-2">
                            {libraries.length === 0 && (
                                <li className="text-sm text-gray-400 px-2 py-3">
                                    No libraries configured yet.
                                </li>
                            )}
                            {libraries.map(lib => {
                                const checked = selected.has(lib.id);
                                return (
                                    <li key={lib.id}>
                                        <button
                                            type="button"
                                            role="checkbox"
                                            aria-checked={checked}
                                            onClick={() => toggle(lib.id)}
                                            className="w-full flex items-center gap-3 px-3 py-2 min-h-[44px] rounded-md text-left text-sm text-gray-200 hover:bg-white/10 focus-visible:outline-none focus-visible:bg-white/10 focus-visible:ring-2 focus-visible:ring-blue-400 transition-colors"
                                        >
                                            {checked ? (
                                                <Check className="w-4 h-4 text-primary flex-shrink-0" />
                                            ) : (
                                                <Square className="w-4 h-4 text-gray-500 flex-shrink-0" />
                                            )}
                                            <LibraryIcon className="w-4 h-4 text-gray-400 flex-shrink-0" />
                                            <span className="flex-1 truncate">{lib.name}</span>
                                            <span className="text-xs text-gray-500">{lib.type}</span>
                                        </button>
                                    </li>
                                );
                            })}
                        </ul>

                        <div className="flex items-center justify-between text-xs">
                            <span className="text-gray-400">
                                {isUnrestricted
                                    ? 'Unrestricted — sees every library.'
                                    : `Restricted to ${selected.size} ${selected.size === 1 ? 'library' : 'libraries'}.`}
                            </span>
                            {selected.size > 0 && (
                                <button
                                    type="button"
                                    onClick={clearAll}
                                    className="text-primary hover:underline focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400 rounded px-1"
                                >
                                    Clear all (unrestricted)
                                </button>
                            )}
                        </div>

                        <div className="flex justify-end gap-2 pt-2">
                            <button
                                type="button"
                                onClick={onClose}
                                className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                Cancel
                            </button>
                            <button
                                type="submit"
                                disabled={updateMutation.isPending}
                                className="px-4 py-2 rounded bg-primary hover:bg-primary/90 text-white transition-colors disabled:opacity-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400"
                            >
                                {updateMutation.isPending ? 'Saving…' : 'Save'}
                            </button>
                        </div>
                    </form>
                )}
            </div>
        </div>
    );
};
