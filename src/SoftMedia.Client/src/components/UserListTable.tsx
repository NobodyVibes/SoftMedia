import React, { useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { userService, type UserDto } from '../services/userService';
import { ConfirmationModal } from './ConfirmationModal';
import { useAuthStore } from '../store/authStore';
import { toast } from 'sonner';

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

    const handlePromote = (user: UserDto) => {
        const newRole = user.role === 'Admin' ? 'User' : 'Admin';
        const action = newRole === 'Admin' ? 'promote' : 'demote';

        setConfirmModal({
            isOpen: true,
            title: `${action === 'promote' ? 'Promote' : 'Demote'} User`,
            message: `Are you sure you want to ${action} ${user.username} to ${newRole}?`,
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

    return (
        <>
            <div className="bg-gray-800 rounded-lg overflow-hidden">
                <table className="w-full">
                    <thead className="bg-gray-900">
                        <tr>
                            <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                Username
                            </th>
                            <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                Role
                            </th>
                            <th className="px-6 py-3 text-left text-xs font-medium text-gray-400 uppercase tracking-wider">
                                Max Rating
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
                        {users?.map((user) => {
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
                                        <span
                                            className={`px-2 py-1 text-xs font-semibold rounded ${user.role === 'Admin'
                                                ? 'bg-violet-600 text-white'
                                                : 'bg-gray-600 text-gray-200'
                                                }`}
                                        >
                                            {user.role}
                                        </span>
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                        {user.maxRating}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm text-gray-300">
                                        {formatDate(user.createdAt)}
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap">
                                        <span
                                            className={`px-2 py-1 text-xs font-semibold rounded ${user.isBanned
                                                ? 'bg-red-600 text-white'
                                                : 'bg-green-600 text-white'
                                                }`}
                                        >
                                            {user.isBanned ? 'Banned' : 'Active'}
                                        </span>
                                    </td>
                                    <td className="px-6 py-4 whitespace-nowrap text-sm">
                                        <div className="flex gap-2">
                                            <button
                                                onClick={() => handlePromote(user)}
                                                disabled={isCurrentUser}
                                                className={`px-3 py-1 rounded transition-colors ${isCurrentUser
                                                    ? 'bg-gray-700 text-gray-500 cursor-not-allowed'
                                                    : 'bg-blue-600 hover:bg-blue-700 text-white'
                                                    }`}
                                            >
                                                {user.role === 'Admin' ? 'Demote' : 'Promote'}
                                            </button>
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

            <ConfirmationModal
                isOpen={confirmModal.isOpen}
                title={confirmModal.title}
                message={confirmModal.message}
                onConfirm={confirmModal.action}
                onCancel={() => setConfirmModal({ ...confirmModal, isOpen: false })}
                variant={confirmModal.variant}
            />
        </>
    );
};
