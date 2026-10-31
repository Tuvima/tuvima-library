-- Collection options (Collections and lists spec WP2, D11 and D12). max_items is "At most N" (null = no limit); season_from and
-- season_to are the optional seasonal window as MM-dd (both set or both null). Minimum items already exists (min_items) and
-- starts being read. Random order is a sort_field value, so it needs no column.
-- Pre-release rule: no conversion of anything older.
ALTER TABLE collections ADD COLUMN max_items INTEGER CHECK (max_items IS NULL OR max_items >= 1);
ALTER TABLE collections ADD COLUMN season_from TEXT CHECK (season_from IS NULL OR season_from GLOB '[0-1][0-9]-[0-3][0-9]');
ALTER TABLE collections ADD COLUMN season_to TEXT CHECK (season_to IS NULL OR season_to GLOB '[0-1][0-9]-[0-3][0-9]');
