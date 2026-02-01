import { useState } from 'react';
import { Star } from 'lucide-react';
import { cn } from '../../lib/utils';

interface StarRatingProps {
    rating: number | null;
    onChange?: (rating: number) => void;
    readOnly?: boolean;
    size?: number;
    max?: number;
    variant?: 'yellow' | 'gradient';
}

/**
 * Interpolates between two hex colors.
 */
function lerpColor(color1: string, color2: string, factor: number) {
    const hex = (x: number) => x.toString(16).padStart(2, '0');

    const r1 = parseInt(color1.substring(1, 3), 16);
    const g1 = parseInt(color1.substring(3, 5), 16);
    const b1 = parseInt(color1.substring(5, 7), 16);

    const r2 = parseInt(color2.substring(1, 3), 16);
    const g2 = parseInt(color2.substring(3, 5), 16);
    const b2 = parseInt(color2.substring(5, 7), 16);

    const r = Math.round(r1 + factor * (r2 - r1));
    const g = Math.round(g1 + factor * (g2 - g1));
    const b = Math.round(b1 + factor * (b2 - b1));

    return `#${hex(r)}${hex(g)}${hex(b)}`;
}

export function StarRating({
    rating,
    onChange,
    readOnly = false,
    size = 20,
    max = 5,
    variant = 'gradient' // Default to gradient as requested for "Your Rating"
}: StarRatingProps) {
    const [hoverRating, setHoverRating] = useState<number | null>(null);

    const displayRating = hoverRating ?? rating ?? 0;

    // Brand Gradient: Bright Blue (#007AFF) to Violet (#8A2BE2)
    const startColor = '#007AFF';
    const endColor = '#8A2BE2';

    return (
        <div className="flex items-center gap-1">
            {Array.from({ length: max }, (_, i) => i + 1).map((star, index) => {
                const isActive = star <= displayRating;

                // Calculate star color for gradient variant
                const starColor = variant === 'gradient'
                    ? lerpColor(startColor, endColor, index / (max - 1))
                    : '#facc15'; // yellow-400 fallback for 'yellow' variant

                return (
                    <button
                        key={star}
                        type="button"
                        disabled={readOnly}
                        onClick={() => !readOnly && onChange?.(star)}
                        onMouseEnter={() => !readOnly && setHoverRating(star)}
                        onMouseLeave={() => !readOnly && setHoverRating(null)}
                        className={cn(
                            "transition-all duration-200 focus:outline-none",
                            readOnly ? "cursor-default" : "cursor-pointer hover:scale-125 transition-transform"
                        )}
                        style={{ color: isActive ? starColor : undefined }}
                    >
                        <Star
                            size={size}
                            className={cn(
                                "transition-all",
                                isActive
                                    ? "fill-current"
                                    : "fill-transparent text-gray-600 opacity-40"
                            )}
                            style={isActive ? { color: starColor, fill: starColor } : {}}
                        />
                    </button>
                );
            })}
        </div>
    );
}
