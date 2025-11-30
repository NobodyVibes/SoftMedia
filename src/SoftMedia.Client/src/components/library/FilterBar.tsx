import { Input } from '../ui/Input';

interface FilterBarProps {
    search: string;
    onSearchChange: (value: string) => void;
    sortBy: string;
    onSortChange: (value: string) => void;
}

export default function FilterBar({ search, onSearchChange, sortBy, onSortChange }: FilterBarProps) {
    return (
        <div className="flex flex-col sm:flex-row gap-4 mb-6">
            <div className="flex-1">
                <Input
                    placeholder="Search library..."
                    value={search}
                    onChange={(e) => onSearchChange(e.target.value)}
                    className="bg-slate-800 border-slate-700"
                />
            </div>
            <div className="w-full sm:w-48">
                <select
                    value={sortBy}
                    onChange={(e) => onSortChange(e.target.value)}
                    className="w-full h-10 rounded-md border border-slate-700 bg-slate-800 px-3 py-2 text-sm text-white focus:outline-none focus:ring-2 focus:ring-primary"
                >
                    <option value="dateAdded_desc">Recently Added</option>
                    <option value="title_asc">Title (A-Z)</option>
                    <option value="year_desc">Year (Newest)</option>
                    <option value="year_asc">Year (Oldest)</option>
                </select>
            </div>
        </div>
    );
}
