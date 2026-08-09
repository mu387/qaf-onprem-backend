SET NOCOUNT ON;

/*
  Hardens dbo.components defaults for migration resilience.
  Safe to run multiple times.
*/

IF COL_LENGTH('dbo.components', 'locked') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON c.object_id = dc.parent_object_id
           AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('dbo.components')
          AND c.name = 'locked'
    )
    BEGIN
        EXEC('ALTER TABLE dbo.components ADD CONSTRAINT DF_components_locked DEFAULT ((0)) FOR [locked];');
    END
END

IF COL_LENGTH('dbo.components', 'status') IS NOT NULL
BEGIN
    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
            ON c.object_id = dc.parent_object_id
           AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID('dbo.components')
          AND c.name = 'status'
    )
    BEGIN
        EXEC('ALTER TABLE dbo.components ADD CONSTRAINT DF_components_status DEFAULT ((1)) FOR [status];');
    END
END
