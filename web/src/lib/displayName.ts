/**
 * Mirrors `DisplayNamePolicy.MaxLength` on the server.
 *
 * A mirror rather than a fetch, for the same reason the password rules are mirrored: the limit has
 * to be known before anything is sent, and a round trip to learn it would make the form wait on the
 * network to tell you something that has not changed in the life of the project.
 *
 * The server is still the authority — it rejects an over-long name whatever the input allows. This
 * only stops the browser accepting keystrokes it already knows will be refused.
 */
export const MAX_DISPLAY_NAME_LENGTH = 60
