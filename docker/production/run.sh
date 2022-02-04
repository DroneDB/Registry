#!/bin/bash

export MYSQL_ROOT_PASSWORD="default-root-password"
export MYSQL_PASSWORD="default-mysql-password"
export REGISTRY_ADMIN_MAIL="test@test.it"
export REGISTRY_ADMIN_PASSWORD="password"
export REGISTRY_SECRET="e7er2yjacmbqjxsmf6h3rtrh7t6wjhef7bkv6kauv3wng3jb3t5hx7jtjry5z2ydd6utbufgq6jar2v3cvexhcescgzacfwvg5kqfa3gx3ppzchdtwcakx5hr3s6485z"
export EXTERNAL_URL=""
export CONTROL_SWITCH='$controlSwitch'

if test -f ".env"; then
    echo "Loading .env file"
    # Override default variables from .env file
    export $(grep -v '^#' .env | xargs)    
else
    echo ".env file does not exist, using default config (NOT FOR PRODUCTION)"
fi

rm appsettings.json
rm initialize.sql

envsubst < appsettings-template.json > appsettings.json
envsubst < initialize-template.sql > initialize.sql

mkdir data

if [[ "${@#--dry}" = "$@" ]]; then
    docker-compose up -d    
else
    cat initialize.sql
    cat appsettings.json
    docker-compose config   
fi
