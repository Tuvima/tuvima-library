-- Who asked for a queued operation (Plugin architecture spec PA2): a lyrics or subtitle refresh runs later from the durable queue, so
-- the person's level has to be kept with it for the plugin audience rule to be applied when the work really happens. The words are the
-- audience words ('administrators', 'adults', 'everyone': the widest group the person belongs to). NULL means the Library asked itself
-- (a scan, a schedule) and no person's audience applies.
ALTER TABLE media_operations ADD COLUMN requested_audience TEXT NULL;
