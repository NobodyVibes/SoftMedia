import React, { useState } from 'react';
import { toast } from 'sonner';
import { userService } from '../services/userService';
import { useMutation, useQueryClient } from '@tanstack/react-query';

interface CreateUserModalProps {
    isOpen: boolean;
    onClose: () => void;
}

export const CreateUserModal: React.FC<CreateUserModalProps> = ({ isOpen, onClose }) => {
    const queryClient = useQueryClient();
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [role, setRole] = useState('User');

    const createMutation = useMutation({
        mutationFn: userService.createUser,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User created successfully');
            onClose();
            setUsername('');
            setPassword('');
            setRole('User');
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to create user');
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!username || !password) {
            toast.error('Username and password are required');
            return;
        }
        createMutation.mutate({ username, password, role });
    };

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
            <div className="bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700">
                <h2 className="text-xl font-bold text-white mb-4">Create New User</h2>
                <form onSubmit={handleSubmit} className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Username</label>
                        <input
                            type="text"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                            placeholder="Enter username"
                        />
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Password</label>
                        <input
                            type="password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                            placeholder="Enter password"
                        />
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Role</label>
                        <select
                            value={role}
                            onChange={(e) => setRole(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-primary"
                        >
                            <option value="User">User</option>
                            <option value="Admin">Admin</option>
                        </select>
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
                            disabled={createMutation.isPending}
                            className="px-4 py-2 rounded bg-primary hover:bg-primary/90 text-white transition-colors disabled:opacity-50"
                        >
                            {createMutation.isPending ? 'Creating...' : 'Create User'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};
