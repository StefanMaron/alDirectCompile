#!/bin/bash
# Restore CRONUS database into the Docker SQL container.
# Run after docker compose up -d sql

set -e

echo "Waiting for SQL Server..."
until docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Passw0rd123!' -C -No -Q "SELECT 1" &>/dev/null; do
    sleep 2
done
echo "SQL Server ready."

# Create login with blank password for BC
docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Passw0rd123!' -C -No -Q "
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = 'bctest')
    CREATE LOGIN bctest WITH PASSWORD = '', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
ALTER SERVER ROLE sysadmin ADD MEMBER bctest;
"
echo "Login 'bctest' ready."

# Check if CRONUS already exists
EXISTS=$(docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Passw0rd123!' -C -No -h -1 -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='CRONUS'" 2>/dev/null | tr -d '[:space:]')

if [ "$EXISTS" = "1" ]; then
    echo "CRONUS database already exists."
else
    echo "Restoring CRONUS database..."
    docker compose exec sql bash -c "ln -sf '/backup/Demo Database BC (27-0).bak' /tmp/cronus.bak"
    docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'Passw0rd123!' -C -No -Q "
        RESTORE DATABASE [CRONUS] FROM DISK='/tmp/cronus.bak'
        WITH MOVE 'Demo Database BC (27-0)_Data' TO '/var/opt/mssql/data/CRONUS.mdf',
             MOVE 'Demo Database BC (27-0)_Log' TO '/var/opt/mssql/data/CRONUS_log.ldf'
    "
    echo "CRONUS restored."
fi
