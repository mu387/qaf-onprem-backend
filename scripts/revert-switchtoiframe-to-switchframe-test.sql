SET NOCOUNT ON;

/*
Purpose:
Revert before/after helper text in test env from switchToIframe back to switchframe
until the validating code is deployed.

Default behavior:
- previews matching rows
- applies the UPDATE inside a transaction
- rolls back unless @ApplyChanges is set to 1

Notes:
- targets component_steps.before_step and component_steps.after_step
- preserves the value part after the helper name
- handles both switchToIframe=:... and switchToIframe=... forms
*/

DECLARE @ApplyChanges bit = 0;

IF OBJECT_ID('tempdb..#changed_rows') IS NOT NULL
    DROP TABLE #changed_rows;

CREATE TABLE #changed_rows
(
    step_id bigint NOT NULL,
    component_id bigint NULL,
    old_before_step nvarchar(max) NULL,
    new_before_step nvarchar(max) NULL,
    old_after_step nvarchar(max) NULL,
    new_after_step nvarchar(max) NULL
);

SELECT
    cs.id AS step_id,
    cs.component_id,
    cs.before_step,
    cs.after_step
FROM component_steps cs
WHERE cs.deleted_at IS NULL
  AND (
      cs.before_step LIKE '%switchToIframe=%'
      OR cs.after_step LIKE '%switchToIframe=%'
  )
ORDER BY cs.component_id, cs.id;

BEGIN TRAN;

UPDATE cs
SET
    before_step = CASE
        WHEN cs.before_step IS NULL THEN NULL
        ELSE REPLACE(REPLACE(cs.before_step, 'switchToIframe=:', 'switchframe=:'), 'switchToIframe=', 'switchframe=')
    END,
    after_step = CASE
        WHEN cs.after_step IS NULL THEN NULL
        ELSE REPLACE(REPLACE(cs.after_step, 'switchToIframe=:', 'switchframe=:'), 'switchToIframe=', 'switchframe=')
    END
OUTPUT
    inserted.id,
    inserted.component_id,
    deleted.before_step,
    inserted.before_step,
    deleted.after_step,
    inserted.after_step
INTO #changed_rows (step_id, component_id, old_before_step, new_before_step, old_after_step, new_after_step)
FROM component_steps cs
WHERE cs.deleted_at IS NULL
  AND (
      cs.before_step LIKE '%switchToIframe=%'
      OR cs.after_step LIKE '%switchToIframe=%'
  );

SELECT
    COUNT(*) AS updated_rows
FROM #changed_rows;

SELECT
    step_id,
    component_id,
    old_before_step,
    new_before_step,
    old_after_step,
    new_after_step
FROM #changed_rows
ORDER BY component_id, step_id;

IF @ApplyChanges = 1
BEGIN
    COMMIT TRAN;
    PRINT 'Changes committed.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT 'Preview only. Set @ApplyChanges = 1 to commit.';
END;
