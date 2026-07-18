import React, { useState } from 'react';
import { toast } from 'sonner';
import { userService } from '../../services/userService';
import { useMutation, useQueryClient } from '@tanstack/react-query';

interface CreateUserModalProps {
    isOpen: boolean;
    onClose: () => void;
}

// Same vocabulary as RatingsModal — the two surfaces must never drift apart.
const MOVIE_RATINGS = ['G', 'PG', 'PG-13', 'R', 'NC-17'];
const TV_RATINGS = ['TV-Y', 'TV-Y7', 'TV-G', 'TV-PG', 'TV-14', 'TV-MA'];
const GAME_RATINGS = ['EC', 'E', 'E10+', 'T', 'M', 'AO']; // matches server RatingTables.Game

export const CreateUserModal: React.FC<CreateUserModalProps> = ({ isOpen, onClose }) => {
    const queryClient = useQueryClient();
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [role, setRole] = useState('User');
    const [firstName, setFirstName] = useState('');
    const [lastName, setLastName] = useState('');
    // R-WI-011: content ceilings are VISIBLE at creation and default to unrestricted —
    // new users are never capped unless the admin picks a limit here (or later via Edit Ratings).
    const [ratings, setRatings] = useState<Record<string, string>>({});

    const createMutation = useMutation({
        mutationFn: userService.createUser,
        onSuccess: () => {
            queryClient.invalidateQueries({ queryKey: ['users'] });
            toast.success('User created successfully');
            onClose();
            setUsername('');
            setPassword('');
            setRole('User');
            setFirstName('');
            setLastName('');
            setRatings({});
        },
        onError: (error: any) => {
            toast.error(error.response?.data || 'Failed to create user');
        },
    });

    const handleSubmit = (e: React.FormEvent) => {
        e.preventDefault();
        if (!username || !password || !firstName || !lastName) {
            toast.error('All fields are required');
            return;
        }
        const contentRatings = Object.fromEntries(Object.entries(ratings).filter(([, v]) => v));
        createMutation.mutate({
            username, password, role, firstName, lastName,
            ...(Object.keys(contentRatings).length ? { contentRatings } : {}),
        });
    };

    const setRating = (type: string, value: string) => setRatings(prev => ({ ...prev, [type]: value }));

    if (!isOpen) return null;

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
            <div className="bg-gray-800 rounded-lg p-6 w-full max-w-md border border-gray-700">
                <h2 className="text-xl font-bold text-white mb-4">Create New User</h2>
                <form onSubmit={handleSubmit} className="space-y-4">
                    <div className="grid grid-cols-2 gap-4">
                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1">First Name</label>
                            <input
                                type="text"
                                value={firstName}
                                onChange={(e) => setFirstName(e.target.value)}
                                className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                                placeholder="First Name"
                            />
                        </div>
                        <div>
                            <label className="block text-sm font-medium text-gray-400 mb-1">Last Name</label>
                            <input
                                type="text"
                                value={lastName}
                                onChange={(e) => setLastName(e.target.value)}
                                className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                                placeholder="Last Name"
                            />
                        </div>
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Username</label>
                        <input
                            type="text"
                            value={username}
                            onChange={(e) => setUsername(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                            placeholder="Enter username"
                        />
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Password</label>
                        <input
                            type="password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                            placeholder="Enter password"
                        />
                    </div>
                    <div>
                        <label className="block text-sm font-medium text-gray-400 mb-1">Role</label>
                        <select
                            value={role}
                            onChange={(e) => setRole(e.target.value)}
                            className="w-full bg-gray-700 border border-gray-600 rounded px-3 py-2 text-white focus:outline-none focus:border-[#007AFF]"
                        >
                            <option value="User">User</option>
                            <option value="Admin">Admin</option>
                        </select>
                    </div>

                    {/* R-WI-011: visible content limits at creation. Default = No limit for every type. */}
                    <fieldset className="border border-white/10 rounded-lg p-3">
                        <legend className="text-sm font-medium text-gray-400 px-1">Content limits</legend>
                        <p className="text-xs text-gray-500 mb-3">
                            New users have <span className="text-gray-300">no content restrictions</span> unless you set limits here (you can change them later via Edit Ratings). Admins always see everything.
                        </p>
                        <div className="grid grid-cols-3 gap-3">
                            <div>
                                <label htmlFor="create-movie-rating" className="block text-xs font-medium text-gray-400 mb-1">Movies</label>
                                <select
                                    id="create-movie-rating"
                                    value={ratings['Movie'] || ''}
                                    onChange={(e) => setRating('Movie', e.target.value)}
                                    className="w-full bg-gray-700 border border-gray-600 rounded px-2 py-2 text-white text-sm focus:outline-none focus:border-[#007AFF]"
                                >
                                    <option value="">No limit</option>
                                    {MOVIE_RATINGS.map(r => <option key={r} value={r}>{r}</option>)}
                                </select>
                            </div>
                            <div>
                                <label htmlFor="create-tv-rating" className="block text-xs font-medium text-gray-400 mb-1">TV</label>
                                <select
                                    id="create-tv-rating"
                                    value={ratings['TV'] || ''}
                                    onChange={(e) => setRating('TV', e.target.value)}
                                    className="w-full bg-gray-700 border border-gray-600 rounded px-2 py-2 text-white text-sm focus:outline-none focus:border-[#007AFF]"
                                >
                                    <option value="">No limit</option>
                                    {TV_RATINGS.map(r => <option key={r} value={r}>{r}</option>)}
                                </select>
                            </div>
                            <div>
                                <label htmlFor="create-game-rating" className="block text-xs font-medium text-gray-400 mb-1">Games</label>
                                <select
                                    id="create-game-rating"
                                    value={ratings['Game'] || ''}
                                    onChange={(e) => setRating('Game', e.target.value)}
                                    className="w-full bg-gray-700 border border-gray-600 rounded px-2 py-2 text-white text-sm focus:outline-none focus:border-[#007AFF]"
                                >
                                    <option value="">No limit</option>
                                    {GAME_RATINGS.map(r => <option key={r} value={r}>{r}</option>)}
                                </select>
                            </div>
                        </div>
                    </fieldset>

                    <div className="flex justify-end gap-2 pt-4">
                        <button
                            type="button"
                            onClick={onClose}
                            className="px-4 py-2 rounded text-gray-300 hover:bg-gray-700 focus-visible:bg-gray-700 focus-visible:ring-2 focus-visible:ring-blue-400 focus-visible:outline-none transition-colors"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={createMutation.isPending}
                            className="px-4 py-2 rounded bg-[#007AFF] hover:bg-[#005BB5] focus-visible:bg-[#005BB5] focus-visible:ring-2 focus-visible:ring-white focus-visible:outline-none text-white transition-colors disabled:opacity-50"
                        >
                            {createMutation.isPending ? 'Creating...' : 'Create User'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
};
