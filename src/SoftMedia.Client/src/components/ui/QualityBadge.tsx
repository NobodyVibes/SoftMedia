interface QualityBadgeProps {
    quality?: 'SD' | 'HD' | '4K' | 'HDR';
}

export default function QualityBadge({ quality }: QualityBadgeProps) {
    if (!quality) return null;

    const styles = {
        'SD': 'bg-gray-600 text-gray-200',
        'HD': 'bg-blue-600 text-white',
        '4K': 'bg-purple-600 text-white',
        'HDR': 'bg-gradient-to-r from-purple-600 to-pink-600 text-white'
    };

    return (
        <span className={`px-2 py-0.5 text-xs font-bold rounded ${styles[quality]}`}>
            {quality}
        </span>
    );
}
