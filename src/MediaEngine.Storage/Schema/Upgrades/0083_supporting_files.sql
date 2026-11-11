-- Supporting files (universal editor v5, WP2). An Extra becomes an editable item: its own title and description, a group, a role, the
-- scope it belongs to and the episode or lesson it is shown alongside, and tags. A person's values live in their own columns (own_title,
-- own_description, role, scope, alongside, and group_id while group_source is 'you') that a rescan never touches; what the Engine reads
-- from the folder and file names sits beside them (title, inferred_role, inferred_group, inferred_scope, inferred_alongside) and is
-- recomputed on every rescan, so "Use folder location" can revert to it.
-- scope, inferred_scope, alongside and inferred_alongside hold the text form of a scope: root, season:2, episode:2:3, folder:Section 1,
-- file:Section 1/02 Intro.mkv, track:4 (alongside may also be none, which means a person cleared the guess).
-- missing_since is set when the file is no longer on disk; the record is kept for a while as "File missing" (a configured number of days).
-- Groups are per root title (its item_folder): a name and an order, stored with the title and deleted with it. source says who made
-- the group: 'folder' (read from a sub-folder; a rescan drops it again once no file is in it) or 'you' (kept until a person deletes it).
-- Pre-release rule: no conversion of anything older; a rescan rebuilds the records.
CREATE TABLE item_extra_groups (
    id          BLOB NOT NULL PRIMARY KEY,
    item_folder TEXT NOT NULL COLLATE NOCASE,
    name        TEXT NOT NULL COLLATE NOCASE,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    source      TEXT NOT NULL DEFAULT 'you' CHECK (source IN ('folder', 'you')),
    created_at  TEXT NOT NULL,
    UNIQUE (item_folder, name)
);

ALTER TABLE item_extras ADD COLUMN own_title TEXT;
ALTER TABLE item_extras ADD COLUMN own_description TEXT;
ALTER TABLE item_extras ADD COLUMN role TEXT;
ALTER TABLE item_extras ADD COLUMN inferred_role TEXT;
ALTER TABLE item_extras ADD COLUMN group_id BLOB REFERENCES item_extra_groups(id) ON DELETE SET NULL;
ALTER TABLE item_extras ADD COLUMN group_source TEXT
    CHECK (group_source IS NULL OR group_source IN ('folder', 'you'));
ALTER TABLE item_extras ADD COLUMN inferred_group TEXT;
ALTER TABLE item_extras ADD COLUMN scope TEXT;
ALTER TABLE item_extras ADD COLUMN inferred_scope TEXT NOT NULL DEFAULT 'root';
ALTER TABLE item_extras ADD COLUMN alongside TEXT;
ALTER TABLE item_extras ADD COLUMN inferred_alongside TEXT;
ALTER TABLE item_extras ADD COLUMN missing_since TEXT;

CREATE INDEX ix_item_extras_group ON item_extras (group_id);
CREATE INDEX ix_item_extra_groups_folder ON item_extra_groups (item_folder);

-- Tags on supporting files reuse the global tags table.
CREATE TABLE item_extra_tag_links (
    extra_id BLOB NOT NULL REFERENCES item_extras(id) ON DELETE CASCADE,
    tag_id   BLOB NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    added_at TEXT NOT NULL,
    PRIMARY KEY (extra_id, tag_id)
);
CREATE INDEX ix_item_extra_tag_links_tag ON item_extra_tag_links (tag_id);
