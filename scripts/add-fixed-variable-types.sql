SET NOCOUNT ON;

/*
  Adds fixed-value custom variable types for frontend and runner usage.
  Safe to run multiple times.
*/

DECLARE @utcNow datetime2(7) = SYSUTCDATETIME();

IF EXISTS (SELECT 1 FROM dbo.variable_types WHERE executable_method = N'string')
BEGIN
    UPDATE dbo.variable_types
    SET name = N'String',
        method = N'static',
        value = NULL,
        params = NULL,
        is_encrypted = 0,
        updated_at = @utcNow
    WHERE executable_method = N'string';
END
ELSE
BEGIN
    INSERT INTO dbo.variable_types (name, method, executable_method, value, params, is_encrypted, created_at, updated_at)
    VALUES (N'String', N'static', N'string', NULL, NULL, 0, @utcNow, @utcNow);
END;

IF EXISTS (SELECT 1 FROM dbo.variable_types WHERE executable_method = N'numeric')
BEGIN
    UPDATE dbo.variable_types
    SET name = N'Numeric',
        method = N'static',
        value = NULL,
        params = NULL,
        is_encrypted = 0,
        updated_at = @utcNow
    WHERE executable_method = N'numeric';
END
ELSE
BEGIN
    INSERT INTO dbo.variable_types (name, method, executable_method, value, params, is_encrypted, created_at, updated_at)
    VALUES (N'Numeric', N'static', N'numeric', NULL, NULL, 0, @utcNow, @utcNow);
END;