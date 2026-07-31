import React, { useState } from 'react';
import { toast } from 'sonner';
import { extractApiError } from '../../services/apiError';
import { userService, type UserDto } from '../../services/userService';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Modal } from '../ui/Modal';

interface RatingsModalProps {
    isOpen: boolean;
    onClose: () => void;
    user: UserDto | null;
}

const MOVIE_RATINGS = ['G', 'PG', 'PG-13', 'R', 'NC-17'];
const TV_RATINGS = ['TV-Y', 'TV-Y7', 'TV-G', 'TV-PG', 'TV-14', 'TV-MA'];
const GAME_RATINGS = ['EC', 'E', 'E10+', 'T', 'M', 'AO']; // matches server RatingTables.Game

export const RatingsModal: React.FC<RatingsModalProps> = ({ isOpen, onClose, user }) => {
    const queryClient = useQueryClient();
    const [ratings, setRatings] = useState<Record<string, string>>({});

    // Reseed the form when the modal is pointed at a different user — during
    // render, not in an effect, so the previous user's ratings never flash for
    // a frame (react.dev: "adjusting state when props change").
    const [seededFor, setSeededFor] = useState<UserDto | null>(null);
    if (user && user !== seededFor) {
        setSeededFor(user);
        setRatings(user.contentRatings || {});
    }

    const updateMutation = useMutation({
        mutationFn: ({ userId, contentRatings }: { userId: string; contentRatings: Record<string, string> }) =>
            userService.updateUserRatings(userId, contentRatings),
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('Ratings updated successfully');
            onClose();
        },
        onError: (error: unknown) => {
            toast.error(extractApiError(error, 'Failed to update ratings'));
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!user) return;
        updateMutation.mutate({ userId: user.id, contentRatings: ratings });
    };

    const handleRatingChange = (type: string, value: string) => {
        setRatings(prev => ({
            ...prev,
            [type]: value
        }));
    };

    if (!isOpen || !user) return null;

    return (
        <Modal isOpen={isOpen} onClose={onClose} title={`Edit Content Ratings for ${user.username}`}>
            <form onSubmit={handleSubmit} className="space-y-4">
                <div>
                    <label htmlFor="edit-movie-rating" className="block text-sm font-medium text-gray-400 mb-1">Movies (MPAA)</label>
                    <select
                        id="edit-movie-rating"
                        value={ratings['Movie'] || ''}
                        onChange={(e) => handleRatingChange('Movie', e.target.value)}
                        className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                    >
                        <option value="">None (Unrestricted)</option>
                        {MOVIE_RATINGS.map(r => (
                            <option key={r} value={r}>{r}</option>
                        ))}
                    </select>
                </div>
                <div>
                    <label htmlFor="edit-tv-rating" className="block text-sm font-medium text-gray-400 mb-1">TV Shows</label>
                    <select
                        id="edit-tv-rating"
                        value={ratings['TV'] || ''}
                        onChange={(e) => handleRatingChange('TV', e.target.value)}
                        className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                    >
                        <option value="">None (Unrestricted)</option>
                        {TV_RATINGS.map(r => (
                            <option key={r} value={r}>{r}</option>
                        ))}
                    </select>
                </div>
                <div>
                    <label htmlFor="edit-game-rating" className="block text-sm font-medium text-gray-400 mb-1">Games (ESRB)</label>
                    <select
                        id="edit-game-rating"
                        value={ratings['Game'] || ''}
                        onChange={(e) => handleRatingChange('Game', e.target.value)}
                        className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                    >
                        <option value="">None (Unrestricted)</option>
                        {GAME_RATINGS.map(r => (
                            <option key={r} value={r}>{r}</option>
                        ))}
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
                        disabled={updateMutation.isPending}
                        className="px-4 py-2 rounded bg-[#007AFF] hover:bg-[#005BB5] text-white transition-colors disabled:opacity-50"
                    >
                        {updateMutation.isPending ? 'Saving...' : 'Save Ratings'}
                    </button>
                </div>
            </form>
        </Modal>
    );
};
