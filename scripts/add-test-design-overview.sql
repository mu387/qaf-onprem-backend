IF COL_LENGTH('dbo.test_designs', 'overview') IS NULL
BEGIN
    ALTER TABLE dbo.test_designs
    ADD overview NVARCHAR(MAX) NULL;
END;
