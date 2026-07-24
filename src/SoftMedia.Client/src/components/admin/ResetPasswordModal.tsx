import React, { useState } from 'react';
import { Button } from '../ui/Button';
import { Input } from '../ui/Input';
import { userService, type UserDto } from '../../services/userService';
import { toast } from 'sonner';
import { extractApiError } from '../../services/apiError';
import { useMutation } from '@tanstack/react-query';
import { Modal } from '../ui/Modal';

interface ResetPasswordModalProps {
    isOpen: boolean;
    onClose: () => void;
    user: UserDto | null;
}

export const ResetPasswordModal: React.FC<ResetPasswordModalProps> = ({ isOpen, onClose, user }) => {
    const [password, setPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');

    const resetMutation = useMutation({
        mutationFn: async () => {
            if (!user) return;
            await userService.resetUserPassword(user.id, password);
        },
        onSuccess: () => {
            toast.success(`Password reset for ${user?.username}`);
            onClose();
            setPassword('');
            setConfirmPassword('');
        },
        onError: (error: unknown) => {
            toast.error(extractApiError(error, 'Failed to reset password'));
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (password !== confirmPassword) {
            toast.error("Passwords do not match");
            return;
        }
        if (password.length < 6) {
            toast.error("Password must be at least 6 characters");
            return;
        }
        resetMutation.mutate();
    };

    if (!isOpen || !user) return null;

    return (
        <Modal
            isOpen={isOpen}
            onClose={onClose}
            title={`Reset Password for ${user.username}`}
            panelClassName="bg-gray-800 rounded-lg shadow-xl max-w-md w-full mx-4 p-6 border border-gray-700"
        >
            <form onSubmit={handleSubmit} className="space-y-4">
                <div className="space-y-2">
                    <label htmlFor="new-password" className="text-sm font-medium text-gray-200">New Password</label>
                    <Input
                        id="new-password"
                        type="password"
                        value={password}
                        onChange={(e) => setPassword(e.target.value)}
                        className="bg-gray-700 border-gray-600 text-white w-full"
                        placeholder="Enter new password"
                        required
                    />
                </div>
                <div className="space-y-2">
                    <label htmlFor="confirm-password" className="text-sm font-medium text-gray-200">Confirm Password</label>
                    <Input
                        id="confirm-password"
                        type="password"
                        value={confirmPassword}
                        onChange={(e) => setConfirmPassword(e.target.value)}
                        className="bg-gray-700 border-gray-600 text-white w-full"
                        placeholder="Confirm new password"
                        required
                    />
                </div>
                <div className="flex justify-end gap-3 mt-6">
                    <Button type="button" variant="ghost" onClick={onClose} className="text-gray-300 hover:text-white hover:bg-gray-700 focus-visible:text-white focus-visible:bg-gray-700 focus-visible:ring-2 focus-visible:ring-primary focus-visible:outline-none">
                        Cancel
                    </Button>
                    <Button type="submit" disabled={resetMutation.isPending} className="bg-red-600 hover:bg-red-700 focus-visible:bg-red-700 focus-visible:ring-2 focus-visible:ring-red-500 focus-visible:outline-none text-white">
                        {resetMutation.isPending ? 'Resetting...' : 'Reset Password'}
                    </Button>
                </div>
            </form>
        </Modal>
    );
};
