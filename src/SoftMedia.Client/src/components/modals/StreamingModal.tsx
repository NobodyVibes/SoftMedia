import React, { useState } from 'react';
import { toast } from 'sonner';
import { extractApiError } from '../../services/apiError';
import { userService, type UserDto } from '../../services/userService';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Modal } from '../ui/Modal';

interface StreamingModalProps {
    isOpen: boolean;
    onClose: () => void;
    user: UserDto | null;
}

const RESOLUTION_OPTIONS = [
    { value: 0, label: 'No limit' },
    { value: 720, label: '720p' },
    { value: 1080, label: '1080p' },
    { value: 1440, label: '1440p' },
    { value: 2160, label: '4K (2160p)' },
];

/**
 * R-WI-009/QS-WI-002 — the admin user-editor's "Streaming limits" section: base bitrate cap,
 * remote (off-LAN) bitrate cap, and a resolution ceiling for one account. Mirrors RatingsModal
 * in shape; opens from a UserListTable row. Enforced at stream-plan time.
 *
 * Semantics (deliberate, documented in the streaming-quality plan §2): a set limit OVERRIDES
 * the server's network caps for this account — it may exceed them ("this user's personal
 * limit"), it is not min'd against them. The remote cap applies only off-LAN and beats the
 * base cap there.
 */
export const StreamingModal: React.FC<StreamingModalProps> = ({ isOpen, onClose, user }) => {
    const queryClient = useQueryClient();
    const [kbps, setKbps] = useState<number>(0);
    const [remoteKbps, setRemoteKbps] = useState<number>(0);
    const [maxResolution, setMaxResolution] = useState<number>(0);

    // Reseed when pointed at a different user — during render, not in an effect,
    // so the previous user's caps never flash (react.dev: "adjusting state when
    // props change").
    const [seededFor, setSeededFor] = useState<UserDto | null>(null);
    if (user && user !== seededFor) {
        setSeededFor(user);
        setKbps(user.maxStreamBitrateKbps ?? 0);
        setRemoteKbps(user.remoteMaxStreamBitrateKbps ?? 0);
        setMaxResolution(user.maxStreamResolution ?? 0);
    }

    const updateMutation = useMutation({
        mutationFn: (limits: { maxStreamBitrateKbps: number; remoteMaxStreamBitrateKbps: number; maxStreamResolution: number }) =>
            userService.updateUserStreaming(user!.id, limits),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('Streaming limits updated');
            onClose();
        },
        onError: (error: unknown) => {
            toast.error(extractApiError(error, 'Failed to update streaming limits'));
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!user) return;
        updateMutation.mutate({
            maxStreamBitrateKbps: Math.max(0, Math.floor(kbps || 0)),
            remoteMaxStreamBitrateKbps: Math.max(0, Math.floor(remoteKbps || 0)),
            maxStreamResolution: maxResolution,
        });
    };

    if (!isOpen || !user) return null;

    return (
        <Modal isOpen={isOpen} onClose={onClose} title={`Streaming Limits for ${user.username}`}>
            <form onSubmit={handleSubmit} className="space-y-4">
                <p className="text-xs text-gray-500">
                    These limits override the server's network caps for this account — including
                    allowing more than the server-wide remote limit.
                </p>
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
                <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">
                        Max remote bitrate (kbps)
                    </label>
                    <input
                        type="number"
                        min={0}
                        step={500}
                        value={remoteKbps}
                        onChange={(e) => setRemoteKbps(Number(e.target.value))}
                        className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                    />
                    <p className="text-xs text-gray-500 mt-1">
                        0 = inherit. Applies only when this user streams from outside the home
                        network, and then takes precedence over the limit above.
                    </p>
                </div>
                <div>
                    <label className="block text-sm font-medium text-gray-400 mb-1">
                        Max resolution
                    </label>
                    <select
                        value={maxResolution}
                        onChange={(e) => setMaxResolution(Number(e.target.value))}
                        className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                    >
                        {RESOLUTION_OPTIONS.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
                    </select>
                    <p className="text-xs text-gray-500 mt-1">
                        Streams above this are downscaled for this account.
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
                        {updateMutation.isPending ? 'Saving...' : 'Save Limits'}
                    </button>
                </div>
            </form>
        </Modal>
    );
};
