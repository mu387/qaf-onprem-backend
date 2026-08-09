SET NOCOUNT ON;
SET XACT_ABORT ON;

/*
  Adds database support for point-based configuration assignments.
  Safe to run multiple times and safe against partially applied test environments.
*/

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.test_plan_item_suite_configurations', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.test_plan_item_suite_configurations
        (
            id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
            test_plan_item_suite_id BIGINT NOT NULL,
            configuration_id BIGINT NOT NULL,
            status_id BIGINT NOT NULL
                CONSTRAINT DF_test_plan_item_suite_configurations_status_id DEFAULT (1),
            created_at DATETIME2(7) NOT NULL
                CONSTRAINT DF_test_plan_item_suite_configurations_created_at DEFAULT SYSUTCDATETIME(),
            updated_at DATETIME2(7) NOT NULL
                CONSTRAINT DF_test_plan_item_suite_configurations_updated_at DEFAULT SYSUTCDATETIME(),
            deleted_at DATETIME2(7) NULL
        );
    END;

    IF COL_LENGTH(N'dbo.test_runner_items', N'execution_id') IS NULL
    BEGIN
        ALTER TABLE dbo.test_runner_items
        ADD execution_id BIGINT NULL;
    END;

    EXEC sp_executesql N'
        UPDATE dbo.test_runner_items
        SET execution_id = test_suite_id
        WHERE execution_id IS NULL
          AND test_suite_id IS NOT NULL;

        IF EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE name = N''UX_test_runner_items_uq_test_runner_items_runner_suite''
              AND object_id = OBJECT_ID(N''dbo.test_runner_items'')
        )
        BEGIN
            DROP INDEX UX_test_runner_items_uq_test_runner_items_runner_suite
            ON dbo.test_runner_items;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE name = N''UX_test_runner_items_runner_execution''
              AND object_id = OBJECT_ID(N''dbo.test_runner_items'')
        )
        BEGIN
            CREATE UNIQUE INDEX UX_test_runner_items_runner_execution
                ON dbo.test_runner_items (test_runner_id, execution_id)
                WHERE execution_id IS NOT NULL;
        END;
    ';

    IF NOT EXISTS
    (
        SELECT 1
        FROM sys.indexes
        WHERE name = N'UX_tpisc_active_assignment'
          AND object_id = OBJECT_ID(N'dbo.test_plan_item_suite_configurations')
    )
    BEGIN
        CREATE UNIQUE INDEX UX_tpisc_active_assignment
            ON dbo.test_plan_item_suite_configurations (test_plan_item_suite_id, configuration_id)
            WHERE deleted_at IS NULL;
    END;

    INSERT INTO dbo.test_plan_item_suite_configurations
    (
        test_plan_item_suite_id,
        configuration_id,
        status_id,
        created_at,
        updated_at,
        deleted_at
    )
    SELECT
        child.parent_id,
        td.configuration_id,
        ISNULL(child.status_id, 1),
        SYSUTCDATETIME(),
        SYSUTCDATETIME(),
        NULL
    FROM dbo.test_plan_item_suites child
    INNER JOIN dbo.test_designs td
        ON td.id = child.test_design_id
    WHERE child.parent_id IS NOT NULL
      AND child.deleted_at IS NULL
      AND td.deleted_at IS NULL
      AND td.configuration_id IS NOT NULL
      AND NOT EXISTS
      (
          SELECT 1
          FROM dbo.test_plan_item_suite_configurations existing
          WHERE existing.test_plan_item_suite_id = child.parent_id
            AND existing.configuration_id = td.configuration_id
            AND existing.deleted_at IS NULL
      );

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
        ROLLBACK TRANSACTION;

    THROW;
END CATCH;
GO

SELECT COL_LENGTH(N'dbo.test_runner_items', N'execution_id') AS execution_id_exists;

SELECT name
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.test_runner_items')
  AND name IN
  (
      N'UX_test_runner_items_uq_test_runner_items_runner_suite',
      N'UX_test_runner_items_runner_execution'
  );

SELECT OBJECT_ID(N'dbo.test_plan_item_suite_configurations', N'U') AS tpisc_table_id;

SELECT name
FROM sys.indexes
WHERE object_id = OBJECT_ID(N'dbo.test_plan_item_suite_configurations')
  AND name = N'UX_tpisc_active_assignment';

SELECT COUNT(*) AS migrated_assignment_rows
FROM dbo.test_plan_item_suite_configurations
WHERE deleted_at IS NULL;