import React, { useState, useEffect } from 'react';
import { toast } from 'sonner';
import { userService, type UserDto } from '../../services/userService';
import { useMutation, useQueryClient } from '@tanstack/react-query';

interface StreamingModalProps {
    isOpen: boolean;
    onClose: () => void;
    user: UserDto | null;
}

/**
 * R-WI-009 — admin sets a per-user max streaming bitrate (kbps; 0 = unlimited). Mirrors RatingsModal
 * in shape and intent: opens from a UserListTable row, scoped to one user. The cap is enforced at
 * stream-plan time; this is the write surface (previously the value was only settable via the DB).
 */
export const StreamingModal: React.FC<StreamingModalProps> = ({ isOpen, onClose, user }) => {
    const queryClient = useQueryClient();
    const [kbps, setKbps] = useState<number>(0);

    useEffect(() => {
        if (user) {
            setKbps(user.maxStreamBitrateKbps ?? 0);
        }
    }, [user]);

    const updateMutation = useMutation({
        mutationFn: ({ userId, maxStreamBitrateKbps }: { userId: string; maxStreamBitrateKbps: number }) =>
            userService.updateUserStreaming(userId, maxStreamBitrateKbps),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('Streaming limit updated');
            onClose();
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to update streaming limit');
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!user) return;
        updateMutation.mutate({ userId: user.id, maxStreamBitrateKbps: Math.max(0, Math.floor(kbps || 0)) });
    };

    if (!isOpen || !user) return null;

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
            <div className="bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700">
                <h2 className="text-xl font-bold text-white mb-4">Streaming Limit for {user.username}</h2>
                <form onSubmit={handleSubmit} className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">
                            Max streaming bitrate (kbps)
                        </label>
                        <input
                            type="number"
                            min={0}
                            step={500}
                            value={kbps}
                            onChange={(e) => setKbps(Number(e.target.value))}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                        />
                        <p className="text-xs text-gray-500 mt-1">
                            0 = unlimited. When set, the user's transcodes are capped to this bitrate
                            (e.g. 4000 ≈ 4&nbsp;Mbps).
                        </p>
                    </div>
                    <div className="flex justify-end gap-2 pt-4">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 transition-colors"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={updateMutation.isPending}
                            className="px-4 py-2 rounded bg-primary hover:bg-primary/90 text-white transition-colors disabled:opacity-50"
                        >
                            {updateMutation.isPending ? 'Saving...' : 'Save Limit'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};
