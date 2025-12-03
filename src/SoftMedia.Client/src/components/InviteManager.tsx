import React, { useState, useMemo } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { inviteService, type InviteDto } from '../services/inviteService';
import { ConfirmationModal } from './ConfirmationModal';
import { toast } from 'sonner';
import { Eye, EyeOff, ArrowUpDown, ArrowUp, ArrowDown } from 'lucide-react';

const InviteCodeDisplay: React.FC<{ code: string; isVisible: boolean }> = ({ code, isVisible }) => {
    return (
        <code className="text-sm text-white font-mono bg-gray-900 px-2 py-1 rounded">
            {isVisible ? code : `${code.substring(0, 3)}•••••••••`}
        </code>
    );
};

type SortConfig = {
    key: keyof InviteDto | 'status';
    direction: 'asc' | 'desc';
};

type InviteFilters = {
    code: string;
    created: string;
    expires: string;
    status: string;
    usedBy: string;
};

export const InviteManager: React.FC = () => {
    const queryClient = useQueryClient();
    const [showExpirationMenu, setShowExpirationMenu] = useState(false);
    const [visibleCodes, setVisibleCodes] = useState<Set<string>>(new Set());
    const [confirmModal, setConfirmModal] = useState<{
        isOpen: boolean;
        code: string;
    }>({
        isOpen: false,
        code: '',
    });

    // Sorting and Filtering State
    const [sortConfig, setSortConfig] = useState<SortConfig>({ key: 'createdAt', direction: 'desc' });
    const [filters, setFilters] = useState<InviteFilters>({
        code: '',
        created: '',
        expires: '',
        status: 'Active', // Default to Active instead of All
        usedBy: '',
    });

    const toggleCodeVisibility = (code: string) => {
        setVisibleCodes(prev => {
            const newSet = new Set(prev);
            if (newSet.has(code)) {
                newSet.delete(code);
            } else {
                newSet.add(code);
            }
            return newSet;
        });
    };

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
        if (invite.isRevoked) return { text: 'Revoked', color: 'bg-red-900' };
        if (invite.usedAt) return { text: 'Used', color: 'bg-gray-600' };
        if (invite.expiresAt && new Date(invite.expiresAt) < new Date())
            return { text: 'Expired', color: 'bg-red-600' };
        return { text: 'Active', color: 'bg-green-600' };
    };

    // Sorting Logic
    const handleSort = (key: SortConfig['key']) => {
        let direction: 'asc' | 'desc' = 'asc';
        if (sortConfig.key === key && sortConfig.direction === 'asc') {
            direction = 'desc';
        }
        setSortConfig({ key, direction });
    };

    // Filter Logic
    const handleFilterChange = (key: keyof InviteFilters, value: string) => {
        setFilters(prev => ({ ...prev, [key]: value }));
    };

    // Processed Invites
    const processedInvites = useMemo(() => {
        if (!invites) return [];

        let result = [...invites];

        // 1. Filtering
        result = result.filter(invite => {
            // Code
            if (filters.code && !invite.code.toLowerCase().includes(filters.code.toLowerCase())) return false;

            // Used By
            if (filters.usedBy && !invite.usedByUsername?.toLowerCase().includes(filters.usedBy.toLowerCase())) return false;

            // Status
            if (filters.status !== 'All') {
                const isUsed = !!invite.usedAt;
                const isExpired = invite.expiresAt && new Date(invite.expiresAt) < new Date();
                const isRevoked = invite.isRevoked;

                if (filters.status === 'Active' && (isUsed || isExpired || isRevoked)) return false;
                if (filters.status === 'Used' && !isUsed) return false;
                if (filters.status === 'Expired' && !isExpired) return false;
                if (filters.status === 'Revoked' && !isRevoked) return false;
            }

            return true;
        });

        // 2. Sorting
        result.sort((a, b) => {
            let aValue: any = '';
            let bValue: any = '';

            switch (sortConfig.key) {
                case 'code':
                    aValue = a.code;
                    bValue = b.code;
                    break;
                case 'createdAt':
                    aValue = new Date(a.createdAt).getTime();
                    bValue = new Date(b.createdAt).getTime();
                    break;
                case 'expiresAt':
                    aValue = a.expiresAt ? new Date(a.expiresAt).getTime() : Number.MAX_VALUE;
                    bValue = b.expiresAt ? new Date(b.expiresAt).getTime() : Number.MAX_VALUE;
                    break;
                case 'usedByUsername':
                    aValue = a.usedByUsername || '';
                    bValue = b.usedByUsername || '';
                    break;
                case 'status':
                    // Custom status priority
                    const getStatusPriority = (i: InviteDto) => {
                        if (i.isRevoked) return 0;
                        if (i.usedAt) return 1;
                        if (i.expiresAt && new Date(i.expiresAt) < new Date()) return 2;
                        return 3; // Active
                    };
                    aValue = getStatusPriority(a);
                    bValue = getStatusPriority(b);
                    break;
                default:
                    aValue = (a as any)[sortConfig.key];
                    bValue = (b as any)[sortConfig.key];
            }

            if (aValue < bValue) return sortConfig.direction === 'asc' ? -1 : 1;
            if (aValue > bValue) return sortConfig.direction === 'asc' ? 1 : -1;
            return 0;
        });

        return result;
    }, [invites, filters, sortConfig]);

    const renderSortIcon = (key: SortConfig['key']) => {
        if (sortConfig.key !== key) return <ArrowUpDown className="w-4 h-4 ml-1 text-gray-600" />;
        return sortConfig.direction === 'asc' ? <ArrowUp className="w-4 h-4 ml-1 text-primary" /> : <ArrowDown className="w-4 h-4 ml-1 text-primary" />;
    };

    return (
        <div className="space-y-4">
            <div className="flex justify-between items-start">
                <div className="space-y-2">
                    <h3 className="text-lg font-semibold text-white">Invite Codes</h3>
                </div>
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
            ) : (
                <div className="bg-gray-800 rounded-lg overflow-hidden">
                    <table className="w-full">
                        <thead className="bg-gray-900">
                            <tr>
                                {/* Headers with Sort */}
                                {[
                                    { key: 'code', label: 'Code' },
                                    { key: 'createdAt', label: 'Created' },
                                    { key: 'expiresAt', label: 'Expires' },
                                    { key: 'status', label: 'Status' },
                                    { key: 'usedByUsername', label: 'Used By' },
                                    { key: null, label: 'Actions' },
                                ].map((col, idx) => (
                                    <th
                                        key={idx}
                                        className={`px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider ${col.key ? 'cursor-pointer hover:bg-gray-800 select-none' : ''}`}
                                        onClick={() => col.key && handleSort(col.key as SortConfig['key'])}
                                    >
                                        <div className="flex items-center">
                                            {col.label}
                                            {col.key && renderSortIcon(col.key as SortConfig['key'])}
                                        </div>
                                    </th>
                                ))}
                            </tr>
                            {/* Filter Row */}
                            <tr className="bg-gray-850 border-b border-gray-700">
                                <td className="px-6 py-2">
                                    <input
                                        type="text"
                                        placeholder="Filter..."
                                        value={filters.code}
                                        onChange={(e) => handleFilterChange('code', e.target.value)}
                                        className="w-full bg-gray-700 text-white text-xs rounded px-2 py-1 border border-gray-600 focus:border-primary focus:outline-none"
                                    />
                                </td>
                                <td className="px-6 py-2"></td> {/* Created */}
                                <td className="px-6 py-2"></td> {/* Expires */}
                                <td className="px-6 py-2">
                                    <select
                                        value={filters.status}
                                        onChange={(e) => handleFilterChange('status', e.target.value)}
                                        className="w-full bg-gray-700 text-white text-xs rounded px-2 py-1 border border-gray-600 focus:border-primary focus:outline-none"
                                    >
                                        <option value="All">All</option>
                                        <option value="Active">Active</option>
                                        <option value="Used">Used</option>
                                        <option value="Expired">Expired</option>
                                        <option value="Revoked">Revoked</option>
                                    </select>
                                </td>
                                <td className="px-6 py-2">
                                    <input
                                        type="text"
                                        placeholder="Filter..."
                                        value={filters.usedBy}
                                        onChange={(e) => handleFilterChange('usedBy', e.target.value)}
                                        className="w-full bg-gray-700 text-white text-xs rounded px-2 py-1 border border-gray-600 focus:border-primary focus:outline-none"
                                    />
                                </td>
                                <td className="px-6 py-2"></td> {/* Actions */}
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-700">
                            {processedInvites.length > 0 ? (
                                processedInvites.map((invite) => {
                                    const status = getInviteStatus(invite);
                                    return (
                                        <tr key={invite.code} className="hover:bg-gray-750">
                                            <td className="px-6 py-4 whitespace-nowrap">
                                                <div className="flex items-center gap-2">
                                                    <InviteCodeDisplay code={invite.code} isVisible={visibleCodes.has(invite.code)} />
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
                                                    <button
                                                        onClick={() => toggleCodeVisibility(invite.code)}
                                                        className="text-gray-400 hover:text-white focus:outline-none"
                                                        title={visibleCodes.has(invite.code) ? "Hide code" : "Show code"}
                                                    >
                                                        {visibleCodes.has(invite.code) ? (
                                                            <EyeOff className="w-4 h-4" />
                                                        ) : (
                                                            <Eye className="w-4 h-4" />
                                                        )}
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
                                })
                            ) : (
                                <tr>
                                    <td colSpan={6} className="px-6 py-8 text-center text-gray-400">
                                        No invites found matching your filters.
                                    </td>
                                </tr>
                            )}
                        </tbody>
                    </table>
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
