# SQL Diagnostic Queries for MondayItemMappings Timeout Issues

## 1. Check for Blocking Queries

### Using sp_whoisactive (if available)
```sql
EXEC sp_whoisactive @get_locks = 1, @get_plans = 1;
```

### Using sys.dm_exec_requests + sys.dm_tran_locks
```sql
SELECT 
    r.session_id,
    r.status,
    r.command,
    r.wait_type,
    r.wait_time,
    r.blocking_session_id,
    r.cpu_time,
    r.total_elapsed_time,
    r.reads,
    r.writes,
    r.logical_reads,
    t.text AS query_text,
    p.query_plan
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
CROSS APPLY sys.dm_exec_query_plan(r.plan_handle) p
WHERE r.database_id = DB_ID('OdmonIntegration')
ORDER BY r.total_elapsed_time DESC;

-- Check for locks on MondayItemMappings
SELECT 
    l.request_session_id,
    l.resource_database_id,
    l.resource_associated_entity_id,
    l.resource_type,
    l.resource_description,
    l.request_mode,
    l.request_status,
    r.command,
    t.text AS query_text
FROM sys.dm_tran_locks l
LEFT JOIN sys.dm_exec_requests r ON l.request_session_id = r.session_id
OUTER APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE l.resource_database_id = DB_ID('OdmonIntegration')
    AND l.resource_associated_entity_id = OBJECT_ID('dbo.MondayItemMappings')
ORDER BY l.request_session_id;
```

## 2. Check Index Usage and Statistics

### Enable statistics for test query
```sql
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

-- Test the lookup query
SELECT TOP(1) 
    Id, TikCounter, TikNumber, MondayItemId, BoardId, IsTest, 
    OdcanitVersion, MondayChecksum, LastSyncFromOdcanitUtc, LastSyncFromMondayUtc
FROM dbo.MondayItemMappings
WHERE TikCounter = @tikCounter 
    AND BoardId = @boardId;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
```

### Check index fragmentation
```sql
SELECT 
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    ps.avg_fragmentation_in_percent,
    ps.page_count,
    ps.avg_page_space_used_in_percent
FROM sys.indexes i
INNER JOIN sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.MondayItemMappings'), NULL, NULL, 'DETAILED') ps
    ON i.object_id = ps.object_id AND i.index_id = ps.index_id
WHERE i.object_id = OBJECT_ID('dbo.MondayItemMappings')
ORDER BY ps.avg_fragmentation_in_percent DESC;
```

### Check index usage statistics
```sql
SELECT 
    OBJECT_NAME(s.object_id) AS TableName,
    i.name AS IndexName,
    s.user_seeks,
    s.user_scans,
    s.user_lookups,
    s.user_updates,
    s.last_user_seek,
    s.last_user_scan,
    s.last_user_lookup
FROM sys.dm_db_index_usage_stats s
INNER JOIN sys.indexes i ON s.object_id = i.object_id AND s.index_id = i.index_id
WHERE s.database_id = DB_ID('OdmonIntegration')
    AND s.object_id = OBJECT_ID('dbo.MondayItemMappings')
ORDER BY s.user_seeks + s.user_scans DESC;
```

## 3. Verify Index Existence

```sql
SELECT 
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique,
    i.is_primary_key,
    STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS IndexColumns,
    STRING_AGG(ic.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS IncludedColumns
FROM sys.indexes i
INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE i.object_id = OBJECT_ID('dbo.MondayItemMappings')
GROUP BY i.name, i.type_desc, i.is_unique, i.is_primary_key
ORDER BY i.name;
```

## 4. Check Data Distribution

```sql
-- Count rows by BoardId
SELECT BoardId, COUNT(*) AS RowCount
FROM dbo.MondayItemMappings
GROUP BY BoardId
ORDER BY RowCount DESC;

-- Check for NULL TikNumber rows
SELECT BoardId, COUNT(*) AS NullTikNumberCount
FROM dbo.MondayItemMappings
WHERE TikNumber IS NULL
GROUP BY BoardId
ORDER BY NullTikNumberCount DESC;

-- Sample rows with BoardId = 0
SELECT TOP(10) Id, TikCounter, TikNumber, BoardId, MondayItemId
FROM dbo.MondayItemMappings
WHERE BoardId = 0;
```

## 5. Execution Plan Analysis

```sql
-- Get actual execution plan for the lookup query
SET STATISTICS XML ON;

SELECT TOP(1) 
    Id, TikCounter, TikNumber, MondayItemId, BoardId, IsTest, 
    OdcanitVersion, MondayChecksum, LastSyncFromOdcanitUtc, LastSyncFromMondayUtc
FROM dbo.MondayItemMappings
WHERE TikCounter = 12345 
    AND BoardId = 5035534500;

SET STATISTICS XML OFF;
```

## Expected Index After Migration

After applying migration `AddMondayItemMappingsIndexes`, you should see:

- **IX_MondayItemMappings_BoardId_TikCounter**: Nonclustered index on (BoardId, TikCounter) with INCLUDE columns: Id, MondayItemId, TikNumber, IsTest, OdcanitVersion, MondayChecksum, LastSyncFromOdcanitUtc, LastSyncFromMondayUtc

This index should make the lookup query O(log N) instead of table scan.
