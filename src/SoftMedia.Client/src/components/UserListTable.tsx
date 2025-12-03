import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { userService, type UserDto } from '../services/userService';
import { ConfirmationModal } from './ConfirmationModal';
import { useAuthStore } from '../store/authStore';
import { toast } from 'sonner';
import { CreateUserModal } from './CreateUserModal';
import { RatingsModal } from './RatingsModal';

export const UserListTable: React.FC = () => {
    const queryClient = useQueryClient();
    const currentUser = useAuthStore((state) => state.user);
    const [confirmModal, setConfirmModal] = useState<{
        isOpen: boolean;
        title: string;
        message: string;
        action: () => void;
        variant: 'default' | 'danger';
    }>({
        isOpen: false,
        title: '',
        message: '',
        action: () => { },
        variant: 'default',
    });
    const [showBannedOrRejected, setShowBannedOrRejected] = useState(false);
    const [isCreateModalOpen, setIsCreateModalOpen] = useState(false);
    const [ratingsModalUser, setRatingsModalUser] = useState<UserDto | null>(null);

    const { data: users, isLoading } = useQuery({
        queryKey: ['users'],
        queryFn: userService.getUsers,
    });

    const updateRoleMutation = useMutation({
        mutationFn: ({ userId, role }: { userId: string; role: string }) =>
            userService.updateUserRole(userId, role),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User role updated successfully');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to update user role');
        },
    });

    const banMutation = useMutation({
        mutationFn: ({ userId, isBanned }: { userId: string; isBanned: boolean }) =>
            userService.banUser(userId, isBanned),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User status updated successfully');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to update user status');
        },
    });

    const approveMutation = useMutation({
        mutationFn: ({ userId, isApproved }: { userId: string; isApproved: boolean }) =>
            userService.approveUser(userId, isApproved),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User approved successfully');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to approve user');
        },
    });

    const denyMutation = useMutation({
        mutationFn: (userId: string) => userService.denyUser(userId),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User denied successfully');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to deny user');
        },
    });

    const deleteMutation = useMutation({
        mutationFn: (userId: string) => userService.deleteUser(userId),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User deleted successfully');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to delete user');
        },
    });

    const handleRoleChange = (user: UserDto, newRole: string) => {
        if (user.role === newRole) return;

        setConfirmModal({
            isOpen: true,
            title: 'Change User Role',
            message: `Are you sure you want to change ${user.username}'s role to ${newRole}?`,
            action: () => {
                updateRoleMutation.mutate({ userId: user.id, role: newRole });
                setConfirmModal({ ...confirmModal, isOpen: false });
            },
            variant: 'default',
        });
    };

    const handleBan = (user: UserDto) => {
        const action = user.isBanned ? 'unban' : 'ban';

        setConfirmModal({
            isOpen: true,
            title: `${action === 'ban' ? 'Ban' : 'Unban'} User`,
            message: `Are you sure you want to ${action} ${user.username}?`,
            action: () => {
                banMutation.mutate({ userId: user.id, isBanned: !user.isBanned });
                setConfirmModal({ ...confirmModal, isOpen: false });
            },
            variant: 'danger',
        });
    };

    const handleApprove = (user: UserDto) => {
        setConfirmModal({
            isOpen: true,
            title: 'Approve User',
            message: `Are you sure you want to approve ${user.username}? They will be able to log in immediately.`,
            action: () => {
                approveMutation.mutate({ userId: user.id, isApproved: true });
                setConfirmModal({ ...confirmModal, isOpen: false });
            },
            variant: 'default',
        });
    };

    const handleDeny = (user: UserDto) => {
        setConfirmModal({
            isOpen: true,
            title: 'Deny User',
            message: `Are you sure you want to deny ${user.username}? They will not be able to log in.`,
            action: () => {
                denyMutation.mutate(user.id);
                setConfirmModal({ ...confirmModal, isOpen: false });
            },
            variant: 'danger',
        });
    };

    const handleDelete = (user: UserDto) => {
        setConfirmModal({
            isOpen: true,
            title: 'Delete User',
            message: `Are you sure you want to permanently delete ${user.username}? This action cannot be undone.`,
            action: () => {
                deleteMutation.mutate(user.id);
                setConfirmModal({ ...confirmModal, isOpen: false });
            },
            variant: 'danger',
        });
    };

    const formatDate = (dateString: string) => {
        return new Date(dateString).toLocaleDateString('en-US', {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
        });
    };

    if (isLoading) {
        return <div className="text-gray-400">Loading users...</div>;
    }

    const filteredUsers = users?.filter(user => {
        if (showBannedOrRejected) return true;
        return !user.isBanned && !user.isRejected;
    });

    return (
        <>
            <div className="mb-4 flex items-center justify-between">
                <div className="flex items-center gap-2">
                    <input
                        type="checkbox"
                        id="showBannedOrRejected"
                        checked={showBannedOrRejected}
                        onChange={(e) => setShowBannedOrRejected(e.target.checked)}
                        className="rounded border-gray-600 bg-gray-700 text-primary focus:ring-primary"
                    />
                    <label htmlFor="showBannedOrRejected" className="text-sm text-gray-300">
                        Show banned/denied users
                    </label>
                </div>
                <button
                    onClick={() => setIsCreateModalOpen(true)}
                    className="px-4 py-2 bg-blue-600 hover:bg-blue-700 text-white rounded-md shadow-md transition-all font-medium flex items-center gap-2"
                >
                    <span className="text-lg">+</span> Create User
                </button>
            </div>

            <div className="bg-gray-800 rounded-lg overflow-hidden border border-gray-700">
                <div className="max-h-[600px] overflow-y-auto">
                    <table className="w-full relative">
                        <thead className="bg-gray-900 sticky top-0 z-10">
                            <tr>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Username
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Role
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Ratings
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Created
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Status
                                </th>
                                <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                    Actions
                                </th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-gray-700">
                            {filteredUsers?.map((user) => {
                                const isCurrentUser = user.id === currentUser?.id;
                                return (
                                    <tr key={user.id} className="hover:bg-gray-750">
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-white">
                                            {user.username}
                                            {isCurrentUser && (
                                                <span className="ml-2 text-xs text-gray-400">(You)</span>
                                            )}
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap">
                                            <select
                                                value={user.role}
                                                onChange={(e) => handleRoleChange(user, e.target.value)}
                                                disabled={isCurrentUser || user.isBanned || user.isRejected}
                                                className={`bg-gray-700 text-white text-sm rounded px-2 py-1 border border-gray-600 focus:outline-none focus:border-primary ${(isCurrentUser || user.isBanned || user.isRejected) ? 'opacity-50 cursor-not-allowed' : ''
                                                    }`}
                                            >
                                                <option value="User">User</option>
                                                <option value="Admin">Admin</option>
                                            </select>
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                            <button
                                                onClick={() => setRatingsModalUser(user)}
                                                className="text-primary hover:text-primary/80 underline"
                                            >
                                                Edit Ratings
                                            </button>
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                            {formatDate(user.createdAt)}
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap">
                                            <div className="flex flex-col gap-1">
                                                {user.isBanned ? (
                                                    <span className="px-2 py-1 text-xs font-semibold rounded w-fit bg-red-600 text-white">
                                                        Banned
                                                    </span>
                                                ) : user.isRejected ? (
                                                    <span className="px-2 py-1 text-xs font-semibold rounded w-fit bg-gray-600 text-gray-300">
                                                        Denied
                                                    </span>
                                                ) : !user.isApproved ? (
                                                    <span className="px-2 py-1 text-xs font-semibold rounded w-fit bg-yellow-600 text-white">
                                                        Pending Approval
                                                    </span>
                                                ) : (
                                                    <span className="px-2 py-1 text-xs font-semibold rounded w-fit bg-green-600 text-white">
                                                        Active
                                                    </span>
                                                )}
                                            </div>
                                        </td>
                                        <td className="px-6 py-4 whitespace-nowrap text-sm">
                                            <div className="flex gap-2">
                                                <button
                                                    onClick={() => handleBan(user)}
                                                    disabled={isCurrentUser}
                                                    className={`px-3 py-1 rounded transition-colors ${isCurrentUser
                                                        ? 'bg-gray-700 text-gray-500 cursor-not-allowed'
                                                        : user.isBanned
                                                            ? 'bg-green-600 hover:bg-green-700 text-white'
                                                            : 'bg-yellow-600 hover:bg-yellow-700 text-white'
                                                        }`}
                                                >
                                                    {user.isBanned ? 'Unban' : 'Ban'}
                                                </button>
                                                {!user.isApproved && !user.isRejected && (
                                                    <>
                                                        <button
                                                            onClick={() => handleApprove(user)}
                                                            disabled={isCurrentUser}
                                                            className="px-3 py-1 rounded transition-colors bg-green-600 hover:bg-green-700 text-white"
                                                        >
                                                            Approve
                                                        </button>
                                                        <button
                                                            onClick={() => handleDeny(user)}
                                                            disabled={isCurrentUser}
                                                            className="px-3 py-1 rounded transition-colors bg-gray-600 hover:bg-gray-700 text-white"
                                                        >
                                                            Deny
                                                        </button>
                                                    </>
                                                )}
                                                <button
                                                    onClick={() => handleDelete(user)}
                                                    disabled={isCurrentUser}
                                                    className={`px-3 py-1 rounded transition-colors ${isCurrentUser
                                                        ? 'bg-gray-700 text-gray-500 cursor-not-allowed'
                                                        : 'bg-red-600 hover:bg-red-700 text-white'
                                                        }`}
                                                >
                                                    Delete
                                                </button>
                                            </div>
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            </div>

            <ConfirmationModal
                isOpen={confirmModal.isOpen}
                title={confirmModal.title}
                message={confirmModal.message}
                onConfirm={confirmModal.action}
                onCancel={() => setConfirmModal({ ...confirmModal, isOpen: false })}
                variant={confirmModal.variant}
            />

            <CreateUserModal
                isOpen={isCreateModalOpen}
                onClose={() => setIsCreateModalOpen(false)}
            />

            <RatingsModal
                isOpen={!!ratingsModalUser}
                onClose={() => setRatingsModalUser(null)}
                user={ratingsModalUser}
            />
        </>
    );
};
