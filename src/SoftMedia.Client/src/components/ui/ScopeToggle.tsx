/**
 * Two-state segmented control used by the Most Watched home row to switch its
 * ranking between every user on the server and the signed-in user alone.
 *
 * Rendered as a radiogroup rather than a pair of buttons so keyboard and screen
 * reader users get the "one of two" semantics for free; arrow keys move between
 * options because both radios sit in the same group.
 */
interface ScopeToggleOption<T extends string> {
    value: T;
    label: string;
}

interface ScopeToggleProps<T extends string> {
    value: T;
    options: readonly [ScopeToggleOption<T>, ScopeToggleOption<T>];
    onChange: (value: T) => void;
    /** Accessible name for the group, e.g. "Most watched scope". */
    label: string;
    /** Disables interaction while a scope change is in flight. */
    disabled?: boolean;
}

export default function ScopeToggle<T extends string>({
    value,
    options,
    onChange,
    label,
    disabled = false,
}: ScopeToggleProps<T>) {
    return (
        <div
            role="radiogroup"
            aria-label={label}
            className="inline-flex items-center rounded-full bg-white/5 ring-1 ring-white/10 p-0.5"
        >
            {options.map(option => {
                const selected = option.value === value;
                return (
                    <button
                        key={option.value}
                        type="button"
                        role="radio"
                        aria-checked={selected}
                        disabled={disabled}
                        onClick={() => !selected && onChange(option.value)}
                        className={[
                            'px-3 py-1 text-xs font-semibold rounded-full transition-colors',
                            'focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-400',
                            'disabled:opacity-50 disabled:cursor-not-allowed',
                            selected
                                ? 'bg-white/15 text-white'
                                : 'text-gray-400 hover:text-white',
                        ].join(' ')}
                    >
                        {option.label}
                    </button>
                );
            })}
        </div>
    );
}
