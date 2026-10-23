-- Sign-in no longer remembers which profile a device opens as (users, profiles and households spec D3): every sign-in shows the
-- household's profile choice with the sign-in's own profile selected, so the per-device setting "always open as" is gone.
-- Pre-release: nothing is carried across.
DROP TABLE device_profile_preferences;
