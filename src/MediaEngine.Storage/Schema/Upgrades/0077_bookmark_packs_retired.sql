-- Bookmark packs are retired (Global bookmarks spec D10). A bookmark file is now compared with the bookmarks a person already has and,
-- once applied, becomes ordinary bookmarks in audiobook_bookmarks: Global when an administrator applies it, the person's own otherwise.
-- There is no pack name, no per-pack switch and no stored pack. Pre-release, so imported packs are not carried across.
DROP TABLE bookmark_pack_items;
DROP TABLE bookmark_packs;
