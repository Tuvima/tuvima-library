-- Users, profiles and households (wave UPH1b: switch). Roles and access are now read from the profile, so a data store set up before this
-- step (where the administrator lives on the sign-in) is not carried forward: the Library is set up again (spec D11, start fresh).
-- This step moves the stored storage epoch on; StorageEpochGuard.CurrentEpoch is the same value.
UPDATE storage_metadata SET value = 'guid-blob-v12-profile-held-access' WHERE key = 'storage_epoch';
