-- Library activity (spec library-activity D30, #868): one import is one batch. A batch the Engine's stopping cut off is marked here so the restart
-- can reopen it and the titles that are picked up again join it, instead of opening a second batch. The mark is cleared once the Engine has
-- been idle long enough that the next files are a new drop. Pre-release rule: no back-fill.
ALTER TABLE activity_runs ADD COLUMN interrupted INTEGER NOT NULL DEFAULT 0;
