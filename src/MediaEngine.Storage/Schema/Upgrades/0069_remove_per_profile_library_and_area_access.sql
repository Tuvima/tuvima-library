-- Library access v4 (libraries redesign D16, D19 Q3). A library is on or off for a whole household and nowhere else: the per-profile library
-- ticks (profile_library_hidden) and the per-profile Watch/Listen/Read/View area switches (profile_feature_grants, whose only values were those
-- four areas) are removed. The household ceiling (household_library_hidden), the Kid's profile settings on the household and the Vault's own
-- per-profile access are untouched. Pre-release rule: the rows are dropped with no conversion.
DROP TABLE profile_library_hidden;
DROP TABLE profile_feature_grants;
