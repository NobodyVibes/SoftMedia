import React from 'react';
import { Modal } from '../ui/Modal';

interface ConfirmationModalProps {
    isOpen: boolean;
    title: string;
    message: string;
    confirmText?: string;
    cancelText?: string;
    onConfirm: () => void;
    onCancel: () => void;
    variant?: 'default' | 'danger';
}

export const ConfirmationModal: React.FC<ConfirmationModalProps> = ({
    isOpen,
    title,
    message,
    confirmText = 'Confirm',
    cancelText = 'Cancel',
    onConfirm,
    onCancel,
    variant = 'default',
}) => {
    return (
        <Modal
            isOpen={isOpen}
            onClose={onCancel}
            title={title}
            panelClassName="bg-gray-800 rounded-lg shadow-xl max-w-md w-full mx-4 p-6"
            // Destructive confirmations must not be dismissible by a stray
            // backdrop click — Escape / Cancel remain available.
            closeOnBackdrop={variant !== 'danger'}
        >
            <p className="text-gray-300 mb-6">{message}</p>
            <div className="flex justify-end gap-3">
                <button
                    onClick={onCancel}
                    className="px-4 py-2 bg-gray-700 text-white rounded hover:bg-gray-600 transition-colors"
                >
                    {cancelText}
                </button>
                <button
                    onClick={onConfirm}
                    className={`px-4 py-2 rounded transition-colors ${variant === 'danger'
                            ? 'bg-red-600 hover:bg-red-700 text-white'
                            : 'bg-gradient-to-r from-blue-500 to-violet-600 hover:from-blue-600 hover:to-violet-700 text-white'
                        }`}
                >
                    {confirmText}
                </button>
            </div>
        </Modal>
    );
};
