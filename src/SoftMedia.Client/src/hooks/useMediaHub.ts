import { useEffect, useRef } from 'react';
import { HubConnectionBuilder, HubConnection, LogLevel, HubConnectionState } from '@microsoft/signalr';
import { useQueryClient } from '@tanstack/react-query';
import { useAuthStore } from '../store/authStore';

interface UseMediaHubOptions {
    libraryId?: string;
    mediaId?: string;
}

// Custom logger to filter out known SignalR errors during React Strict Mode unmounts
import type { ILogger } from '@microsoft/signalr';

class SignalRLogger implements ILogger {
    log(logLevel: LogLevel, message: string) {
        // Filter out "connection stopped during negotiation" errors
        if (message && (
            message.includes('The connection was stopped during negotiation') ||
            message.includes('Failed to start the connection')
        )) {
            return;
        }

        // Filter out logs below Warning level to keep console clean
        if (logLevel < LogLevel.Warning) {
            return;
        }

        // Forward other logs to console
        switch (logLevel) {
            case LogLevel.Critical:
            case LogLevel.Error:
                console.error(`[SignalR] ${message}`);
                break;
            case LogLevel.Warning:
                console.warn(`[SignalR] ${message}`);
                break;
            case LogLevel.Information:
                console.info(`[SignalR] ${message}`);
                break;
            case LogLevel.Debug:
            case LogLevel.Trace:
                console.debug(`[SignalR] ${message}`);
                break;
        }
    }
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
            .withUrl('/hubs/media', {
                accessTokenFactory: () => token
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000]) // Retry: immediately, 2s, 5s, 10s, 30s
            .configureLogging(new SignalRLogger()) // Use custom logger
            .build();

        connectionRef.current = connection;
        let isMounted = true;

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
        const startConnection = async () => {
            try {
                await connection.start();
                if (!isMounted) {
                    await connection.stop();
                    return;
                }

                console.debug('[SignalR] Connected');
                const opts = optionsRef.current;

                if (opts.libraryId) {
                    await connection.invoke('JoinLibrary', opts.libraryId).catch(err => console.error('[SignalR] Failed to join library group:', err));
                }
                if (opts.mediaId) {
                    await connection.invoke('JoinMedia', opts.mediaId).catch(err => console.error('[SignalR] Failed to join media group:', err));
                }
            } catch (err: any) {
                // Ignore AbortError which happens when unmounting during negotiation
                const errorMessage = err?.message || err?.toString() || '';
                if (isMounted &&
                    !errorMessage.includes('The connection was stopped during negotiation') &&
                    !errorMessage.includes('Failed to start the connection')) {
                    console.error('[SignalR] Connection failed:', err);
                }
            }
        };

        startConnection();

        // Cleanup on unmount
        return () => {
            isMounted = false;
            // Only stop if we are connected or connecting
            if (connection.state !== HubConnectionState.Disconnected) {
                connection.stop()
                    .then(() => console.debug('[SignalR] Disconnected'))
                    .catch(() => {
                        // Ignore errors during stop
                    });
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
