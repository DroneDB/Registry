#!/bin/bash

export MYSQL_ROOT_PASSWORD="default-root-password"
export MYSQL_PASSWORD="default-mysql-password"
export ADMIN_PASS="redisadminpass"
export REGISTRY_ADMIN_MAIL="test@test.it"
export REGISTRY_ADMIN_PASSWORD="password"
export REGISTRY_SECRET="e7er2yjacmbqjxsmf6h3rtrh7t6wjhef7bkv6kauv3wng3jb3t5hx7jtjry5z2ydd6utbufgq6jar2v3cvexhcescgzacfwvg5kqfa3gx3ppzchdtwcakx5hr3s6485z"
export REGISTRY_EXTERNAL_AUTH_URL=""
export EXTERNAL_URL=""

export S3_ENDPOINT="minio:9000"
export S3_ACCESS_KEY="minioadmin"
export S3_SECRET_KEY="miniopass"
export S3_REGION="us-east-1"
export S3_USE_SSL="false"

# 1GB size
export S3_CACHE_SIZE="1073741824"
# One day expiration
export S3_CACHE_EXPIRATION="24:00:00";

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

mkdir -p data

if [[ "${@#--dry}" = "$@" ]]; then
    docker-compose up --build -d    
else
    cat initialize.sql
    cat appsettings.json
    docker-compose config   
fi