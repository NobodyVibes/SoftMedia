/**
 * Genre Colors Utility
 * 
 * Centralized configuration for genre-specific colors used across:
 * - MediaCard hover glow effects
 * - Genre tag pills in MediaCard
 * - Genre tag pills in MediaDetailLayout
 * 
 * Features smart matching for genre variations:
 * - "Drama Film" → matches "Drama"
 * - "Speculative Fiction" → matches "Sci-Fi"
 * - "Action-Adventure" → matches "Action" or "Adventure"
 */



// Genre aliases - map variations to base genres
const GENRE_ALIASES: Record<string, string> = {
    // Sci-Fi variations
    'science fiction': 'Sci-Fi',
    'scifi': 'Sci-Fi',
    'sf': 'Sci-Fi',
    'speculative fiction': 'Sci-Fi',
    'speculative': 'Sci-Fi',
    'futuristic': 'Sci-Fi',

    // Action/Adventure
    'action-adventure': 'Action',
    'actionadventure': 'Action',

    // Sports
    'sports': 'Sport',
    'sporting': 'Sport',

    // Animation
    'animated': 'Animation',
    'cartoon': 'Animation',
    'cgi': 'Animation',

    // Documentary
    'doc': 'Documentary',
    'docudrama': 'Documentary',
    'true crime': 'Crime',

    // Kids/Family
    'children': 'Kids',
    "children's": 'Kids',
    'childrens': 'Kids',
    'child': 'Kids',

    // Horror sub-genres → Horror
    'slasher': 'Horror',
    'gore': 'Horror',
    'splatter': 'Horror',
    'creature': 'Horror',
    'monster': 'Horror',
    'paranormal': 'Supernatural',
    'ghost': 'Supernatural',
    'haunted': 'Supernatural',
    'occult': 'Supernatural',

    // Thriller variations
    'suspense': 'Thriller',
    'psychological thriller': 'Thriller',
    'crime thriller': 'Thriller',

    // Comedy variations  
    'comedic': 'Comedy',
    'comic': 'Comedy',
    'farce': 'Comedy',
    'slapstick': 'Comedy',
    'dark comedy': 'Comedy',
    'black comedy': 'Comedy',
    'romantic comedy': 'Romance',
    'romcom': 'Romance',
    'rom-com': 'Romance',

    // Drama variations
    'dramatic': 'Drama',
    'melodrama': 'Drama',
    'social drama': 'Drama',
    'family drama': 'Drama',

    // War
    'military': 'War',
    'combat': 'War',
    'war film': 'War',

    // Crime
    'gangster': 'Crime',
    'mafia': 'Crime',
    'mob': 'Crime',
    'detective': 'Mystery',
    'whodunit': 'Mystery',

    // Spy/Espionage
    'spy': 'Spy',
    'espionage': 'Spy',
    'secret agent': 'Spy',
    'spy thriller': 'Spy',
    'spy action': 'Spy',
    'agent': 'Spy',

    // Holiday/Seasonal
    'christmas': 'Christmas',
    'holiday': 'Holiday',
    'xmas': 'Christmas',
    'halloween': 'Horror',
    'festive': 'Holiday',

    // Historical
    'period': 'History',
    'period piece': 'History',
    'period drama': 'History',
    'historical': 'History',
    'historical drama': 'History',
    'costume drama': 'History',
    'biographical': 'Biography',
    'biopic': 'Biography',
    'bio': 'Biography',

    // Fantasy variations
    'sword and sorcery': 'Fantasy',
    'high fantasy': 'Fantasy',
    'dark fantasy': 'Fantasy',
    'urban fantasy': 'Fantasy',
    'magical': 'Fantasy',
    'magic': 'Fantasy',
    'fairy tale': 'Fantasy',
    'fairytale': 'Fantasy',
    'myth': 'Mythology',
    'mythological': 'Mythology',
    'mythology': 'Fantasy',
    'legend': 'Fantasy',

    // Post-apocalyptic/Dystopia → Sci-Fi
    'post-apocalyptic': 'Sci-Fi',
    'postapocalyptic': 'Sci-Fi',
    'apocalyptic': 'Sci-Fi',
    'dystopia': 'Sci-Fi',
    'dystopian': 'Sci-Fi',
    'cyberpunk': 'Sci-Fi',
    'steampunk': 'Sci-Fi',

    // Superhero → Action
    'superhero': 'Action',
    'super hero': 'Action',
    'comic book': 'Action',
    'marvel': 'Action',
    'dc': 'Action',

    // Nature/Wildlife
    'nature': 'Documentary',
    'wildlife': 'Documentary',
    'natural history': 'Documentary',
    'travel': 'Documentary',

    // Music-related
    'music': 'Musical',
    'concert': 'Musical',
    'music video': 'Musical',
    'opera': 'Musical',

    // Reality TV
    'reality tv': 'Reality',
    'reality show': 'Reality',
    'competition': 'Reality',
    'contest': 'Game Show',
    'quiz': 'Game Show',
    'game': 'Game Show',

    // Talk shows
    'talk': 'Talk Show',
    'interview': 'Talk Show',
    'chat show': 'Talk Show',

    // Anime-related
    'japanese animation': 'Anime',
    'manga': 'Anime',
    'shonen': 'Anime',
    'shojo': 'Anime',
    'seinen': 'Anime',
    'isekai': 'Anime',
};

// Words to strip from genre names when normalizing
const STRIP_WORDS = [
    'film',
    'films',
    'movie',
    'movies',
    'show',
    'shows',
    'series',
    'television',
    'tv',
    'program',
    'programme',
    'genre',
    'type',
    'style',
    'based',
    'inspired',
];

// Gradient classes for hover glow effects (Tailwind CSS)
const genreGradients: Record<string, string> = {
    'Fantasy': 'from-purple-600 to-pink-600',
    'Action': 'from-red-600 to-orange-600',
    'Horror': 'from-red-900 to-black',
    'Comedy': 'from-yellow-400 to-orange-500',
    'Drama': 'from-blue-600 to-indigo-600',
    'Sci-Fi': 'from-cyan-500 to-blue-600',
    'Thriller': 'from-emerald-600 to-teal-700',
    'Animation': 'from-pink-500 to-rose-500',
    'Mystery': 'from-violet-600 to-indigo-700',
    'Adventure': 'from-green-500 to-teal-500',
    'Crime': 'from-slate-700 to-red-800',
    'Romance': 'from-rose-400 to-pink-500',
    'Documentary': 'from-amber-500 to-orange-600',
    'War': 'from-gray-600 to-red-900',
    'Family': 'from-sky-400 to-blue-500',
    'Musical': 'from-fuchsia-500 to-pink-500',
    'Western': 'from-amber-600 to-yellow-700',
    'History': 'from-amber-700 to-orange-800',
    'Sport': 'from-lime-500 to-green-600',
    'Biography': 'from-teal-500 to-cyan-600',
    'Kids': 'from-sky-400 to-blue-500',
    'Reality': 'from-orange-500 to-amber-600',
    'News': 'from-gray-500 to-slate-600',
    'Talk Show': 'from-indigo-500 to-purple-600',
    'Game Show': 'from-yellow-500 to-amber-600',
    'Anime': 'from-pink-500 to-purple-600',
    'Supernatural': 'from-indigo-800 to-purple-900',
    'Nature': 'from-green-600 to-emerald-700',
    'Spy': 'from-gray-700 to-slate-900',
    'Espionage': 'from-gray-700 to-slate-900',
    'Sitcom': 'from-yellow-400 to-orange-500',
    'Political': 'from-red-700 to-blue-700',
    'Psychological': 'from-purple-800 to-indigo-900',
    'Parody': 'from-yellow-500 to-pink-500',
    'Satire': 'from-amber-500 to-rose-500',
    'Noir': 'from-gray-800 to-black',
    'Martial Arts': 'from-red-600 to-amber-600',
    'Disaster': 'from-orange-600 to-red-700',
    'Superhero': 'from-blue-600 to-red-600',
    'Heist': 'from-amber-600 to-slate-700',
    'Legal': 'from-blue-700 to-slate-700',
    'Courtroom': 'from-blue-700 to-slate-700',
    'Medical': 'from-teal-600 to-blue-600',
    'Epic': 'from-amber-700 to-yellow-500',
    'Mythology': 'from-amber-600 to-purple-700',
    'Holiday': 'from-red-600 to-green-600',
    'Christmas': 'from-red-600 to-green-600',
};

// Background and text colors for genre tags (Tailwind CSS classes)
export interface GenreTagColors {
    bg: string;       // Background color class
    text: string;     // Text color class
    hoverBg: string;  // Hover background color class
    border?: string;  // Optional border color class
}

const genreTagColors: Record<string, GenreTagColors> = {
    'Fantasy': {
        bg: 'bg-purple-500/20',
        text: 'text-purple-300',
        hoverBg: 'hover:bg-purple-500/30',
        border: 'border-purple-500/30',
    },
    'Action': {
        bg: 'bg-red-500/20',
        text: 'text-red-300',
        hoverBg: 'hover:bg-red-500/30',
        border: 'border-red-500/30',
    },
    'Horror': {
        bg: 'bg-red-900/30',
        text: 'text-red-400',
        hoverBg: 'hover:bg-red-900/40',
        border: 'border-red-900/40',
    },
    'Comedy': {
        bg: 'bg-yellow-500/20',
        text: 'text-yellow-300',
        hoverBg: 'hover:bg-yellow-500/30',
        border: 'border-yellow-500/30',
    },
    'Drama': {
        bg: 'bg-blue-500/20',
        text: 'text-blue-300',
        hoverBg: 'hover:bg-blue-500/30',
        border: 'border-blue-500/30',
    },
    'Sci-Fi': {
        bg: 'bg-cyan-500/20',
        text: 'text-cyan-300',
        hoverBg: 'hover:bg-cyan-500/30',
        border: 'border-cyan-500/30',
    },
    'Thriller': {
        bg: 'bg-emerald-500/20',
        text: 'text-emerald-300',
        hoverBg: 'hover:bg-emerald-500/30',
        border: 'border-emerald-500/30',
    },
    'Animation': {
        bg: 'bg-pink-500/20',
        text: 'text-pink-300',
        hoverBg: 'hover:bg-pink-500/30',
        border: 'border-pink-500/30',
    },
    'Mystery': {
        bg: 'bg-violet-500/20',
        text: 'text-violet-300',
        hoverBg: 'hover:bg-violet-500/30',
        border: 'border-violet-500/30',
    },
    'Adventure': {
        bg: 'bg-green-500/20',
        text: 'text-green-300',
        hoverBg: 'hover:bg-green-500/30',
        border: 'border-green-500/30',
    },
    'Crime': {
        bg: 'bg-slate-500/20',
        text: 'text-slate-300',
        hoverBg: 'hover:bg-slate-500/30',
        border: 'border-slate-500/30',
    },
    'Romance': {
        bg: 'bg-rose-500/20',
        text: 'text-rose-300',
        hoverBg: 'hover:bg-rose-500/30',
        border: 'border-rose-500/30',
    },
    'Documentary': {
        bg: 'bg-amber-500/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-500/30',
        border: 'border-amber-500/30',
    },
    'War': {
        bg: 'bg-gray-500/20',
        text: 'text-gray-300',
        hoverBg: 'hover:bg-gray-500/30',
        border: 'border-gray-500/30',
    },
    'Family': {
        bg: 'bg-sky-500/20',
        text: 'text-sky-300',
        hoverBg: 'hover:bg-sky-500/30',
        border: 'border-sky-500/30',
    },
    'Musical': {
        bg: 'bg-fuchsia-500/20',
        text: 'text-fuchsia-300',
        hoverBg: 'hover:bg-fuchsia-500/30',
        border: 'border-fuchsia-500/30',
    },
    'Western': {
        bg: 'bg-amber-600/20',
        text: 'text-amber-400',
        hoverBg: 'hover:bg-amber-600/30',
        border: 'border-amber-600/30',
    },
    'History': {
        bg: 'bg-amber-700/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-700/30',
        border: 'border-amber-700/30',
    },
    'Sport': {
        bg: 'bg-lime-500/20',
        text: 'text-lime-300',
        hoverBg: 'hover:bg-lime-500/30',
        border: 'border-lime-500/30',
    },
    'Biography': {
        bg: 'bg-teal-500/20',
        text: 'text-teal-300',
        hoverBg: 'hover:bg-teal-500/30',
        border: 'border-teal-500/30',
    },
    'Kids': {
        bg: 'bg-sky-500/20',
        text: 'text-sky-300',
        hoverBg: 'hover:bg-sky-500/30',
        border: 'border-sky-500/30',
    },
    'Reality': {
        bg: 'bg-orange-500/20',
        text: 'text-orange-300',
        hoverBg: 'hover:bg-orange-500/30',
        border: 'border-orange-500/30',
    },
    'News': {
        bg: 'bg-gray-500/20',
        text: 'text-gray-300',
        hoverBg: 'hover:bg-gray-500/30',
        border: 'border-gray-500/30',
    },
    'Talk Show': {
        bg: 'bg-indigo-500/20',
        text: 'text-indigo-300',
        hoverBg: 'hover:bg-indigo-500/30',
        border: 'border-indigo-500/30',
    },
    'Game Show': {
        bg: 'bg-yellow-500/20',
        text: 'text-yellow-300',
        hoverBg: 'hover:bg-yellow-500/30',
        border: 'border-yellow-500/30',
    },
    'Anime': {
        bg: 'bg-pink-500/20',
        text: 'text-pink-300',
        hoverBg: 'hover:bg-pink-500/30',
        border: 'border-pink-500/30',
    },
    'Supernatural': {
        bg: 'bg-indigo-800/20',
        text: 'text-indigo-300',
        hoverBg: 'hover:bg-indigo-800/30',
        border: 'border-indigo-800/30',
    },
    'Nature': {
        bg: 'bg-green-600/20',
        text: 'text-green-300',
        hoverBg: 'hover:bg-green-600/30',
        border: 'border-green-600/30',
    },
    'Spy': {
        bg: 'bg-gray-600/20',
        text: 'text-gray-300',
        hoverBg: 'hover:bg-gray-600/30',
        border: 'border-gray-600/30',
    },
    'Espionage': {
        bg: 'bg-gray-600/20',
        text: 'text-gray-300',
        hoverBg: 'hover:bg-gray-600/30',
        border: 'border-gray-600/30',
    },
    'Sitcom': {
        bg: 'bg-yellow-500/20',
        text: 'text-yellow-300',
        hoverBg: 'hover:bg-yellow-500/30',
        border: 'border-yellow-500/30',
    },
    'Political': {
        bg: 'bg-red-600/20',
        text: 'text-red-300',
        hoverBg: 'hover:bg-red-600/30',
        border: 'border-red-600/30',
    },
    'Psychological': {
        bg: 'bg-purple-700/20',
        text: 'text-purple-300',
        hoverBg: 'hover:bg-purple-700/30',
        border: 'border-purple-700/30',
    },
    'Parody': {
        bg: 'bg-yellow-500/20',
        text: 'text-yellow-300',
        hoverBg: 'hover:bg-yellow-500/30',
        border: 'border-yellow-500/30',
    },
    'Satire': {
        bg: 'bg-amber-500/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-500/30',
        border: 'border-amber-500/30',
    },
    'Noir': {
        bg: 'bg-gray-700/20',
        text: 'text-gray-300',
        hoverBg: 'hover:bg-gray-700/30',
        border: 'border-gray-700/30',
    },
    'Martial Arts': {
        bg: 'bg-red-500/20',
        text: 'text-red-300',
        hoverBg: 'hover:bg-red-500/30',
        border: 'border-red-500/30',
    },
    'Disaster': {
        bg: 'bg-orange-600/20',
        text: 'text-orange-300',
        hoverBg: 'hover:bg-orange-600/30',
        border: 'border-orange-600/30',
    },
    'Superhero': {
        bg: 'bg-blue-500/20',
        text: 'text-blue-300',
        hoverBg: 'hover:bg-blue-500/30',
        border: 'border-blue-500/30',
    },
    'Heist': {
        bg: 'bg-amber-600/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-600/30',
        border: 'border-amber-600/30',
    },
    'Legal': {
        bg: 'bg-blue-600/20',
        text: 'text-blue-300',
        hoverBg: 'hover:bg-blue-600/30',
        border: 'border-blue-600/30',
    },
    'Courtroom': {
        bg: 'bg-blue-600/20',
        text: 'text-blue-300',
        hoverBg: 'hover:bg-blue-600/30',
        border: 'border-blue-600/30',
    },
    'Medical': {
        bg: 'bg-teal-500/20',
        text: 'text-teal-300',
        hoverBg: 'hover:bg-teal-500/30',
        border: 'border-teal-500/30',
    },
    'Epic': {
        bg: 'bg-amber-600/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-600/30',
        border: 'border-amber-600/30',
    },
    'Mythology': {
        bg: 'bg-amber-500/20',
        text: 'text-amber-300',
        hoverBg: 'hover:bg-amber-500/30',
        border: 'border-amber-500/30',
    },
    'Holiday': {
        bg: 'bg-red-500/20',
        text: 'text-red-300',
        hoverBg: 'hover:bg-red-500/30',
        border: 'border-red-500/30',
    },
    'Christmas': {
        bg: 'bg-red-500/20',
        text: 'text-red-300',
        hoverBg: 'hover:bg-red-500/30',
        border: 'border-red-500/30',
    },
};

// Default colors for unknown genres
const defaultGradient = 'from-blue-600 to-violet-600';
const defaultTagColors: GenreTagColors = {
    bg: 'bg-white/10',
    text: 'text-gray-200',
    hoverBg: 'hover:bg-white/20',
    border: 'border-white/10',
};

// Cache for normalized genre lookups
const genreMatchCache = new Map<string, string | null>();

/**
 * Normalize a genre string by removing common suffixes and converting to lowercase
 */
function normalizeGenre(genre: string): string {
    let normalized = genre.toLowerCase().trim();

    // Remove common suffixes/words
    for (const word of STRIP_WORDS) {
        // Match word at end with optional 's'
        normalized = normalized.replace(new RegExp(`\\s*${word}s?\\s*$`, 'gi'), '');
        // Match word at start
        normalized = normalized.replace(new RegExp(`^${word}s?\\s*`, 'gi'), '');
        // Match word in middle
        normalized = normalized.replace(new RegExp(`\\s+${word}s?\\s+`, 'gi'), ' ');
    }

    // Clean up whitespace and hyphens
    normalized = normalized.replace(/[-_]/g, ' ').replace(/\s+/g, ' ').trim();

    return normalized;
}

/**
 * Find the best matching base genre for a given genre string
 * Uses multiple strategies:
 * 1. Exact match (case-insensitive)
 * 2. Alias lookup
 * 3. Normalized match
 * 4. Keyword extraction
 */
function findBaseGenre(genre: string): string | null {
    // Check cache first
    const cacheKey = genre.toLowerCase();
    if (genreMatchCache.has(cacheKey)) {
        return genreMatchCache.get(cacheKey)!;
    }

    const lowerGenre = genre.toLowerCase().trim();

    // 1. Check for exact match (case-insensitive) in gradients
    for (const baseGenre of Object.keys(genreGradients)) {
        if (baseGenre.toLowerCase() === lowerGenre) {
            genreMatchCache.set(cacheKey, baseGenre);
            return baseGenre;
        }
    }

    // 2. Check aliases
    if (GENRE_ALIASES[lowerGenre]) {
        const aliased = GENRE_ALIASES[lowerGenre];
        genreMatchCache.set(cacheKey, aliased);
        return aliased;
    }

    // 3. Normalize and try matching
    const normalized = normalizeGenre(lowerGenre);

    // Check normalized against base genres
    for (const baseGenre of Object.keys(genreGradients)) {
        if (baseGenre.toLowerCase() === normalized) {
            genreMatchCache.set(cacheKey, baseGenre);
            return baseGenre;
        }
    }

    // Check normalized against aliases
    if (GENRE_ALIASES[normalized]) {
        const aliased = GENRE_ALIASES[normalized];
        genreMatchCache.set(cacheKey, aliased);
        return aliased;
    }

    // 4. Keyword extraction - check if any base genre keyword is in the genre string
    for (const baseGenre of Object.keys(genreGradients)) {
        const baseKeyword = baseGenre.toLowerCase();
        // Check if the genre contains the base genre as a word
        const regex = new RegExp(`\\b${baseKeyword}\\b`, 'i');
        if (regex.test(lowerGenre) || regex.test(normalized)) {
            genreMatchCache.set(cacheKey, baseGenre);
            return baseGenre;
        }
    }

    // 5. Check if normalized or original contains any alias as a substring
    for (const [alias, baseGenre] of Object.entries(GENRE_ALIASES)) {
        if (lowerGenre.includes(alias) || normalized.includes(alias)) {
            genreMatchCache.set(cacheKey, baseGenre);
            return baseGenre;
        }
    }

    // No match found
    genreMatchCache.set(cacheKey, null);
    return null;
}

/**
 * Get the gradient class for a genre's hover glow effect
 * Uses smart matching to handle genre variations
 */
export function getGenreGradient(genre: string): string {
    // Try direct lookup first
    if (genreGradients[genre]) {
        return genreGradients[genre];
    }

    // Try smart matching
    const baseGenre = findBaseGenre(genre);
    if (baseGenre && genreGradients[baseGenre]) {
        return genreGradients[baseGenre];
    }

    return defaultGradient;
}

/**
 * Get the tag colors for a genre
 * Uses smart matching to handle genre variations
 */
export function getGenreColors(genre: string): GenreTagColors {
    // Try direct lookup first
    if (genreTagColors[genre]) {
        return genreTagColors[genre];
    }

    // Try smart matching
    const baseGenre = findBaseGenre(genre);
    if (baseGenre && genreTagColors[baseGenre]) {
        return genreTagColors[baseGenre];
    }

    return defaultTagColors;
}

/**
 * Get a combined class string for genre tag styling
 * Includes background, text, hover, and border classes
 */
export function getGenreTagClasses(genre: string): string {
    const colors = getGenreColors(genre);
    return `${colors.bg} ${colors.text} ${colors.hoverBg} ${colors.border ? `border ${colors.border}` : ''}`;
}

// Export for testing
export { findBaseGenre, normalizeGenre };
