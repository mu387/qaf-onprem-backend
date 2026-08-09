param(
    [string]$SourceConnectionString = 'Server=localhost;Database=QafOnPremDotNet;Integrated Security=True;TrustServerCertificate=True;Encrypt=False;',
    [Parameter(Mandatory = $true)]
    [string]$TargetConnectionString,
    [string]$SchemaName = 'dbo',
    [int]$BatchSize = 1000,
    [switch]$SkipValidation
)

$ErrorActionPreference = 'Stop'

function Open-SqlConnection {
    param([string]$ConnectionString)

    $connection = [System.Data.SqlClient.SqlConnection]::new($ConnectionString)
    $connection.Open()
    return $connection
}

function Get-ConnectionIdentity {
    param([string]$ConnectionString)

    $builder = [System.Data.SqlClient.SqlConnectionStringBuilder]::new($ConnectionString)
    return ('{0}::{1}' -f $builder.DataSource.Trim().ToLowerInvariant(), $builder.InitialCatalog.Trim().ToLowerInvariant())
}

function Invoke-SqlQuery {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 0
    $command.CommandText = $CommandText
    foreach ($entry in $Parameters.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
    }

    $table = New-Object System.Data.DataTable
    $reader = $command.ExecuteReader()
    try {
        $table.Load($reader)
    }
    finally {
        $reader.Dispose()
        $command.Dispose()
    }

    return ,$table
}

function Invoke-SqlNonQuery {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 0
    $command.CommandText = $CommandText
    foreach ($entry in $Parameters.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
    }

    try {
        [void]$command.ExecuteNonQuery()
    }
    finally {
        $command.Dispose()
    }
}

function Get-SqlScalar {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$CommandText,
        [hashtable]$Parameters = @{}
    )

    $command = $Connection.CreateCommand()
    $command.CommandTimeout = 0
    $command.CommandText = $CommandText
    foreach ($entry in $Parameters.GetEnumerator()) {
        [void]$command.Parameters.AddWithValue($entry.Key, $entry.Value)
    }

    try {
        return $command.ExecuteScalar()
    }
    finally {
        $command.Dispose()
    }
}

function Escape-SqlIdentifier {
    param([string]$Name)

    return '[' + $Name.Replace(']', ']]') + ']'
}

function Get-SqlTypeDefinition {
    param([System.Data.DataRow]$Column)

    $dataType = [string]$Column.data_type
    $maxLength = [int]$Column.max_length
    $precision = [int]$Column.precision
    $scale = [int]$Column.scale

    switch ($dataType.ToLowerInvariant()) {
        'nvarchar' {
            if ($maxLength -eq -1) { return 'NVARCHAR(MAX)' }
            return 'NVARCHAR(' + ($maxLength / 2) + ')'
        }
        'nchar' {
            return 'NCHAR(' + ($maxLength / 2) + ')'
        }
        'varchar' {
            if ($maxLength -eq -1) { return 'VARCHAR(MAX)' }
            return 'VARCHAR(' + $maxLength + ')'
        }
        'char' {
            return 'CHAR(' + $maxLength + ')'
        }
        'varbinary' {
            if ($maxLength -eq -1) { return 'VARBINARY(MAX)' }
            return 'VARBINARY(' + $maxLength + ')'
        }
        'binary' {
            return 'BINARY(' + $maxLength + ')'
        }
        'decimal' {
            return 'DECIMAL(' + $precision + ',' + $scale + ')'
        }
        'numeric' {
            return 'NUMERIC(' + $precision + ',' + $scale + ')'
        }
        'datetime2' {
            return 'DATETIME2(' + $scale + ')'
        }
        'datetimeoffset' {
            return 'DATETIMEOFFSET(' + $scale + ')'
        }
        'time' {
            return 'TIME(' + $scale + ')'
        }
        default {
            return $dataType.ToUpperInvariant()
        }
    }
}

function Convert-ReferentialAction {
    param([string]$Action)

    return $Action.Replace('_', ' ')
}

function Get-SourceTables {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
SELECT t.name
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName
  AND t.is_ms_shipped = 0
ORDER BY t.name;
"@

    return Invoke-SqlQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Get-SourceColumns {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
SELECT
    t.name AS table_name,
    c.column_id,
    c.name AS column_name,
    ty.name AS data_type,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    CAST(ISNULL(ic.seed_value, 0) AS BIGINT) AS identity_seed,
    CAST(ISNULL(ic.increment_value, 0) AS BIGINT) AS identity_increment,
    dc.name AS default_constraint_name,
    dc.definition AS default_definition
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.columns c ON c.object_id = t.object_id
INNER JOIN sys.types ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE s.name = @schemaName
  AND t.is_ms_shipped = 0
  AND c.is_computed = 0
ORDER BY t.name, c.column_id;
"@

    return Invoke-SqlQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Get-PrimaryKeys {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
SELECT
    t.name AS table_name,
    kc.name AS constraint_name,
    ic.key_ordinal,
    c.name AS column_name
FROM sys.key_constraints kc
INNER JOIN sys.tables t ON t.object_id = kc.parent_object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = @schemaName
  AND kc.type = 'PK'
ORDER BY t.name, ic.key_ordinal;
"@

    return Invoke-SqlQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Get-Indexes {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
SELECT
    t.name AS table_name,
    i.name AS index_name,
    i.is_unique,
    i.has_filter,
    i.filter_definition,
    ic.key_ordinal,
    ic.is_included_column,
    ic.index_column_id,
    c.name AS column_name
FROM sys.indexes i
INNER JOIN sys.tables t ON t.object_id = i.object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE s.name = @schemaName
  AND t.is_ms_shipped = 0
  AND i.index_id > 0
  AND i.is_primary_key = 0
  AND i.is_unique_constraint = 0
  AND i.is_hypothetical = 0
  AND i.type IN (1, 2)
ORDER BY t.name, i.name, ic.is_included_column, ic.key_ordinal, ic.index_column_id;
"@

    return Invoke-SqlQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Get-ForeignKeys {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
SELECT
    fk.name AS constraint_name,
    pt.name AS table_name,
    pc.name AS column_name,
    rt.name AS referenced_table_name,
    rc.name AS referenced_column_name,
    fkc.constraint_column_id,
    fk.update_referential_action_desc,
    fk.delete_referential_action_desc
FROM sys.foreign_keys fk
INNER JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
INNER JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
WHERE ps.name = @schemaName
ORDER BY pt.name, fk.name, fkc.constraint_column_id;
"@

    return Invoke-SqlQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Ensure-TargetSchema {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $sql = @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @schemaName)
BEGIN
    DECLARE @createSchemaSql NVARCHAR(MAX) = N'CREATE SCHEMA ' + QUOTENAME(@schemaName) + N';';
    EXEC sp_executesql @createSchemaSql;
END
"@

    Invoke-SqlNonQuery -Connection $Connection -CommandText $sql -Parameters @{ '@schemaName' = $Schema }
}

function Remove-TargetObjects {
    param(
        [System.Data.SqlClient.SqlConnection]$Connection,
        [string]$Schema
    )

    $dropForeignKeysSql = @"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';'
FROM sys.foreign_keys fk
INNER JOIN sys.tables t ON t.object_id = fk.parent_object_id
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName;
IF (@sql <> N'') EXEC sp_executesql @sql;
"@

    $dropTablesSql = @"
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + N'DROP TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N';'
FROM sys.tables t
INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schemaName
  AND t.is_ms_shipped = 0;
IF (@sql <> N'') EXEC sp_executesql @sql;
"@

    Invoke-SqlNonQuery -Connection $Connection -CommandText $dropForeignKeysSql -Parameters @{ '@schemaName' = $Schema }
    Invoke-SqlNonQuery -Connection $Connection -CommandText $dropTablesSql -Parameters @{ '@schemaName' = $Schema }
}

$sourceIdentity = Get-ConnectionIdentity -ConnectionString $SourceConnectionString
$targetIdentity = Get-ConnectionIdentity -ConnectionString $TargetConnectionString
if ($sourceIdentity -eq $targetIdentity) {
    throw 'Source and target databases resolve to the same connection identity. Refusing to continue.'
}

$sourceConnection = Open-SqlConnection -ConnectionString $SourceConnectionString
$targetConnection = Open-SqlConnection -ConnectionString $TargetConnectionString

try {
    $tables = Get-SourceTables -Connection $sourceConnection -Schema $SchemaName
    $columns = Get-SourceColumns -Connection $sourceConnection -Schema $SchemaName
    $primaryKeys = Get-PrimaryKeys -Connection $sourceConnection -Schema $SchemaName
    $indexes = Get-Indexes -Connection $sourceConnection -Schema $SchemaName
    $foreignKeys = Get-ForeignKeys -Connection $sourceConnection -Schema $SchemaName

    $tableNames = @($tables.Rows | ForEach-Object { $_.name })
    if ($tableNames.Count -eq 0) {
        throw 'No user tables were found in the source schema.'
    }

    Ensure-TargetSchema -Connection $targetConnection -Schema $SchemaName
    Remove-TargetObjects -Connection $targetConnection -Schema $SchemaName

    foreach ($tableName in $tableNames) {
        $tableColumns = @($columns.Select("table_name = '$tableName'", 'column_id ASC'))
        $tablePrimaryKeys = @($primaryKeys.Select("table_name = '$tableName'", 'key_ordinal ASC'))
        $columnDefinitions = New-Object System.Collections.Generic.List[string]

        foreach ($column in $tableColumns) {
            $columnSql = '{0} {1}' -f (Escape-SqlIdentifier $column.column_name), (Get-SqlTypeDefinition -Column $column)
            if ([bool]$column.is_identity) {
                $columnSql += ' IDENTITY(' + [string]$column.identity_seed + ',' + [string]$column.identity_increment + ')'
            }

            if (-not [bool]$column.is_nullable) {
                $columnSql += ' NOT NULL'
            }
            else {
                $columnSql += ' NULL'
            }

            if (-not [string]::IsNullOrWhiteSpace([string]$column.default_definition)) {
                $constraintName = [string]$column.default_constraint_name
                if ([string]::IsNullOrWhiteSpace($constraintName)) {
                    $constraintName = 'DF_' + $tableName + '_' + $column.column_name
                }
                $columnSql += ' CONSTRAINT ' + (Escape-SqlIdentifier $constraintName) + ' DEFAULT ' + [string]$column.default_definition
            }

            $columnDefinitions.Add($columnSql)
        }

        if ($tablePrimaryKeys.Count -gt 0) {
            $pkName = [string]$tablePrimaryKeys[0].constraint_name
            $pkColumns = ($tablePrimaryKeys | ForEach-Object { Escape-SqlIdentifier $_.column_name }) -join ', '
            $columnDefinitions.Add('CONSTRAINT ' + (Escape-SqlIdentifier $pkName) + ' PRIMARY KEY (' + $pkColumns + ')')
        }

        $createTableSql = 'CREATE TABLE {0}.{1} (' -f (Escape-SqlIdentifier $SchemaName), (Escape-SqlIdentifier $tableName)
        $createTableSql += [Environment]::NewLine + '    ' + ($columnDefinitions -join (',' + [Environment]::NewLine + '    '))
        $createTableSql += [Environment]::NewLine + ');'
        Invoke-SqlNonQuery -Connection $targetConnection -CommandText $createTableSql
        Write-Output ('created-table ' + $tableName)
    }

    foreach ($tableName in $tableNames) {
        $tableColumns = @($columns.Select("table_name = '$tableName'", 'column_id ASC'))
        $columnNames = @($tableColumns | ForEach-Object { [string]$_.column_name })
        $selectList = ($columnNames | ForEach-Object { Escape-SqlIdentifier $_ }) -join ', '
        $sourceCommand = $sourceConnection.CreateCommand()
        $sourceCommand.CommandTimeout = 0
        $sourceCommand.CommandText = 'SELECT ' + $selectList + ' FROM ' + (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $tableName) + ';'
        $sourceReader = $sourceCommand.ExecuteReader()

        try {
            $bulkOptions = [System.Data.SqlClient.SqlBulkCopyOptions]::KeepIdentity
            $bulkCopy = [System.Data.SqlClient.SqlBulkCopy]::new($targetConnection, $bulkOptions, $null)
            $bulkCopy.BatchSize = $BatchSize
            $bulkCopy.BulkCopyTimeout = 0
            $bulkCopy.DestinationTableName = (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $tableName)
            foreach ($columnName in $columnNames) {
                [void]$bulkCopy.ColumnMappings.Add($columnName, $columnName)
            }

            try {
                $bulkCopy.WriteToServer($sourceReader)
            }
            finally {
                $bulkCopy.Close()
            }
        }
        finally {
            $sourceReader.Dispose()
            $sourceCommand.Dispose()
        }

        Write-Output ('copied-table ' + $tableName)
    }

    $indexGroups = $indexes.Rows | Group-Object table_name, index_name
    foreach ($indexGroup in $indexGroups) {
        $rows = @($indexGroup.Group | Sort-Object is_included_column, key_ordinal, index_column_id)
        $first = $rows[0]
        $tableName = [string]$first.table_name
        $indexName = [string]$first.index_name
        $keyColumns = @($rows | Where-Object { -not [bool]$_.is_included_column } | ForEach-Object { Escape-SqlIdentifier $_.column_name })
        $includeColumns = @($rows | Where-Object { [bool]$_.is_included_column } | ForEach-Object { Escape-SqlIdentifier $_.column_name })
        $createIndexSql = 'CREATE '
        if ([bool]$first.is_unique) {
            $createIndexSql += 'UNIQUE '
        }
        $createIndexSql += 'INDEX ' + (Escape-SqlIdentifier $indexName) + ' ON ' + (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $tableName)
        $createIndexSql += ' (' + ($keyColumns -join ', ') + ')'
        if ($includeColumns.Count -gt 0) {
            $createIndexSql += ' INCLUDE (' + ($includeColumns -join ', ') + ')'
        }
        if ([bool]$first.has_filter -and -not [string]::IsNullOrWhiteSpace([string]$first.filter_definition)) {
            $createIndexSql += ' WHERE ' + [string]$first.filter_definition
        }
        $createIndexSql += ';'
        Invoke-SqlNonQuery -Connection $targetConnection -CommandText $createIndexSql
        Write-Output ('created-index ' + $tableName + '::' + $indexName)
    }

    $foreignKeyGroups = $foreignKeys.Rows | Group-Object table_name, constraint_name
    foreach ($foreignKeyGroup in $foreignKeyGroups) {
        $rows = @($foreignKeyGroup.Group | Sort-Object constraint_column_id)
        $first = $rows[0]
        $tableName = [string]$first.table_name
        $constraintName = [string]$first.constraint_name
        $referencedTableName = [string]$first.referenced_table_name
        $sourceColumns = @($rows | ForEach-Object { Escape-SqlIdentifier $_.column_name }) -join ', '
        $referencedColumns = @($rows | ForEach-Object { Escape-SqlIdentifier $_.referenced_column_name }) -join ', '
        $updateAction = Convert-ReferentialAction -Action ([string]$first.update_referential_action_desc)
        $deleteAction = Convert-ReferentialAction -Action ([string]$first.delete_referential_action_desc)
        $foreignKeySql = 'ALTER TABLE ' + (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $tableName)
        $foreignKeySql += ' ADD CONSTRAINT ' + (Escape-SqlIdentifier $constraintName)
        $foreignKeySql += ' FOREIGN KEY (' + $sourceColumns + ') REFERENCES ' + (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $referencedTableName)
        $foreignKeySql += ' (' + $referencedColumns + ') ON UPDATE ' + $updateAction + ' ON DELETE ' + $deleteAction + ';'
        Invoke-SqlNonQuery -Connection $targetConnection -CommandText $foreignKeySql
        Write-Output ('created-foreign-key ' + $tableName + '::' + $constraintName)
    }

    if (-not $SkipValidation) {
        foreach ($tableName in $tableNames) {
            $qualifiedTable = (Escape-SqlIdentifier $SchemaName) + '.' + (Escape-SqlIdentifier $tableName)
            $sourceCount = [int64](Get-SqlScalar -Connection $sourceConnection -CommandText ('SELECT COUNT(*) FROM ' + $qualifiedTable + ';'))
            $targetCount = [int64](Get-SqlScalar -Connection $targetConnection -CommandText ('SELECT COUNT(*) FROM ' + $qualifiedTable + ';'))
            Write-Output ('validated ' + $tableName + ' source=' + $sourceCount + ' target=' + $targetCount)
            if ($sourceCount -ne $targetCount) {
                throw ('Row count mismatch for table ' + $tableName + '. Source=' + $sourceCount + ' Target=' + $targetCount)
            }
        }
    }
}
finally {
    if ($sourceConnection) {
        $sourceConnection.Dispose()
    }
    if ($targetConnection) {
        $targetConnection.Dispose()
    }
}

Write-Output 'sqlserver-promotion-complete'