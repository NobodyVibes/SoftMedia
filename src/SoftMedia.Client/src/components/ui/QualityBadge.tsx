interface QualityBadgeProps {
    /**
     * DV-WI-021 — server-derived version label ("4K HDR10 Director's Cut", "1080p").
     * Rendered verbatim: the server (VersionLabelHelper) is the single label authority,
     * replacing the old client-side SD/HD/4K/HDR derivation that drifted three ways.
     */
    label?: string;
}

export default function QualityBadge({ label }: QualityBadgeProps) {
    if (!label) return null;

    const style = label.includes('HDR') || label.includes('Dolby')
        ? 'bg-gradient-to-r from-purple-600 to-pink-600 text-white'
        : label.startsWith('4K') || label.startsWith('8K')
            ? 'bg-purple-600 text-white'
            : 'bg-blue-600 text-white';

    return (
        <span className={`px-2 py-0.5 text-xs font-bold rounded ${style}`}>
            {label}
        </span>
    );
}
