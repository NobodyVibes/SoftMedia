interface ProgressBarProps {
    progress: number; // 0-100
}

export default function ProgressBar({ progress }: ProgressBarProps) {
    if (progress <= 0) return null;

    return (
        <div className="absolute bottom-0 left-0 right-0 h-1 bg-gray-800/50">
            <div
                className="h-full bg-primary transition-all duration-300"
                style={{ width: `${Math.min(100, Math.max(0, progress))}%` }}
            />
        </div>
    );
}
