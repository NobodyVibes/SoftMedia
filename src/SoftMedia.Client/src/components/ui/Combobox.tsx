import { useState, useRef, useEffect, useMemo } from 'react';
import { ChevronDown, Check } from 'lucide-react';
import { cn } from '../../lib/utils';

interface ComboboxProps {
    value: string;
    onChange: (value: string) => void;
    options: string[];
    placeholder?: string;
    className?: string;
}

export function Combobox({ value, onChange, options, placeholder, className }: ComboboxProps) {
    const [isOpen, setIsOpen] = useState(false);
    const [query, setQuery] = useState('');
    const containerRef = useRef<HTMLDivElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);

    // Filter options based on query
    const filteredOptions = useMemo(() => {
        // If query is empty OR matches the currently selected value, show all options
        // This allows the user to see the full list without clearing the input
        if (query === '' || query === value) return options;

        return options.filter((option) =>
            option.toLowerCase().includes(query.toLowerCase())
        );
    }, [query, options, value]);

    // Handle outside click to close dropdown
    useEffect(() => {
        const handleClickOutside = (event: MouseEvent) => {
            if (containerRef.current && !containerRef.current.contains(event.target as Node)) {
                setIsOpen(false);
            }
        };

        document.addEventListener('mousedown', handleClickOutside);
        return () => document.removeEventListener('mousedown', handleClickOutside);
    }, []);

    // Sync query with value when closed or value changes externally
    useEffect(() => {
        if (!isOpen) {
            setQuery(value);
        }
    }, [isOpen, value]);

    const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        setQuery(e.target.value);
        setIsOpen(true);
    };

    const handleSelect = (option: string) => {
        onChange(option);
        setQuery(option);
        setIsOpen(false);
        inputRef.current?.blur();
    };

    const handleInputFocus = () => {
        setIsOpen(true);
    };

    return (
        <div className={cn("relative w-full", className)} ref={containerRef}>
            <div className="relative">
                <input
                    ref={inputRef}
                    type="text"
                    value={query}
                    onChange={handleInputChange}
                    onFocus={handleInputFocus}
                    placeholder={placeholder}
                    className={cn(
                        "w-full bg-black/20 border border-white/10 rounded-lg px-4 py-2 pr-10 text-white focus:border-primary/50 focus:outline-none transition-colors",
                        "placeholder:text-gray-500"
                    )}
                />
                <button
                    onClick={() => {
                        if (isOpen) {
                            setIsOpen(false);
                        } else {
                            setIsOpen(true);
                            inputRef.current?.focus();
                        }
                    }}
                    className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-gray-400 hover:text-white transition-colors"
                    tabIndex={-1}
                >
                    <ChevronDown size={16} className={cn("transition-transform", isOpen && "rotate-180")} />
                </button>
            </div>

            {isOpen && (
                <div className="absolute z-50 w-full mt-1 bg-[#1a1a1a] border border-white/10 rounded-lg shadow-xl max-h-60 overflow-y-auto custom-scrollbar">
                    {filteredOptions.length === 0 ? (
                        <div className="px-4 py-2 text-sm text-gray-500">No options found.</div>
                    ) : (
                        <ul className="py-1">
                            {filteredOptions.map((option) => (
                                <li
                                    key={option}
                                    onClick={() => handleSelect(option)}
                                    className={cn(
                                        "px-4 py-2 text-sm cursor-pointer flex items-center justify-between group",
                                        option === value ? "bg-primary/20 text-primary" : "text-gray-300 hover:bg-white/5 hover:text-white"
                                    )}
                                >
                                    <span>{option}</span>
                                    {option === value && <Check size={14} className="text-primary" />}
                                </li>
                            ))}
                        </ul>
                    )}
                </div>
            )}
        </div>
    );
}
