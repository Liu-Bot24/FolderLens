-- Apply transactionally after a backup. Existing ungrouped v2 sessions remain valid.
BEGIN IMMEDIATE;
ALTER TABLE ResultItems ADD COLUMN group_id TEXT;
CREATE TABLE ResultGroups (
  session_id TEXT NOT NULL REFERENCES ResultSessions(session_id) ON DELETE CASCADE,
  group_id TEXT NOT NULL,
  relative_path TEXT NOT NULL,
  logical_bytes INTEGER NOT NULL CHECK(logical_bytes>=0),
  match_count INTEGER NOT NULL CHECK(match_count>=0),
  start_ordinal INTEGER NOT NULL CHECK(start_ordinal>=0),
  item_count INTEGER NOT NULL DEFAULT 0 CHECK(item_count>=0),
  scan_state TEXT NOT NULL,
  capacity_scope TEXT NOT NULL,
  PRIMARY KEY(session_id,group_id),
  UNIQUE(session_id,start_ordinal)
) STRICT;
PRAGMA user_version=3;
COMMIT;
