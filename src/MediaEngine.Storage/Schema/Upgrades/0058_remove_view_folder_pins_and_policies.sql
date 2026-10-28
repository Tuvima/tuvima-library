-- The View Folders page is removed (Library pages spec D24, WP13). Pinned folders and the per-sub-folder Photos timeline rules existed only for
-- that page. Whether a folder's photos appear in the Photos timeline is now one switch on each source folder (view_source_policies.include_in_timeline),
-- so a sub-folder rule can no longer override it. Pre-release rule: the rows are dropped with no conversion.
DROP TABLE view_folder_pins;
DROP TABLE view_folder_timeline_policies;
