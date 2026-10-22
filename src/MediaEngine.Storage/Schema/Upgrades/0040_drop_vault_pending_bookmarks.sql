-- Vault bookmarks waiting for their video are now kept in the general waiting area (waiting_personal_state, step 0039), like every other
-- bookmark in a restored backup; the Vault's own waiting table and its re-link are gone (backup and restore spec D7). Pre-release, so
-- nothing is carried across.
DROP TABLE vault_pending_bookmarks;
