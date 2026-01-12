import { useEffect, useRef } from 'react';
import { HubConnectionBuilder, HubConnection, LogLevel, HubConnectionState } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useAuthStore } from '../store/authStore';

interface UseMediaHubOptions {
    libraryId?: string;
    mediaId?: string;
}

/**
 * React hook for real-time SignalR updates.
 * Automatically connects to the hub, joins appropriate groups, and invalidates queries on updates.
 */
export function useMediaHub({ libraryId, mediaId }: UseMediaHubOptions) {
    const queryClient = useQueryClient();
    const { token } = useAuthStore();
    const connectionRef = useRef<HubConnection | null>(null);
    const optionsRef = useRef({ libraryId, mediaId });

    // Keep options ref updated
    optionsRef.current = { libraryId, mediaId };

    useEffect(() => {
        // Don't connect if not authenticated
        if (!token) return;

        // Build connection with auto-reconnect
        const connection = new HubConnectionBuilder()
            .withUrl(`/hubs/media?access_token=${encodeURIComponent(token)}`)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]) // Retry: immediately, 2s, 5s, 10s, 30s
            .configureLogging(LogLevel.Warning)
            .build();

        connectionRef.current = connection;

        // Handle incoming events
        connection.on('ItemAdded', (libId: string) => {
            console.debug('[SignalR] ItemAdded:', libId);
            queryClient.invalidateQueries({ queryKey: ['library', libId, 'items'] });
        });

        connection.on('ItemUpdated', (mediaId: string) => {
            console.debug('[SignalR] ItemUpdated:', mediaId);
            queryClient.invalidateQueries({ queryKey: ['media', mediaId] });
        });

        connection.on('ScanProgress', (libId: string, processed: number, total: number, status: string) => {
            console.debug('[SignalR] ScanProgress:', libId, processed, total, status);
            // Invalidate scan queue to update any progress displays
            queryClient.invalidateQueries({ queryKey: ['scanQueue'] });
        });

        // Handle reconnection - rejoin groups
        connection.onreconnected(() => {
            console.debug('[SignalR] Reconnected, rejoining groups...');
            const opts = optionsRef.current;
            if (opts.libraryId) connection.invoke('JoinLibrary', opts.libraryId).catch(console.error);
            if (opts.mediaId) connection.invoke('JoinMedia', opts.mediaId).catch(console.error);
        });

        // Start connection and join groups
        connection.start()
            .then(() => {
                console.debug('[SignalR] Connected');
                const opts = optionsRef.current;
                if (opts.libraryId) {
                    connection.invoke('JoinLibrary', opts.libraryId)
                        .catch(err => console.error('[SignalR] Failed to join library group:', err));
                }
                if (opts.mediaId) {
                    connection.invoke('JoinMedia', opts.mediaId)
                        .catch(err => console.error('[SignalR] Failed to join media group:', err));
                }
            })
            .catch(err => console.error('[SignalR] Connection failed:', err));

        // Cleanup on unmount
        return () => {
            if (connection.state !== HubConnectionState.Disconnected) {
                connection.stop()
                    .then(() => console.debug('[SignalR] Disconnected'))
                    .catch(console.error);
            }
        };
    }, [token, queryClient]);

    // Handle group changes without reconnecting
    useEffect(() => {
        const connection = connectionRef.current;
        if (!connection || connection.state !== HubConnectionState.Connected) return;

        // This effect handles when libraryId or mediaId changes after initial connection
        // For now, we just rejoin the new groups (leave is handled server-side on disconnect)
        if (libraryId) {
            connection.invoke('JoinLibrary', libraryId).catch(console.error);
        }
        if (mediaId) {
            connection.invoke('JoinMedia', mediaId).catch(console.error);
        }
    }, [libraryId, mediaId]);
}
