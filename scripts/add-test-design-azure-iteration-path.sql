IF COL_LENGTH('dbo.test_designs', 'azure_iteration_path') IS NULL
BEGIN
    ALTER TABLE dbo.test_designs
    ADD azure_iteration_path NVARCHAR(512) NULL;
END;