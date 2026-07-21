import { useState } from 'react';
import { X, Plus, Trash2 } from 'lucide-react';
import { Combobox } from '../ui/Combobox';
import type { Library } from '../../types';

interface LibraryFormProps {
    initialData?: Library;
    onSubmit: (data: { name: string; type: string; paths: string[] }) => Promise<void>;
    onCancel: () => void;
    isLoading: boolean;
}

export function LibraryForm({ initialData, onSubmit, onCancel, isLoading }: LibraryFormProps) {
    const [name, setName] = useState(initialData?.name || '');
    const [type, setType] = useState<Library['type']>(initialData?.type || 'Movie');
    const [paths, setPaths] = useState<string[]>(initialData?.paths || []);
    const [newPath, setNewPath] = useState('');

    const libraryTypes = ['Movie', 'TV', 'Music', 'Book', 'Game', 'Photo'];

    const handleSubmit = async (e: React.FormEvent) => {
        e.preventDefault();
        if (paths.length === 0) {
            // Ideally show error
            return;
        }
        await onSubmit({ name, type, paths });
    };

    const addPath = () => {
        if (newPath && !paths.includes(newPath)) {
            setPaths([...paths, newPath]);
            setNewPath('');
        }
    };

    const removePath = (pathToRemove: string) => {
        setPaths(paths.filter(p => p !== pathToRemove));
    };

    return (
        <div className="fixed inset-0 bg-black/50 flex items-center justify-center z-50">
            <div className="bg-[#1a1a1a] border border-white/10 rounded-xl p-6 w-full max-w-md shadow-2xl">
                <div className="flex justify-between items-center mb-6">
                    <h2 className="text-xl font-bold text-white">{initialData ? 'Edit Library' : 'Add Library'}</h2>
                    <button onClick={onCancel} className="text-gray-400 hover:text-white">
                        <X size={24} />
                    </button>
                </div>

                <form onSubmit={handleSubmit} className="space-y-4">
                    <div>
                        <label className="block text-sm font-medium text-gray-300 mb-1">Name</label>
                        <input
                            type="text"
                            value={name}
                            onChange={(e) => setName(e.target.value)}
                            className="w-full bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none"
                            required
                        />
                    </div>

                    <div>
                        <label className="block text-sm font-medium text-gray-300 mb-1">Type</label>
                        <Combobox
                            value={type}
                            onChange={(val) => setType(val as Library['type'])}
                            options={libraryTypes}
                            placeholder="Select type..."
                            className="w-full"
                        />
                    </div>

                    <div>
                        <label className="block text-sm font-medium text-gray-300 mb-1">Folders</label>
                        <div className="space-y-2 mb-2">
                            {paths.map(path => (
                                <div key={path} className="flex items-center justify-between bg-white/5 px-3 py-2 rounded-lg">
                                    <span className="text-sm text-gray-300 truncate">{path}</span>
                                    <button
                                        type="button"
                                        onClick={() => removePath(path)}
                                        className="text-red-400 hover:text-red-300"
                                    >
                                        <Trash2 size={16} />
                                    </button>
                                </div>
                            ))}
                        </div>
                        <div className="flex gap-2">
                            <input
                                type="text"
                                value={newPath}
                                onChange={(e) => setNewPath(e.target.value)}
                                placeholder="C:\Media\Movies"
                                className="flex-1 bg-black/20 border border-white/10 rounded-lg px-4 py-2 text-white focus:border-primary/50 focus:outline-none text-sm"
                            />
                            <button
                                type="button"
                                onClick={addPath}
                                className="bg-white/10 hover:bg-white/20 text-white p-2 rounded-lg transition-colors"
                            >
                                <Plus size={20} />
                            </button>
                        </div>
                        <p className="text-xs text-gray-500 mt-1">Enter absolute path and click +</p>
                    </div>

                    <div className="flex justify-end gap-3 mt-6">
                        <button
                            type="button"
                            onClick={onCancel}
                            className="px-4 py-2 text-gray-300 hover:text-white transition-colors"
                        >
                            Cancel
                        </button>
                        <button
                            type="submit"
                            disabled={isLoading || paths.length === 0}
                            className="px-6 py-2 bg-primary hover:bg-primary/90 text-white rounded-lg font-medium transition-colors disabled:opacity-50"
                        >
                            {isLoading ? 'Saving...' : 'Save Library'}
                        </button>
                    </div>
                </form>
            </div>
        </div>
    );
}
