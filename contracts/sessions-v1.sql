-- Independent, LOCAL, rebuildable database; no cross-database foreign keys to catalog.
PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
BEGIN IMMEDIATE;
CREATE TABLE ResultSessions (
  session_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL,
  root_epoch INTEGER NOT NULL CHECK(root_epoch>=1),
  query_generation INTEGER NOT NULL CHECK(query_generation>=1),
  catalog_revision INTEGER NOT NULL CHECK(catalog_revision>=0),
  filter_json TEXT NOT NULL,
  sort_json TEXT NOT NULL,
  natural_key_version INTEGER NOT NULL CHECK(natural_key_version>=1),
  state TEXT NOT NULL CHECK(state IN('building','ready','cancelled','failed')),
  committed_count INTEGER NOT NULL DEFAULT 0 CHECK(committed_count>=0),
  total_count INTEGER CHECK(total_count>=0),
  pending_count INTEGER NOT NULL DEFAULT 0 CHECK(pending_count>=0),
  unresolvable_count INTEGER NOT NULL DEFAULT 0 CHECK(unresolvable_count>=0),
  created_utc_ticks INTEGER NOT NULL,
  completed_utc_ticks INTEGER,
  active_leases INTEGER NOT NULL DEFAULT 0 CHECK(active_leases>=0),
  error_code TEXT,
  CHECK(state<>'ready' OR (total_count IS NOT NULL AND total_count=committed_count AND completed_utc_ticks IS NOT NULL))
) STRICT;
CREATE TABLE ResultItems (
  session_id TEXT NOT NULL REFERENCES ResultSessions(session_id) ON DELETE CASCADE,
  ordinal INTEGER NOT NULL CHECK(ordinal>=0),
  entry_id TEXT NOT NULL,
  observed_version INTEGER NOT NULL CHECK(observed_version>=1),
  observed_path_revision INTEGER NOT NULL CHECK(observed_path_revision>=1),
  directory_id TEXT NOT NULL,
  snapshot_relative_path TEXT NOT NULL,
  snapshot_logical_bytes INTEGER NOT NULL CHECK(snapshot_logical_bytes>=0),
  snapshot_allocated_bytes INTEGER CHECK(snapshot_allocated_bytes>=0),
  snapshot_physical_identity TEXT,
  PRIMARY KEY(session_id,ordinal),
  UNIQUE(session_id,entry_id)
) STRICT;
CREATE INDEX IX_ResultItems_Directory ON ResultItems(session_id,directory_id);
CREATE TABLE ResultDirectories (
  session_id TEXT NOT NULL REFERENCES ResultSessions(session_id) ON DELETE CASCADE,
  directory_id TEXT NOT NULL,
  parent_id TEXT,
  relative_path TEXT NOT NULL,
  PRIMARY KEY(session_id,directory_id)
) STRICT;
PRAGMA user_version=1;
COMMIT;
-- Take a deep page: SELECT * FROM ResultItems WHERE session_id=$s AND ordinal >= $i ORDER BY ordinal LIMIT $n;
-- Ready transition must atomically verify count, MIN=0/MAX=count-1, no holes, complete source enumeration.
