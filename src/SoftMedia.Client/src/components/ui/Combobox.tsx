import { useState, useRef, useEffect, useMemo, useId } from 'react';
import { ChevronDown, Check } from 'lucide-react';
import { cn } from '../../lib/utils';

interface ComboboxProps {
    value: string;
    onChange: (value: string) => void;
    options: string[];
    placeholder?: string;
    className?: string;
}

/**
 * SR-WI-051 — editable combobox following the WAI-ARIA combobox pattern
 * (role="combobox" input + role="listbox" popup, aria-activedescendant for
 * the keyboard-highlighted option).
 *
 * Keyboard map:
 *   ArrowDown  open the list / move the highlight down
 *   ArrowUp    open the list / move the highlight up
 *   Enter      select the highlighted option (falls through to form submit
 *              when nothing is highlighted)
 *   Escape     close the list (consumed, so an enclosing Modal stays open)
 */
export function Combobox({ value, onChange, options, placeholder, className }: ComboboxProps) {
    const [isOpen, setIsOpen] = useState(false);
    const [query, setQuery] = useState('');
    // Index into filteredOptions of the keyboard-highlighted option; -1 = none.
    const [highlighted, setHighlighted] = useState(-1);
    const containerRef = useRef<HTMLDivElement>(null);
    const inputRef = useRef<HTMLInputElement>(null);
    const listboxId = useId();

    const optionId = (index: number) => `${listboxId}-option-${index}`;

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
            setHighlighted(-1);
        }
    }, [isOpen, value]);

    // Keep the highlight inside the filtered list as the user types.
    useEffect(() => {
        setHighlighted(h => (h >= filteredOptions.length ? filteredOptions.length - 1 : h));
    }, [filteredOptions]);

    // Keep the highlighted option scrolled into view (no-op in jsdom).
    useEffect(() => {
        if (highlighted < 0) return;
        document.getElementById(optionId(highlighted))?.scrollIntoView?.({ block: 'nearest' });
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [highlighted]);

    const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
        setQuery(e.target.value);
        setIsOpen(true);
        setHighlighted(-1);
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

    const handleKeyDown = (e: React.KeyboardEvent<HTMLInputElement>) => {
        switch (e.key) {
            case 'ArrowDown': {
                e.preventDefault();
                if (!isOpen) {
                    setIsOpen(true);
                    setHighlighted(filteredOptions.length > 0 ? 0 : -1);
                    return;
                }
                if (filteredOptions.length === 0) return;
                setHighlighted(h => Math.min(h + 1, filteredOptions.length - 1));
                break;
            }
            case 'ArrowUp': {
                e.preventDefault();
                if (!isOpen) {
                    setIsOpen(true);
                    setHighlighted(filteredOptions.length - 1);
                    return;
                }
                if (filteredOptions.length === 0) return;
                setHighlighted(h => (h <= 0 ? 0 : h - 1));
                break;
            }
            case 'Enter': {
                if (isOpen && highlighted >= 0 && highlighted < filteredOptions.length) {
                    e.preventDefault();
                    handleSelect(filteredOptions[highlighted]);
                }
                break;
            }
            case 'Escape': {
                if (isOpen) {
                    // Consume the key so an enclosing dialog doesn't also close.
                    e.preventDefault();
                    e.stopPropagation();
                    setIsOpen(false);
                }
                break;
            }
        }
    };

    return (
        <div className={cn("relative w-full", className)} ref={containerRef}>
            <div className="relative">
                <input
                    ref={inputRef}
                    type="text"
                    role="combobox"
                    aria-expanded={isOpen}
                    aria-controls={listboxId}
                    aria-autocomplete="list"
                    aria-activedescendant={isOpen && highlighted >= 0 ? optionId(highlighted) : undefined}
                    value={query}
                    onChange={handleInputChange}
                    onFocus={handleInputFocus}
                    onKeyDown={handleKeyDown}
                    placeholder={placeholder}
                    className={cn(
                        "w-full bg-black/20 border border-white/10 rounded-lg px-4 py-2 pr-10 text-white focus:border-primary/50 focus:outline-none transition-colors",
                        "placeholder:text-gray-500"
                    )}
                />
                <button
                    type="button"
                    aria-label={isOpen ? 'Hide options' : 'Show options'}
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
                        <ul id={listboxId} role="listbox" className="py-1">
                            {filteredOptions.map((option, index) => (
                                <li
                                    key={option}
                                    id={optionId(index)}
                                    role="option"
                                    aria-selected={option === value}
                                    onClick={() => handleSelect(option)}
                                    onMouseEnter={() => setHighlighted(index)}
                                    className={cn(
                                        "px-4 py-2 text-sm cursor-pointer flex items-center justify-between group",
                                        option === value ? "bg-primary/20 text-primary" : "text-gray-300 hover:bg-white/5 hover:text-white",
                                        index === highlighted && (option === value ? "bg-primary/30" : "bg-white/5 text-white")
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
