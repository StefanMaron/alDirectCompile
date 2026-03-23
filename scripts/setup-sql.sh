#!/bin/bash
# Restore CRONUS database and set up BC requirements.
# Run after docker compose up -d sql

set -e

SA_PASSWORD='Passw0rd123!'
SQLCMD="docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $SA_PASSWORD -C -No"

echo "Waiting for SQL Server..."
until $SQLCMD -Q "SELECT 1" &>/dev/null; do
    sleep 2
done
echo "SQL Server ready."

# Create login for BC
$SQLCMD -Q "
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'bctest')
    CREATE LOGIN bctest WITH PASSWORD = 'Test1234', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ELSE
    ALTER LOGIN bctest WITH PASSWORD = 'Test1234', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ALTER SERVER ROLE sysadmin ADD MEMBER bctest;
"
echo "Login 'bctest' ready."

# Restore CRONUS if needed
EXISTS=$(docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -C -No -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='CRONUS'" 2>/dev/null | tr -d '[:space:]')

if [ "$EXISTS" = "1" ]; then
    echo "CRONUS database already exists."
else
    echo "Restoring CRONUS database..."
    docker compose exec sql bash -c "ln -sf '/backup/BusinessCentral-W1.bak' /tmp/cronus.bak"

    # Get logical file names from backup
    FILELIST=$($SQLCMD -h -1 -Q "RESTORE FILELISTONLY FROM DISK='/tmp/cronus.bak'" 2>/dev/null)
    DATA_NAME=$(echo "$FILELIST" | head -1 | awk '{print $1}')
    LOG_NAME=$(echo "$FILELIST" | head -2 | tail -1 | awk '{print $1}')
    echo "Logical files: $DATA_NAME, $LOG_NAME"

    $SQLCMD -Q "
        RESTORE DATABASE [CRONUS] FROM DISK='/tmp/cronus.bak'
        WITH MOVE '$DATA_NAME' TO '/var/opt/mssql/data/CRONUS.mdf',
             MOVE '$LOG_NAME' TO '/var/opt/mssql/data/CRONUS_log.ldf'
    "
    echo "CRONUS restored."
fi

SQLCMD_DB="docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U bctest -P Test1234 -d CRONUS -C -No"

# Import encryption public key
echo "Setting up encryption key..."
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '\$ndo\$publicencryptionkey')
    CREATE TABLE [dbo].[\$ndo\$publicencryptionkey] ([id] INT NOT NULL PRIMARY KEY, [publickey] NVARCHAR(1024) NOT NULL);
DELETE FROM [dbo].[\$ndo\$publicencryptionkey] WHERE [id] = 0;
INSERT INTO [dbo].[\$ndo\$publicencryptionkey] ([id], [publickey]) VALUES (0,
N'<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>');
"
echo "Encryption key ready."

# Import license
echo "Importing license..."
docker cp "artifacts/sandbox/27.5.46862.48004/w1/Cronus.bclicense" aldirectcompile-sql-1:/tmp/Cronus.bclicense
$SQLCMD_DB -Q "
UPDATE [\$ndo\$dbproperty]
SET [license] = (SELECT BulkColumn FROM OPENROWSET(BULK '/tmp/Cronus.bclicense', SINGLE_BLOB) AS f);
"
echo "License imported."

# Set tenant to Sandbox (enables test automation)
$SQLCMD_DB -Q "
UPDATE [\$ndo\$tenantproperty] SET tenanttype = 1 WHERE tenantid = 'default';
"
echo "Tenant set to Sandbox."

# Create admin user with password Admin123!
echo "Creating admin user..."
USER_GUID='00000000-0000-0000-0000-000000000001'
PASSWORD_HASH='aXD91GRctWiXaqXeWbXhxQ==-V3'

$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT 1 FROM [User] WHERE [User Name] = 'admin')
BEGIN
    INSERT INTO [User] ([User Security ID], [User Name], [Full Name], [State], [Expiry Date],
        [Windows Security ID], [Change Password], [License Type], [Authentication Email],
        [Contact Email], [Exchange Identifier], [Application ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'admin', N'Admin', 0, '2099-12-31', N'', 0, 0, N'', N'', N'',
        '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');

    INSERT INTO [User Property] ([User Security ID], [Password], [Name Identifier],
        [Authentication Key], [WebServices Key], [WebServices Key Expiry Date],
        [Authentication Object ID], [Directory Role ID], [Telemetry User ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'$PASSWORD_HASH', N'', N'', N'', '1753-01-01', N'', N'', '$USER_GUID',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');

    INSERT INTO [Access Control] ([User Security ID], [Role ID], [Company Name], [Scope], [App ID],
        [\$systemId], [\$systemCreatedAt], [\$systemCreatedBy], [\$systemModifiedAt], [\$systemModifiedBy])
    VALUES ('$USER_GUID', N'SUPER', N'', 0, '00000000-0000-0000-0000-000000000000',
        NEWID(), GETUTCDATE(), '$USER_GUID', GETUTCDATE(), '$USER_GUID');
END
"
echo "Admin user ready (admin / Admin123!)."
echo ""
echo "Setup complete!"
