SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.clients', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.clients
    (
        id INT NOT NULL PRIMARY KEY,
        account_status NVARCHAR(50) NULL,
        account_disable_reason NVARCHAR(100) NULL,
        mfa_required BIT NOT NULL CONSTRAINT DF_clients_mfa_required DEFAULT 0,
        sso_required BIT NOT NULL CONSTRAINT DF_clients_sso_required DEFAULT 0,
        ip_allowlist_json NVARCHAR(MAX) NULL,
        max_users INT NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL
    );
END;

IF OBJECT_ID(N'dbo.users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.users
    (
        id INT NOT NULL PRIMARY KEY,
        name NVARCHAR(255) NOT NULL,
        email NVARCHAR(255) NOT NULL,
        client_id INT NULL,
        is_client INT NOT NULL CONSTRAINT DF_users_is_client DEFAULT 0,
        is_active BIT NOT NULL CONSTRAINT DF_users_is_active DEFAULT 1,
        mfa_enabled BIT NOT NULL CONSTRAINT DF_users_mfa_enabled DEFAULT 0,
        sso_enabled BIT NOT NULL CONSTRAINT DF_users_sso_enabled DEFAULT 0,
        must_reset_password BIT NOT NULL CONSTRAINT DF_users_must_reset_password DEFAULT 0,
        phone NVARCHAR(50) NULL,
        job_title NVARCHAR(255) NULL,
        department NVARCHAR(255) NULL,
        timezone NVARCHAR(100) NULL,
        avatar_path NVARCHAR(500) NULL,
        [password] NVARCHAR(255) NOT NULL,
        email_verified_at DATETIMEOFFSET NULL,
        deleted_at DATETIMEOFFSET NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL,
        CONSTRAINT FK_users_clients FOREIGN KEY (client_id) REFERENCES dbo.clients (id)
    );

    CREATE UNIQUE INDEX IX_users_email ON dbo.users (email);
END;

IF OBJECT_ID(N'dbo.roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.roles
    (
        id INT NOT NULL PRIMARY KEY,
        name NVARCHAR(255) NOT NULL,
        guard_name NVARCHAR(255) NOT NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL
    );

    CREATE UNIQUE INDEX IX_roles_name_guard_name ON dbo.roles (name, guard_name);
END;

IF OBJECT_ID(N'dbo.permissions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.permissions
    (
        id INT NOT NULL PRIMARY KEY,
        category NVARCHAR(255) NOT NULL,
        description NVARCHAR(1000) NULL,
        name NVARCHAR(255) NOT NULL,
        guard_name NVARCHAR(255) NOT NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL
    );

    CREATE UNIQUE INDEX IX_permissions_name_guard_name ON dbo.permissions (name, guard_name);
END;

IF OBJECT_ID(N'dbo.model_has_roles', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.model_has_roles
    (
        role_id INT NOT NULL,
        model_type NVARCHAR(255) NOT NULL,
        model_id INT NOT NULL,
        CONSTRAINT PK_model_has_roles PRIMARY KEY (role_id, model_id, model_type),
        CONSTRAINT FK_model_has_roles_roles FOREIGN KEY (role_id) REFERENCES dbo.roles (id)
    );

    CREATE INDEX IX_model_has_roles_model_lookup ON dbo.model_has_roles (model_id, model_type);
END;

IF OBJECT_ID(N'dbo.role_has_permissions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.role_has_permissions
    (
        permission_id INT NOT NULL,
        role_id INT NOT NULL,
        CONSTRAINT PK_role_has_permissions PRIMARY KEY (permission_id, role_id),
        CONSTRAINT FK_role_has_permissions_permissions FOREIGN KEY (permission_id) REFERENCES dbo.permissions (id),
        CONSTRAINT FK_role_has_permissions_roles FOREIGN KEY (role_id) REFERENCES dbo.roles (id)
    );
END;

IF OBJECT_ID(N'dbo.user_settings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.user_settings
    (
        id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        user_id INT NOT NULL,
        settings NVARCHAR(MAX) NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL,
        CONSTRAINT FK_user_settings_users FOREIGN KEY (user_id) REFERENCES dbo.users (id)
    );

    CREATE INDEX IX_user_settings_user_id ON dbo.user_settings (user_id);
END;

IF OBJECT_ID(N'dbo.ticketing_systems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ticketing_systems
    (
        id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        client_id INT NOT NULL,
        ticketing_token NVARCHAR(1000) NULL,
        created_at DATETIMEOFFSET NULL,
        updated_at DATETIMEOFFSET NULL,
        CONSTRAINT FK_ticketing_systems_clients FOREIGN KEY (client_id) REFERENCES dbo.clients (id)
    );

    CREATE INDEX IX_ticketing_systems_client_id ON dbo.ticketing_systems (client_id);
END;

MERGE dbo.clients AS target
USING (
    SELECT
        100 AS id,
        N'active' AS account_status,
        CAST(NULL AS NVARCHAR(100)) AS account_disable_reason,
        CAST(0 AS BIT) AS mfa_required,
        CAST(0 AS BIT) AS sso_required,
        CAST(NULL AS NVARCHAR(MAX)) AS ip_allowlist_json,
        25 AS max_users,
        SYSDATETIMEOFFSET() AS created_at,
        SYSDATETIMEOFFSET() AS updated_at
) AS source
ON target.id = source.id
WHEN MATCHED THEN
    UPDATE SET
        account_status = source.account_status,
        account_disable_reason = source.account_disable_reason,
        mfa_required = source.mfa_required,
        sso_required = source.sso_required,
        ip_allowlist_json = source.ip_allowlist_json,
        max_users = source.max_users,
        updated_at = source.updated_at
WHEN NOT MATCHED THEN
    INSERT (id, account_status, account_disable_reason, mfa_required, sso_required, ip_allowlist_json, max_users, created_at, updated_at)
    VALUES (source.id, source.account_status, source.account_disable_reason, source.mfa_required, source.sso_required, source.ip_allowlist_json, source.max_users, source.created_at, source.updated_at);

MERGE dbo.roles AS target
USING (
    SELECT 1 AS id, N'Client Owner' AS name, N'web' AS guard_name, SYSDATETIMEOFFSET() AS created_at, SYSDATETIMEOFFSET() AS updated_at
) AS source
ON target.id = source.id
WHEN MATCHED THEN
    UPDATE SET name = source.name, guard_name = source.guard_name, updated_at = source.updated_at
WHEN NOT MATCHED THEN
    INSERT (id, name, guard_name, created_at, updated_at)
    VALUES (source.id, source.name, source.guard_name, source.created_at, source.updated_at);

MERGE dbo.permissions AS target
USING (
    SELECT 1 AS id, N'Dashboard' AS category, N'View dashboard' AS description, N'dashboard.view' AS name, N'web' AS guard_name, SYSDATETIMEOFFSET() AS created_at, SYSDATETIMEOFFSET() AS updated_at
    UNION ALL
    SELECT 2, N'Roles', N'View roles', N'roles.view', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 3, N'Roles', N'Create roles', N'roles.create', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 4, N'Roles', N'Edit roles', N'roles.edit', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 5, N'Queue', N'Read queue and schedules', N'Read Queue', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 6, N'Queue', N'Create queue and schedules', N'Create Queue', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 7, N'Queue', N'Update queue and schedules', N'Update Queue', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 8, N'Queue', N'Delete queue and schedules', N'Delete Queue', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 9, N'Integration', N'Read integrations', N'Read Integration', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 10, N'Integration', N'Create integrations', N'Create Integration', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 11, N'Integration', N'Update integrations', N'Update Integration', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
    UNION ALL
    SELECT 12, N'Integration', N'Delete integrations', N'Delete Integration', N'web', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET()
) AS source
ON target.id = source.id
WHEN MATCHED THEN
    UPDATE SET
        category = source.category,
        description = source.description,
        name = source.name,
        guard_name = source.guard_name,
        updated_at = source.updated_at
WHEN NOT MATCHED THEN
    INSERT (id, category, description, name, guard_name, created_at, updated_at)
    VALUES (source.id, source.category, source.description, source.name, source.guard_name, source.created_at, source.updated_at);

MERGE dbo.users AS target
USING (
    SELECT
        1 AS id,
        N'Client Owner' AS name,
        N'client@example.com' AS email,
        100 AS client_id,
        1 AS is_client,
        CAST(1 AS BIT) AS is_active,
        CAST(0 AS BIT) AS mfa_enabled,
        CAST(0 AS BIT) AS sso_enabled,
        CAST(0 AS BIT) AS must_reset_password,
        CAST(NULL AS NVARCHAR(50)) AS phone,
        N'QA Lead' AS job_title,
        N'Quality Engineering' AS department,
        N'America/New_York' AS timezone,
        CAST(NULL AS NVARCHAR(500)) AS avatar_path,
        N'$2y$10$RXADCzuElVvdJ1PI8Qvcge7tB0zu/a3WlHp2r/ivmsc12RNKXirGe' AS [password],
        CAST(NULL AS DATETIMEOFFSET) AS email_verified_at,
        CAST(NULL AS DATETIMEOFFSET) AS deleted_at,
        SYSDATETIMEOFFSET() AS created_at,
        SYSDATETIMEOFFSET() AS updated_at
) AS source
ON target.id = source.id
WHEN MATCHED THEN
    UPDATE SET
        name = source.name,
        email = source.email,
        client_id = source.client_id,
        is_client = source.is_client,
        is_active = source.is_active,
        mfa_enabled = source.mfa_enabled,
        sso_enabled = source.sso_enabled,
        must_reset_password = source.must_reset_password,
        phone = source.phone,
        job_title = source.job_title,
        department = source.department,
        timezone = source.timezone,
        avatar_path = source.avatar_path,
        [password] = source.[password],
        deleted_at = source.deleted_at,
        updated_at = source.updated_at
WHEN NOT MATCHED THEN
    INSERT (id, name, email, client_id, is_client, is_active, mfa_enabled, sso_enabled, must_reset_password, phone, job_title, department, timezone, avatar_path, [password], email_verified_at, deleted_at, created_at, updated_at)
    VALUES (source.id, source.name, source.email, source.client_id, source.is_client, source.is_active, source.mfa_enabled, source.sso_enabled, source.must_reset_password, source.phone, source.job_title, source.department, source.timezone, source.avatar_path, source.[password], source.email_verified_at, source.deleted_at, source.created_at, source.updated_at);

IF NOT EXISTS (
    SELECT 1
    FROM dbo.model_has_roles
    WHERE role_id = 1 AND model_type = N'App\Models\User' AND model_id = 1
)
BEGIN
    INSERT INTO dbo.model_has_roles (role_id, model_type, model_id)
    VALUES (1, N'App\Models\User', 1);
END;

IF NOT EXISTS (
    SELECT 1
    FROM dbo.role_has_permissions
    WHERE permission_id = 1 AND role_id = 1
)
BEGIN
    INSERT INTO dbo.role_has_permissions (permission_id, role_id)
    VALUES (1, 1), (2, 1), (3, 1), (4, 1);
END;

INSERT INTO dbo.role_has_permissions (permission_id, role_id)
SELECT permission_id, role_id
FROM (
    VALUES (5, 1), (6, 1), (7, 1), (8, 1), (9, 1), (10, 1), (11, 1), (12, 1)
) AS source(permission_id, role_id)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.role_has_permissions existing
    WHERE existing.permission_id = source.permission_id
      AND existing.role_id = source.role_id
);

IF NOT EXISTS (
    SELECT 1
    FROM dbo.user_settings
    WHERE user_id = 1
)
BEGIN
    INSERT INTO dbo.user_settings (user_id, settings, created_at, updated_at)
    VALUES (1, N'{}', SYSDATETIMEOFFSET(), SYSDATETIMEOFFSET());
END;