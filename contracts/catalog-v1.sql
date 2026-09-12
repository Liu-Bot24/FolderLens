-- FolderLens catalog schema baseline, v1. Implemented schema may evolve only with migrations/tests.
-- Run on a LOCAL SQLite DB. Set pragmas on each connection where applicable.
PRAGMA foreign_keys=ON;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;
PRAGMA temp_store=FILE;
PRAGMA cache_size=-32768;
BEGIN IMMEDIATE;
CREATE TABLE SchemaInfo (
  singleton INTEGER PRIMARY KEY CHECK(singleton=1),
  schema_version INTEGER NOT NULL CHECK(schema_version>=1),
  natural_key_version INTEGER NOT NULL CHECK(natural_key_version>=1),
  catalog_revision INTEGER NOT NULL DEFAULT 0 CHECK(catalog_revision>=0)
) STRICT;
INSERT INTO SchemaInfo VALUES(1,1,1,0);
CREATE TABLE Roots (
  root_id TEXT PRIMARY KEY,
  display_path TEXT NOT NULL,
  canonical_key TEXT NOT NULL UNIQUE COLLATE BINARY,
  volume_identity TEXT,
  root_epoch INTEGER NOT NULL CHECK(root_epoch>=1),
  availability TEXT NOT NULL CHECK(availability IN('online','offline','inaccessible','unknown')),
  scan_state TEXT NOT NULL CHECK(scan_state IN('notStarted','scanning','ready','partial','cancelled','failed')),
  last_checked_utc_ticks INTEGER,
  cloud_policy TEXT NOT NULL DEFAULT 'localOnly' CHECK(cloud_policy IN('localOnly','explicitAllowed'))
) STRICT;
CREATE TABLE Directories (
  directory_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL REFERENCES Roots(root_id),
  parent_id TEXT,
  name TEXT NOT NULL,
  relative_path TEXT NOT NULL,
  canonical_key TEXT NOT NULL COLLATE BINARY,
  case_mode TEXT NOT NULL CHECK(case_mode IN('sensitive','insensitive','unknown')),
  path_revision INTEGER NOT NULL DEFAULT 1 CHECK(path_revision>=1),
  entry_state TEXT NOT NULL DEFAULT 'present' CHECK(entry_state IN('present','missing','excluded')),
  last_seen_scan_id TEXT,
  UNIQUE(root_id,directory_id),
  UNIQUE(root_id,canonical_key),
  FOREIGN KEY(root_id,parent_id) REFERENCES Directories(root_id,directory_id),
  CHECK(parent_id IS NULL OR parent_id<>directory_id)
) STRICT;
CREATE INDEX IX_Directories_Parent ON Directories(root_id,parent_id);
CREATE TABLE Files (
  entry_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL REFERENCES Roots(root_id),
  directory_id TEXT NOT NULL,
  name TEXT NOT NULL,
  extension TEXT NOT NULL,
  relative_path TEXT NOT NULL,
  canonical_key TEXT NOT NULL COLLATE BINARY,
  path_sort_key BLOB NOT NULL,
  name_sort_key BLOB NOT NULL,
  natural_key_version INTEGER NOT NULL CHECK(natural_key_version>=1),
  physical_identity TEXT,
  file_version INTEGER NOT NULL DEFAULT 1 CHECK(file_version>=1),
  path_revision INTEGER NOT NULL DEFAULT 1 CHECK(path_revision>=1),
  stat_signature TEXT NOT NULL,
  entry_state TEXT NOT NULL DEFAULT 'present' CHECK(entry_state IN('present','missing','excluded')),
  kind TEXT NOT NULL CHECK(kind IN('image','video','audio','text','markdown','other')),
  kind_confidence TEXT NOT NULL CHECK(kind_confidence IN('extension','verified')),
  format_id TEXT,
  is_raw INTEGER CHECK(is_raw IN(0,1)),
  is_animated INTEGER CHECK(is_animated IN(0,1)),
  logical_bytes INTEGER NOT NULL CHECK(logical_bytes>=0),
  allocated_bytes INTEGER CHECK(allocated_bytes>=0),
  file_attributes INTEGER NOT NULL DEFAULT 0 CHECK(file_attributes>=0),
  mtime_utc_ticks INTEGER NOT NULL,
  ctime_utc_ticks INTEGER,
  capture_wall_ticks INTEGER,
  capture_offset_minutes INTEGER CHECK(capture_offset_minutes BETWEEN -840 AND 840),
  capture_utc_ticks INTEGER,
  encoded_width INTEGER CHECK(encoded_width>0),
  encoded_height INTEGER CHECK(encoded_height>0),
  display_width INTEGER CHECK(display_width>0),
  display_height INTEGER CHECK(display_height>0),
  long_edge INTEGER CHECK(long_edge>0),
  short_edge INTEGER CHECK(short_edge>0),
  pixel_count INTEGER CHECK(pixel_count>0),
  orientation INTEGER CHECK(orientation BETWEEN 1 AND 8),
  bit_depth INTEGER CHECK(bit_depth BETWEEN 1 AND 128),
  frame_count INTEGER CHECK(frame_count>=1),
  page_count INTEGER CHECK(page_count>=1),
  duration_ms INTEGER CHECK(duration_ms>=0),
  fps_num INTEGER CHECK(fps_num>=0),
  fps_den INTEGER CHECK(fps_den>0),
  video_codec TEXT,
  audio_codec TEXT,
  hydration_state TEXT NOT NULL DEFAULT 'local' CHECK(hydration_state IN('local','placeholder','unknown')),
  source_metadata_version INTEGER CHECK(source_metadata_version>=1),
  last_seen_scan_id TEXT,
  updated_revision INTEGER NOT NULL CHECK(updated_revision>=0),
  FOREIGN KEY(root_id,directory_id) REFERENCES Directories(root_id,directory_id),
  UNIQUE(root_id,canonical_key),
  CHECK(capture_utc_ticks IS NULL OR (capture_wall_ticks IS NOT NULL AND capture_offset_minutes IS NOT NULL)),
  CHECK((display_width IS NULL AND display_height IS NULL AND long_edge IS NULL AND short_edge IS NULL AND pixel_count IS NULL)
     OR (display_width IS NOT NULL AND display_height IS NOT NULL AND long_edge IS NOT NULL AND short_edge IS NOT NULL AND pixel_count IS NOT NULL
       AND long_edge=MAX(display_width,display_height) AND short_edge=MIN(display_width,display_height)
       AND pixel_count=display_width*display_height)),
  CHECK(source_metadata_version IS NULL OR source_metadata_version<=file_version)
) STRICT;
CREATE INDEX IX_Files_Name ON Files(root_id,entry_state,name_sort_key,path_sort_key,entry_id);
CREATE INDEX IX_Files_Type ON Files(root_id,entry_state,kind,format_id);
CREATE INDEX IX_Files_Size ON Files(root_id,entry_state,logical_bytes,path_sort_key,entry_id);
CREATE INDEX IX_Files_Mtime ON Files(root_id,entry_state,mtime_utc_ticks,path_sort_key,entry_id);
CREATE INDEX IX_Files_LongEdge ON Files(root_id,entry_state,long_edge,path_sort_key,entry_id);
CREATE INDEX IX_Files_Pixels ON Files(root_id,entry_state,pixel_count,path_sort_key,entry_id);
CREATE INDEX IX_Files_Duration ON Files(root_id,entry_state,duration_ms,path_sort_key,entry_id);
CREATE INDEX IX_Files_Directory ON Files(root_id,directory_id,entry_state);
CREATE INDEX IX_Files_Physical ON Files(root_id,physical_identity) WHERE physical_identity IS NOT NULL;
CREATE TABLE FieldStates (
  entry_id TEXT NOT NULL REFERENCES Files(entry_id) ON DELETE CASCADE,
  field_group TEXT NOT NULL CHECK(field_group IN('identity','imageGeometry','animation','imageColor','captureTime','media','allocation')),
  source_version INTEGER NOT NULL CHECK(source_version>=1),
  state TEXT NOT NULL CHECK(state IN('notRequested','pending','ready','failed','unsupported','deferredOffline')),
  provider_version TEXT,
  attempt_count INTEGER NOT NULL DEFAULT 0 CHECK(attempt_count>=0),
  retry_after_utc_ticks INTEGER,
  error_code TEXT,
  PRIMARY KEY(entry_id,field_group)
) STRICT;
CREATE TABLE ScanRuns (
  scan_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL REFERENCES Roots(root_id),
  root_epoch INTEGER NOT NULL CHECK(root_epoch>=1),
  state TEXT NOT NULL CHECK(state IN('running','completed','partial','cancelled','failed')),
  started_utc_ticks INTEGER NOT NULL,
  completed_utc_ticks INTEGER,
  error_count INTEGER NOT NULL DEFAULT 0 CHECK(error_count>=0)
) STRICT;
CREATE TABLE DirectoryScans (
  scan_id TEXT NOT NULL REFERENCES ScanRuns(scan_id) ON DELETE CASCADE,
  directory_id TEXT NOT NULL REFERENCES Directories(directory_id),
  state TEXT NOT NULL CHECK(state IN('queued','enumerating','completed','inaccessible','offline','excluded','cancelled','failed')),
  entry_count INTEGER NOT NULL DEFAULT 0 CHECK(entry_count>=0),
  error_code TEXT,
  PRIMARY KEY(scan_id,directory_id)
) STRICT;
CREATE TABLE Exclusions (
  exclusion_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL REFERENCES Roots(root_id),
  relative_path TEXT NOT NULL,
  canonical_key TEXT NOT NULL COLLATE BINARY,
  mode TEXT NOT NULL CHECK(mode IN('hideView','skipScan')),
  enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN(0,1)),
  UNIQUE(root_id,canonical_key,mode)
) STRICT;
CREATE TABLE AggregateBuilds (
  build_id TEXT PRIMARY KEY,
  root_id TEXT NOT NULL REFERENCES Roots(root_id),
  catalog_revision INTEGER NOT NULL CHECK(catalog_revision>=0),
  state TEXT NOT NULL CHECK(state IN('building','ready','partial','failed')),
  is_active INTEGER NOT NULL DEFAULT 0 CHECK(is_active IN(0,1)),
  CHECK(is_active=0 OR state IN('ready','partial'))
) STRICT;
CREATE UNIQUE INDEX IX_OneActiveAggregate ON AggregateBuilds(root_id) WHERE is_active=1;
CREATE TABLE DirectoryAggregates (
  build_id TEXT NOT NULL REFERENCES AggregateBuilds(build_id) ON DELETE CASCADE,
  directory_id TEXT NOT NULL REFERENCES Directories(directory_id),
  direct_file_count INTEGER NOT NULL CHECK(direct_file_count>=0),
  subtree_file_count INTEGER NOT NULL CHECK(subtree_file_count>=direct_file_count),
  direct_logical_bytes INTEGER NOT NULL CHECK(direct_logical_bytes>=0),
  subtree_logical_bytes INTEGER NOT NULL CHECK(subtree_logical_bytes>=direct_logical_bytes),
  direct_allocated_known_bytes INTEGER NOT NULL CHECK(direct_allocated_known_bytes>=0),
  subtree_allocated_known_bytes INTEGER NOT NULL CHECK(subtree_allocated_known_bytes>=direct_allocated_known_bytes),
  allocation_unknown_count INTEGER NOT NULL CHECK(allocation_unknown_count>=0),
  excluded_directory_count INTEGER NOT NULL CHECK(excluded_directory_count>=0),
  inaccessible_directory_count INTEGER NOT NULL CHECK(inaccessible_directory_count>=0),
  completeness TEXT NOT NULL CHECK(completeness IN('complete','partial','scanning','offlineSnapshot')),
  PRIMARY KEY(build_id,directory_id)
) STRICT;
CREATE TABLE DirtyDirectories (
  directory_id TEXT PRIMARY KEY REFERENCES Directories(directory_id) ON DELETE CASCADE,
  reason TEXT NOT NULL,
  earliest_utc_ticks INTEGER NOT NULL,
  root_epoch INTEGER NOT NULL CHECK(root_epoch>=1)
) STRICT;
PRAGMA user_version=1;
COMMIT;
