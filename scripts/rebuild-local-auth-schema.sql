SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.role_has_permissions', N'U') IS NOT NULL DROP TABLE dbo.role_has_permissions;
IF OBJECT_ID(N'dbo.model_has_roles', N'U') IS NOT NULL DROP TABLE dbo.model_has_roles;
IF OBJECT_ID(N'dbo.user_settings', N'U') IS NOT NULL DROP TABLE dbo.user_settings;
IF OBJECT_ID(N'dbo.ticketing_systems', N'U') IS NOT NULL DROP TABLE dbo.ticketing_systems;
IF OBJECT_ID(N'dbo.users', N'U') IS NOT NULL DROP TABLE dbo.users;
IF OBJECT_ID(N'dbo.roles', N'U') IS NOT NULL DROP TABLE dbo.roles;
IF OBJECT_ID(N'dbo.permissions', N'U') IS NOT NULL DROP TABLE dbo.permissions;
IF OBJECT_ID(N'dbo.clients', N'U') IS NOT NULL DROP TABLE dbo.clients;

CREATE TABLE dbo.clients
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    first_name NVARCHAR(255) NULL,
    last_name NVARCHAR(255) NULL,
    email NVARCHAR(255) NULL,
    address_1 NVARCHAR(255) NULL,
    address_2 NVARCHAR(255) NULL,
    city NVARCHAR(255) NULL,
    state NVARCHAR(255) NULL,
    zip NVARCHAR(10) NULL,
    country NVARCHAR(100) NULL,
    phone_number NVARCHAR(10) NULL,
    cell_phone NVARCHAR(100) NULL,
    marketing_emails_consent NVARCHAR(255) NULL,
    ci_key_hash NVARCHAR(80) NULL,
    email_sandbox_inbound_key NVARCHAR(64) NULL,
    account_status NVARCHAR(30) NOT NULL CONSTRAINT DF_clients_account_status DEFAULT N'active',
    account_disable_reason NVARCHAR(50) NULL,
    mfa_required BIT NOT NULL CONSTRAINT DF_clients_mfa_required DEFAULT 0,
    sso_required BIT NOT NULL CONSTRAINT DF_clients_sso_required DEFAULT 0,
    ip_allowlist_json NVARCHAR(MAX) NULL,
    max_users INT NOT NULL CONSTRAINT DF_clients_max_users DEFAULT 10,
    pilot_client BIT NOT NULL CONSTRAINT DF_clients_pilot_client DEFAULT 0,
    replicate_demo BIT NOT NULL CONSTRAINT DF_clients_replicate_demo DEFAULT 0,
    deleted_at DATETIMEOFFSET NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL
);

CREATE INDEX IX_clients_email ON dbo.clients (email);
CREATE INDEX IX_clients_email_sandbox_inbound_key ON dbo.clients (email_sandbox_inbound_key);
CREATE INDEX IX_clients_account_status ON dbo.clients (account_status);
CREATE INDEX IX_clients_ci_key_hash ON dbo.clients (ci_key_hash);

CREATE TABLE dbo.permissions
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    category NVARCHAR(255) NOT NULL,
    description NVARCHAR(255) NOT NULL,
    name NVARCHAR(255) NOT NULL,
    guard_name NVARCHAR(255) NOT NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL
);

CREATE UNIQUE INDEX IX_permissions_name_guard_name ON dbo.permissions (name, guard_name);

CREATE TABLE dbo.roles
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name NVARCHAR(255) NOT NULL,
    guard_name NVARCHAR(255) NOT NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL,
    client_id BIGINT NULL,
    CONSTRAINT FK_roles_clients FOREIGN KEY (client_id) REFERENCES dbo.clients (id)
);

CREATE INDEX IX_roles_name_guard_name_client_id ON dbo.roles (name, guard_name, client_id);

CREATE TABLE dbo.users
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name NVARCHAR(255) NOT NULL,
    email NVARCHAR(255) NOT NULL,
    client_id BIGINT NULL,
    is_client BIT NOT NULL CONSTRAINT DF_users_is_client DEFAULT 0,
    is_active BIT NOT NULL CONSTRAINT DF_users_is_active DEFAULT 1,
    mfa_enabled BIT NOT NULL CONSTRAINT DF_users_mfa_enabled DEFAULT 0,
    sso_enabled BIT NOT NULL CONSTRAINT DF_users_sso_enabled DEFAULT 0,
    must_reset_password BIT NOT NULL CONSTRAINT DF_users_must_reset_password DEFAULT 0,
    email_verified_at DATETIMEOFFSET NULL,
    [password] NVARCHAR(255) NOT NULL,
    remember_token NVARCHAR(100) NULL,
    deleted_at DATETIMEOFFSET NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL,
    phone NVARCHAR(50) NULL,
    job_title NVARCHAR(255) NULL,
    department NVARCHAR(255) NULL,
    timezone NVARCHAR(100) NULL,
    avatar_path NVARCHAR(500) NULL,
    CONSTRAINT FK_users_clients FOREIGN KEY (client_id) REFERENCES dbo.clients (id)
);

CREATE UNIQUE INDEX IX_users_email ON dbo.users (email);

CREATE TABLE dbo.model_has_roles
(
    role_id BIGINT NOT NULL,
    model_type NVARCHAR(255) NOT NULL,
    model_id BIGINT NOT NULL,
    CONSTRAINT PK_model_has_roles PRIMARY KEY (role_id, model_id, model_type),
    CONSTRAINT FK_model_has_roles_roles FOREIGN KEY (role_id) REFERENCES dbo.roles (id) ON DELETE CASCADE
);

CREATE INDEX IX_model_has_roles_model_lookup ON dbo.model_has_roles (model_id, model_type);

CREATE TABLE dbo.role_has_permissions
(
    permission_id BIGINT NOT NULL,
    role_id BIGINT NOT NULL,
    CONSTRAINT PK_role_has_permissions PRIMARY KEY (permission_id, role_id),
    CONSTRAINT FK_role_has_permissions_permissions FOREIGN KEY (permission_id) REFERENCES dbo.permissions (id) ON DELETE CASCADE,
    CONSTRAINT FK_role_has_permissions_roles FOREIGN KEY (role_id) REFERENCES dbo.roles (id) ON DELETE CASCADE
);

CREATE TABLE dbo.user_settings
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    user_id BIGINT NULL,
    settings NVARCHAR(MAX) NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL,
    CONSTRAINT FK_user_settings_users FOREIGN KEY (user_id) REFERENCES dbo.users (id)
);

CREATE INDEX IX_user_settings_user_id ON dbo.user_settings (user_id);

CREATE TABLE dbo.ticketing_systems
(
    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    name NVARCHAR(255) NULL,
    username NVARCHAR(255) NULL,
    project_name NVARCHAR(255) NULL,
    ticketing_token NVARCHAR(MAX) NULL,
    ticketing_system_type_id BIGINT NULL,
    client_id BIGINT NOT NULL,
    created_at DATETIMEOFFSET NULL,
    updated_at DATETIMEOFFSET NULL,
    CONSTRAINT FK_ticketing_systems_clients FOREIGN KEY (client_id) REFERENCES dbo.clients (id)
);

CREATE INDEX IX_ticketing_systems_client_id ON dbo.ticketing_systems (client_id);