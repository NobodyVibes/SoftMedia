import { useState } from 'react';
import { Star } from 'lucide-react';
import { cn } from '../../lib/utils';

interface StarRatingProps {
    rating: number | null;
    onChange?: (rating: number) => void;
    readOnly?: boolean;
    size?: number;
}

export function StarRating({ rating, onChange, readOnly = false, size = 20 }: StarRatingProps) {
    const [hoverRating, setHoverRating] = useState<number | null>(null);

    const displayRating = hoverRating ?? rating ?? 0;

    return (
        <div className="flex items-center gap-1">
            {[1, 2, 3, 4, 5].map((star) => (
                <button
                    key={star}
                    type="button"
                    disabled={readOnly}
                    onClick={() => !readOnly && onChange?.(star)}
                    onMouseEnter={() => !readOnly && setHoverRating(star)}
                    onMouseLeave={() => !readOnly && setHoverRating(null)}
                    className={cn(
                        "transition-colors focus:outline-none",
                        readOnly ? "cursor-default" : "cursor-pointer hover:scale-110 transition-transform"
                    )}
                >
                    <Star
                        size={size}
                        className={cn(
                            "transition-all",
                            star <= displayRating
                                ? "fill-yellow-400 text-yellow-400"
                                : "fill-transparent text-gray-600"
                        )}
                    />
                </button>
            ))}
        </div>
    );
}
