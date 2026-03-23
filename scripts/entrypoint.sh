#!/bin/bash
# Self-contained BC service tier entrypoint.
# Downloads artifacts, restores DB, configures BC, publishes test runner, starts server.
set -eo pipefail
trap 'echo "[entrypoint] ERROR at line $LINENO: $BASH_COMMAND (exit $?)"' ERR

# Unbuffered output for Docker log visibility
if command -v stdbuf &>/dev/null; then
    exec 1> >(stdbuf -oL cat) 2>&1
fi

BC_TYPE="${BC_TYPE:-sandbox}"
BC_VERSION="${BC_VERSION:-27.5.46862.48004}"
BC_COUNTRY="${BC_COUNTRY:-w1}"
SA_PASSWORD="${SA_PASSWORD:-Passw0rd123!}"
BC_DB_PASSWORD="${BC_DB_PASSWORD:-Test1234}"
BC_DB_USER="${BC_DB_USER:-bctest}"
SQL_SERVER="${SQL_SERVER:-sql}"
ARTIFACTS="/bc/artifacts"
SERVICE_DIR="/bc/service"

# =============================================================================
# Step 1: Download artifacts if not already present
# =============================================================================
if [ ! -f "$ARTIFACTS/app/manifest.json" ]; then
    if [ -n "$BC_ARTIFACT_URL" ]; then
        echo "[entrypoint] Downloading BC from $BC_ARTIFACT_URL..."
        /bc/scripts/download-artifacts.sh "$BC_ARTIFACT_URL" "$ARTIFACTS"
    else
        echo "[entrypoint] Downloading BC $BC_TYPE $BC_VERSION ($BC_COUNTRY)..."
        /bc/scripts/download-artifacts.sh "$BC_TYPE" "$BC_VERSION" "$BC_COUNTRY" "$ARTIFACTS"
    fi
else
    echo "[entrypoint] Artifacts already cached."
fi

# Read manifest
MANIFEST="$ARTIFACTS/app/manifest.json"
DB_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('database',''))")
LICENSE_FILE=$(python3 -c "import json; print(json.load(open('$MANIFEST')).get('licenseFile',''))")
PLATFORM_VERSION=$(python3 -c "import json; print(json.load(open('$MANIFEST'))['platform'])")
MAJOR_VERSION=$(echo "$PLATFORM_VERSION" | cut -d. -f1)
NAV_DIR="${MAJOR_VERSION}0"

echo "[entrypoint] Platform: $PLATFORM_VERSION, NAV dir: $NAV_DIR, DB: $DB_FILE"

# =============================================================================
# Step 2: Copy service tier to working directory (if not already set up)
# =============================================================================
if [ ! -f "$SERVICE_DIR/Microsoft.Dynamics.Nav.Server.dll" ]; then
    echo "[entrypoint] Setting up service tier..."
    # Auto-detect service tier path (differs between versions: PFiles64 vs "program files")
    SRC=$(find "$ARTIFACTS/platform/ServiceTier" -name "Microsoft.Dynamics.Nav.Server.dll" -printf "%h\n" 2>/dev/null | head -1)
    if [ -z "$SRC" ] || [ ! -d "$SRC" ]; then
        echo "[entrypoint] ERROR: Service tier not found in $ARTIFACTS/platform/ServiceTier/"
        find "$ARTIFACTS/platform/ServiceTier" -maxdepth 4 -type d 2>/dev/null
        exit 1
    fi
    echo "[entrypoint] Found service tier at: $SRC"
    cp -r "$SRC/." "$SERVICE_DIR/"

    # Create temp directory BC expects (detect NAV_DIR from actual path)
    NAV_DIR=$(echo "$SRC" | grep -oP '\d{3}(?=/Service)')
    [ -z "$NAV_DIR" ] && NAV_DIR="${MAJOR_VERSION}0"
    mkdir -p "/usr/share/Microsoft/Microsoft Dynamics NAV/$NAV_DIR/Server"

    # Override framework DLLs
    cp /bc/hook/System.Security.Principal.Windows.dll /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.*/
    cp /bc/hook/Microsoft.AspNetCore.Server.HttpSys.dll /usr/share/dotnet/shared/Microsoft.AspNetCore.App/8.0.*/

    # Replace stub DLLs in service dir (Geneva, SqlClient, etc.)
    for stub in OpenTelemetry.Exporter.Geneva.dll Microsoft.Data.SqlClient.dll; do
        if [ -f "/bc/hook/$stub" ]; then
            [ -f "$SERVICE_DIR/$stub" ] && [ ! -f "$SERVICE_DIR/${stub}.orig" ] && cp "$SERVICE_DIR/$stub" "$SERVICE_DIR/${stub}.orig"
            cp "/bc/hook/$stub" "$SERVICE_DIR/$stub"
            echo "[entrypoint] Replaced $stub with stub/unix version"
        fi
    done

    # Patch CustomSettings.config
    CONFIG="$SERVICE_DIR/CustomSettings.config"
    sed -i \
        -e "s|DatabaseServer\" value=\"[^\"]*\"|DatabaseServer\" value=\"$SQL_SERVER\"|" \
        -e "s|DatabaseName\" value=\"[^\"]*\"|DatabaseName\" value=\"CRONUS\"|" \
        -e "s|DatabaseUserName\" value=\"[^\"]*\"|DatabaseUserName\" value=\"$BC_DB_USER\"|" \
        -e "s|ProtectedDatabasePassword\" value=\"[^\"]*\"|ProtectedDatabasePassword\" value=\"$BC_DB_PASSWORD\"|" \
        -e "s|ClientServicesCredentialType\" value=\"[^\"]*\"|ClientServicesCredentialType\" value=\"NavUserPassword\"|" \
        -e "s|DeveloperServicesEnabled\" value=\"[^\"]*\"|DeveloperServicesEnabled\" value=\"true\"|" \
        -e "s|TrustSQLServerCertificate\" value=\"[^\"]*\"|TrustSQLServerCertificate\" value=\"true\"|" \
        -e "s|ReportingServiceIsSideService\" value=\"[^\"]*\"|ReportingServiceIsSideService\" value=\"false\"|" \
        -e "s|ClientServicesPort\" value=\"[^\"]*\"|ClientServicesPort\" value=\"7085\"|" \
        -e "s|SOAPServicesPort\" value=\"[^\"]*\"|SOAPServicesPort\" value=\"7047\"|" \
        -e "s|ODataServicesPort\" value=\"[^\"]*\"|ODataServicesPort\" value=\"7048\"|" \
        -e "s|ManagementServicesPort\" value=\"[^\"]*\"|ManagementServicesPort\" value=\"7045\"|" \
        -e "s|ManagementApiServicesPort\" value=\"[^\"]*\"|ManagementApiServicesPort\" value=\"7086\"|" \
        -e "s|DeveloperServicesPort\" value=\"[^\"]*\"|DeveloperServicesPort\" value=\"7049\"|" \
        "$CONFIG"

    # Add settings if missing
    if ! grep -q "TenantEnvironmentType" "$CONFIG"; then
        sed -i '/<add key="TestAutomationEnabled"/a\  <add key="TenantEnvironmentType" value="Sandbox" />' "$CONFIG"
    fi
    if ! grep -q "TestAutomationEnabled" "$CONFIG"; then
        sed -i '/<\/appSettings>/i\  <add key="TestAutomationEnabled" value="true"/>' "$CONFIG"
    fi

    echo "[entrypoint] Service tier configured."
else
    echo "[entrypoint] Service tier already set up."
fi

# =============================================================================
# Step 3: Wait for SQL Server and set up database
# =============================================================================
export PATH="$PATH:/opt/mssql-tools18/bin"

echo "[entrypoint] Waiting for SQL Server..."
until sqlcmd -S "$SQL_SERVER" -U sa -P "$SA_PASSWORD" -C -No -Q "SELECT 1" &>/dev/null; do
    sleep 2
done
echo "[entrypoint] SQL Server ready."

SQLCMD="sqlcmd -S $SQL_SERVER -U sa -P $SA_PASSWORD -C -No"

# Create login
$SQLCMD -Q "
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = '$BC_DB_USER')
    CREATE LOGIN [$BC_DB_USER] WITH PASSWORD = '$BC_DB_PASSWORD', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ELSE
    ALTER LOGIN [$BC_DB_USER] WITH PASSWORD = '$BC_DB_PASSWORD', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ALTER SERVER ROLE sysadmin ADD MEMBER [$BC_DB_USER];
"

# Restore database if needed
DB_EXISTS=$($SQLCMD -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='CRONUS'" 2>/dev/null | tr -d '[:space:]')
if [ "$DB_EXISTS" != "1" ]; then
    echo "[entrypoint] Restoring CRONUS database..."
    BAK_PATH="$ARTIFACTS/app/$DB_FILE"
    if [ ! -f "$BAK_PATH" ]; then
        echo "[entrypoint] ERROR: Database backup not found at $BAK_PATH"
        exit 1
    fi

    # Get logical file names
    FILELIST=$($SQLCMD -h -1 -Q "RESTORE FILELISTONLY FROM DISK='$BAK_PATH'" 2>/dev/null)
    DATA_NAME=$(echo "$FILELIST" | head -1 | awk '{print $1}')
    LOG_NAME=$(echo "$FILELIST" | head -2 | tail -1 | awk '{print $1}')

    $SQLCMD -Q "
        RESTORE DATABASE [CRONUS] FROM DISK='$BAK_PATH'
        WITH MOVE '$DATA_NAME' TO '/var/opt/mssql/data/CRONUS.mdf',
             MOVE '$LOG_NAME' TO '/var/opt/mssql/data/CRONUS_log.ldf'
    "
    echo "[entrypoint] CRONUS restored."
else
    echo "[entrypoint] CRONUS already exists."
fi

SQLCMD_DB="sqlcmd -S $SQL_SERVER -U $BC_DB_USER -P $BC_DB_PASSWORD -d CRONUS -C -No"

# Encryption key
$SQLCMD_DB -Q "
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = '\$ndo\$publicencryptionkey')
    CREATE TABLE [dbo].[\$ndo\$publicencryptionkey] ([id] INT NOT NULL PRIMARY KEY, [publickey] NVARCHAR(1024) NOT NULL);
DELETE FROM [dbo].[\$ndo\$publicencryptionkey] WHERE [id] = 0;
INSERT INTO [dbo].[\$ndo\$publicencryptionkey] ([id], [publickey]) VALUES (0,
N'<RSAKeyValue><Modulus>xbzyD+SGxykyAv82XOEFtDzWEIok0MM5SAc+CS6Mq0W5LwiyXeakWyblq1XgYi3CDu700986ZVRi4KJjruZlzBeZ7IWXD4lEEpTCRuqoxasRTnwVpyVqGuHclJAnUpjeBS6HvaS/iesYWwxZcmlsmzJHvF3hXdDmLj+8GSKgo4IhschPCIpnoH8+FREX++VpwfZH1ejMk5Izds/ZI70Xc/OWfRfaYy3rtCFeZQ1R5T1AhlNJDgpn0a1oP86F8yDGYawB2GJKIewdcWE8usu4QesrFnlS1g/IJcFXe71/TiJjryqRJPk8ze3Jh9+atx57OnI4R3QvuM/lQ7YoN1RVjw==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>');
" 2>/dev/null

# License
if [ -n "$LICENSE_FILE" ] && [ -f "$ARTIFACTS/app/$LICENSE_FILE" ]; then
    $SQLCMD_DB -Q "
    UPDATE [\$ndo\$dbproperty]
    SET [license] = (SELECT BulkColumn FROM OPENROWSET(BULK '$ARTIFACTS/app/$LICENSE_FILE', SINGLE_BLOB) AS f);
    " 2>/dev/null
    echo "[entrypoint] License imported."
fi

# Sandbox tenant type
$SQLCMD_DB -Q "UPDATE [\$ndo\$tenantproperty] SET tenanttype = 1 WHERE tenantid = 'default';" 2>/dev/null

# Admin user (password hash for Admin123! with GUID 00000000-0000-0000-0000-000000000001)
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
" 2>/dev/null
echo "[entrypoint] Database ready (admin / Admin123!)."

# =============================================================================
# Step 4: Start BC server in background, publish test runner, then wait
# =============================================================================
cd "$SERVICE_DIR"

# Verify SQL is still accessible before starting BC
echo "[entrypoint] Verifying SQL connection..."
if sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" &>/dev/null; then
    echo "[entrypoint] SQL connection verified."
else
    echo "[entrypoint] ERROR: SQL connection failed! Retrying..."
    sleep 5
    sqlcmd -S "$SQL_SERVER" -U "$BC_DB_USER" -P "$BC_DB_PASSWORD" -d CRONUS -C -No -Q "SELECT 1" || {
        echo "[entrypoint] FATAL: Cannot connect to SQL"
        exit 1
    }
fi

echo "[entrypoint] Config check:"
grep -E "DatabaseServer|DatabaseName|DatabaseUserName|ProtectedDatabase" "$SERVICE_DIR/CustomSettings.config" | head -5
echo "[entrypoint] Starting BC service tier..."
# Start BC — use a FIFO to keep stdin open for /console mode
mkfifo /tmp/bc-stdin 2>/dev/null || true
DOTNET_STARTUP_HOOKS=/bc/hook/StartupHook.dll dotnet Microsoft.Dynamics.Nav.Server.dll /console < /tmp/bc-stdin &
BC_PID=$!
# Keep the FIFO writer open in background (prevents EOF)
exec 3>/tmp/bc-stdin

# Wait for dev endpoint to be ready, then publish test runner
(
    INSTANCE=$(grep -oP 'ServerInstance" value="\K[^"]+' $SERVICE_DIR/CustomSettings.config 2>/dev/null || echo "InstanceName")
    DEV_URL="http://localhost:7049/$INSTANCE/dev"

    echo "[entrypoint] Waiting for BC to start..."
    for i in $(seq 1 180); do
        # Check if BC process died
        if ! kill -0 $BC_PID 2>/dev/null; then
            echo "[entrypoint] ERROR: BC process died"
            wait $BC_PID 2>/dev/null
            exit 1
        fi
        HTTP=$(curl -s -o /dev/null -w "%{http_code}" --max-time 3 "$DEV_URL/packages" 2>&1)
        if [ "$HTTP" != "000" ]; then
            break
        fi
        sleep 2
    done

    if [ -f /bc/testrunner/TestRunner.app ]; then
        echo "[entrypoint] Publishing Test Runner Extension..."
        HTTP_CODE=$(curl -s -o /dev/null -w "%{http_code}" --max-time 30 \
            -u "admin:Admin123!" -X POST \
            -F "file=@/bc/testrunner/TestRunner.app;type=application/octet-stream" \
            "$DEV_URL/apps?SchemaUpdateMode=synchronize" 2>&1)
        echo "[entrypoint] Test Runner publish: HTTP $HTTP_CODE"
    fi
    echo "[entrypoint] Ready for test apps."
) &

wait $BC_PID
