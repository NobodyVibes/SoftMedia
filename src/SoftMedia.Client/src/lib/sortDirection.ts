/**
 * The direction a sort key means when the user hasn't said otherwise: titles read A-Z,
 * everything else (dates, years, ratings, play counts) means newest/highest/most first.
 *
 * MUST mirror SortDirection.NaturalFor on the server. If the two disagree, the arrow
 * icon claims one direction while the query runs the other.
 *
 * Its own module (not an export of FilterBar) so that component file exports
 * only components and Fast Refresh keeps working there.
 */
const ASCENDING_BY_NATURE = new Set(['title', 'artist']);

export function naturalDirectionFor(sortKey: string): 'asc' | 'desc' {
    return ASCENDING_BY_NATURE.has(sortKey) ? 'asc' : 'desc';
}
