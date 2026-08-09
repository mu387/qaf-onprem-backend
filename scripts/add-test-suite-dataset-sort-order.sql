IF COL_LENGTH('dbo.data_sets', 'sort_order') IS NULL
BEGIN
    ALTER TABLE dbo.data_sets
    ADD sort_order INT NULL;
END;

;WITH ordered_datasets AS (
    SELECT
        id,
        ROW_NUMBER() OVER (
            PARTITION BY test_component_id
            ORDER BY id
        ) AS next_sort_order
    FROM dbo.data_sets
    WHERE deleted_at IS NULL
)
UPDATE ds
SET sort_order = ordered_datasets.next_sort_order
FROM dbo.data_sets ds
INNER JOIN ordered_datasets ON ordered_datasets.id = ds.id
WHERE ds.sort_order IS NULL;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_data_sets_test_component_sort_order_id'
      AND object_id = OBJECT_ID('dbo.data_sets')
)
BEGIN
    CREATE INDEX IX_data_sets_test_component_sort_order_id
        ON dbo.data_sets (test_component_id, sort_order, id)
        WHERE deleted_at IS NULL;
END;