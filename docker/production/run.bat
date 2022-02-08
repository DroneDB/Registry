
@ECHO OFF

SET MYSQL_ROOT_PASSWORD=default-root-password
SET MYSQL_PASSWORD=default-mysql-password
SET REGISTRY_ADMIN_MAIL=test@test.it
SET REGISTRY_ADMIN_PASSWORD=password
SET REGISTRY_SECRET=e7er2yjacmbqjxsmf6h3rtrh7t6wjhef7bkv6kauv3wng3jb3t5hx7jtjry5z2ydd6utbufgq6jar2v3cvexhcescgzacfwvg5kqfa3gx3ppzchdtwcakx5hr3s6485z
SET EXTERNAL_URL=""
SET CONTROL_SWITCH=$controlSwitch

del appsettings.json
del initialize.sql

mkdir data

if exist .env (
    
    echo Loading .env file

    :: Load environment variables in .env file
    FOR /F "tokens=*" %%i in ('type .env') do SET %%i

) else (
    echo .env file does not exist, using default config (NOT FOR PRODUCTION)
)

:: Using envsubst.exe from https://github.com/HeDo88TH/envsubst-win

:: replace environment variables in file appsettings-template.json
envsubst-win.exe "appsettings-template.json" "appsettings.json"

:: replace environment variables in file initialize-template.sql
envsubst-win.exe "initialize-template.sql" "initialize.sql"

if "%1"=="--dry" (    
    echo Dry run, not starting docker-compose
    type initialize.sql
    type appsettings.json
    docker-compose config 
) else (
    echo Starting docker-compose
    docker-compose up -d
)
    