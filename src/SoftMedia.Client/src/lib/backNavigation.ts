import { MediaType, type MediaItem } from '../types';

/**
 * Back-navigation targets are HIERARCHICAL, never browser history (`navigate(-1)`).
 * Players, the reader, and detail pages are all deep-linked from home rows, search,
 * libraries, collections and each other, so history-back is entry-dependent — and can
 * loop (player back → detail page → history-back → player again). Every back control
 * resolves its destination through this module so the rules live in exactly one place.
 */

/**
 * Where a player/reader "Back" lands: the media's detail surface. For an episode that
 * is its SERIES page — the app has no per-episode detail pages (every launch path,
 * from library quick-play to the series page's episode list, passes through the
 * series detail surface).
 */
export function playerBackTarget(item: Pick<MediaItem, 'id' | 'seriesId'>): string {
    return `/media/${item.seriesId ?? item.id}`;
}

/**
 * Where a detail page's "Back" lands: one level up the containment chain.
 * - photo → its album in the photo library (the album key rides on the photo URL;
 *   PhotoLibraryView reads the same ?album= param) — after paging through 20 photos,
 *   history-back would replay all 20
 * - episode → its series page; track → its album; album → its artist
 * - top-level items (movies, series, artists, books…) → their library
 */
export function detailBackTarget(
    item: Pick<MediaItem, 'id'> & Partial<Pick<MediaItem, 'type' | 'seriesId' | 'albumId' | 'artistId' | 'libraryId'>>,
    photoAlbumKey?: string | null,
): string {
    if (item.type === MediaType.Photo && item.libraryId) {
        return `/libraries/${item.libraryId}${photoAlbumKey != null ? `?album=${encodeURIComponent(photoAlbumKey)}` : ''}`;
    }
    if (item.seriesId) return `/media/${item.seriesId}`;
    if (item.albumId) return `/media/${item.albumId}`;
    if (item.artistId && item.type === MediaType.Album) return `/media/${item.artistId}`;
    if (item.libraryId) return `/libraries/${item.libraryId}`;
    return '/';
}
