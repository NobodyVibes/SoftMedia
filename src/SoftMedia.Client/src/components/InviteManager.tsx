import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { inviteService, type InviteDto } from '../services/inviteService';
import { ConfirmationModal } from './ConfirmationModal';
import { toast } from 'sonner';

export const InviteManager: React.FC = () => {
    const queryClient = useQueryClient();
    const [showExpirationMenu, setShowExpirationMenu] = useState(false);
    const [confirmModal, setConfirmModal] = useState<{
        isOpen: boolean;
        code: string;
    }>({
        isOpen: false,
        code: '',
    });

    const { data: invites, isLoading } = useQuery({
        queryKey: ['invites'],
        queryFn: inviteService.getInvites,
    });

    const createMutation = useMutation({
        mutationFn: (expiresInHours: number | null) =>
            inviteService.createInvite(expiresInHours),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['invites'] });
            toast.success('Invite code generated successfully');
            setShowExpirationMenu(false);
        },
        onError: () => {
            toast.error('Failed to generate invite code');
        },
    });

    const revokeMutation = useMutation({
        mutationFn: (code: string) => inviteService.revokeInvite(code),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['invites'] });
            toast.success('Invite code revoked successfully');
        },
        onError: () => {
            toast.error('Failed to revoke invite code');
        },
    });

    const handleCreateInvite = (expiresInHours: number | null) => {
        createMutation.mutate(expiresInHours);
    };

    const handleCopyCode = (code: string) => {
        navigator.clipboard.writeText(code);
        toast.success('Invite code copied to clipboard');
    };

    const handleRevoke = (code: string) => {
        setConfirmModal({ isOpen: true, code });
    };

    const confirmRevoke = () => {
        revokeMutation.mutate(confirmModal.code);
        setConfirmModal({ isOpen: false, code: '' });
    };

    const formatDate = (dateString: string | null) => {
        if (!dateString) return 'Never';
        return new Date(dateString).toLocaleDateString('en-US', {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
            hour: '2-digit',
            minute: '2-digit',
        });
    };

    const getInviteStatus = (invite: InviteDto) => {
        if (invite.usedAt) return { text: 'Used', color: 'bg-gray-600' };
        if (invite.expiresAt && new Date(invite.expiresAt) < new Date())
            return { text: 'Expired', color: 'bg-red-600' };
        return { text: 'Active', color: 'bg-green-600' };
    };

    return (
        <div className="space-y-4">
            <div className="flex justify-between items-center">
                <h3 className="text-lg font-semibold text-white">Invite Codes</h3>
                <div className="relative">
                    <button
                        onClick={() => setShowExpirationMenu(!showExpirationMenu)}
                        className="px-4 py-2 bg-gradient-to-r from-blue-500 to-violet-600 hover:from-blue-600 hover:to-violet-700 text-white rounded transition-colors"
                    >
                        Generate Invite
                    </button>
                    {showExpirationMenu && (
                        <div className="absolute right-0 mt-2 w-48 bg-gray-800 rounded-lg shadow-xl z-10 border border-gray-700">
                            <button
                                onClick={() => handleCreateInvite(24)}
                                className="block w-full text-left px-4 py-2 text-white hover:bg-gray-700 rounded-t-lg"
                            >
                                Expires in 24 hours
                            </button>
                            <button
                                onClick={() => handleCreateInvite(24 * 7)}
                                className="block w-full text-left px-4 py-2 text-white hover:bg-gray-700"
                            >
                                Expires in 7 days
                            </button>
                            <button
                                onClick={() => handleCreateInvite(24 * 30)}
                                className="block w-full text-left px-4 py-2 text-white hover:bg-gray-700"
                            >
                                Expires in 30 days
                            </button>
                            <button
                                onClick={() => handleCreateInvite(null)}
                                className="block w-full text-left px-4 py-2 text-white hover:bg-gray-700 rounded-b-lg"
                            >
                                Never expires
                            </button>
                        </div>
                    )}
                </div>
            </div>

            {isLoading ? (
                <div className="text-gray-400">Loading invites...</div>
            ) : invites && invites.length > 0 ? (
                <div className="bg-gray-800 rounded-lg overflow-hidden">
                    <table className="w-full">
                        <thead className="bg-gray-900">
                            <tr>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Code
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Created
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Expires
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Status
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Used By
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Actions
                                </th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-700">
                            {invites.map((invite) => {
                                const status = getInviteStatus(invite);
                                return (
                                    <tr key={invite.code} className="hover:bg-gray-750">
                                        <td className="px-6 py-4 whitespace-nowrap">
                                            <div className="flex items-center gap-2">
                                                <code className="text-sm text-white bg-gray-900 px-2 py-1 rounded">
                                                    {invite.code}
                                                </code>
                                                <button
                                                    onClick={() => handleCopyCode(invite.code)}
                                                    className="text-blue-400 hover:text-blue-300"
                                                    title="Copy to clipboard"
                                                >
                                                    <svg
                                                        className="w-4 h-4"
                                                        fill="none"
                                                        stroke="currentColor"
                                                        viewBox="0 0 24 24"
                                                    >
                                                        <path
                                                            strokeLinecap="round"
                                                            strokeLinejoin="round"
                                                            strokeWidth={2}
                                                            d="M8 16H6a2 2 0 01-2-2V6a2 2 0 012-2h8a2 2 0 012 2v2m-6 12h8a2 2 0 002-2v-8a2 2 0 00-2-2h-8a2 2 0 00-2 2v8a2 2 0 002 2z"
                                                        />
                                                    </svg>
                                                </button>
                                            </div>
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                            {formatDate(invite.createdAt)}
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                            {formatDate(invite.expiresAt)}
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap">
                                            <span
                                                className={`px-2 py-1 text-xs font-semibold rounded ${status.color} text-white`}
                                            >
                                                {status.text}
                                            </span>
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                            {invite.usedByUsername || '-'}
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap">
                                            {!invite.usedAt && (
                                                <button
                                                    onClick={() => handleRevoke(invite.code)}
                                                    className="px-3 py-1 bg-red-600 hover:bg-red-700 text-white rounded transition-colors text-sm"
                                                >
                                                    Revoke
                                                </button>
                                            )}
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            ) : (
                <div className="text-gray-400 text-center py-8">
                    No invite codes generated yet. Click "Generate Invite" to create one.
                </div>
            )}

            <ConfirmationModal
                isOpen={confirmModal.isOpen}
                title="Revoke Invite"
                message={`Are you sure you want to revoke invite code ${confirmModal.code}? It will no longer be usable for signup.`}
                onConfirm={confirmRevoke}
                onCancel={() => setConfirmModal({ isOpen: false, code: '' })}
                variant="danger"
            />
        </div>
    );
};
