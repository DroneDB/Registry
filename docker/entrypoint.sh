#!/bin/sh
# Registry container entrypoint (POSIX sh).
#
# Starts /Registry/Registry.Web for ${INSTANCE_TYPE} (0 web + processing, 1 processing node,
# 2 web server, 3 thumbnail generator) on 0.0.0.0:${REGISTRY_PORT} with /data as storage folder;
# extra container arguments are appended to the Registry command line.
#
# Started as root (the default), it first makes the data the app writes owned by ${APP_USER}, then
# drops privileges with setpriv. Started with --user, it runs the app as that user unchanged.
# Either way the app is exec'ed, so it is PID 1 and receives SIGTERM from `docker stop`
# (graceful shutdown: Hangfire drain, ddb checkpoint).
#
# Ownership is checked on the top-level of each path, so a migrated volume costs one stat per start.
# Only when the owner differs (first start after the switch to a non-root user, or a file the host
# re-created, such as the rendered appsettings.json) are the entries not yet owned changed; the
# top-level path is changed last, so an interrupted migration resumes on the next start.
#
# Environment:
#   INSTANCE_TYPE               Registry instance type (default 0)
#   REGISTRY_PORT               HTTP port (default 5000; healthcheck.sh probes the same one)
#   APP_USER                    unprivileged user the app runs as (default registry, created in the image)
#   REGISTRY_EXTRA_OWNED_PATHS  space-separated extra paths the app writes outside /data
#                               (e.g. a datasets volume mounted elsewhere)
set -eu

DATA_DIR=/data
APP_USER="${APP_USER:-registry}"

set -- /Registry/Registry.Web \
    --address "0.0.0.0:${REGISTRY_PORT:-5000}" \
    --instance-type "${INSTANCE_TYPE:-0}" \
    "$DATA_DIR" "$@"

# ensure_owned PATH UID GID: give PATH (recursively) to UID:GID unless its top level already is.
ensure_owned() {
    [ -e "$1" ] || return 0
    [ "$(stat -c %u "$1")" = "$2" ] && return 0

    echo " -> Giving $1 to $APP_USER ($2:$3), first start as non-root: this can take a while on large volumes"
    if find "$1" -mindepth 1 ! -user "$2" -exec chown -h "$2:$3" {} + && chown -h "$2:$3" "$1"; then
        return 0
    fi
    echo " !> Cannot change the owner of $1 (read-only or root-squashed mount?): the app may fail to write there" >&2
}

if [ "$(id -u)" = "0" ]; then
    uid="$(id -u "$APP_USER")"
    gid="$(id -g "$APP_USER")"

    mkdir -p "$DATA_DIR/temp"

    # /data/temp and appsettings.json are often separate mounts (a bind-mounted file keeps the host
    # owner), so they are checked on their own. Word splitting of the extra paths is intended.
    # shellcheck disable=SC2086
    for path in "$DATA_DIR" "$DATA_DIR/temp" "$DATA_DIR/appsettings.json" ${REGISTRY_EXTRA_OWNED_PATHS:-}; do
        ensure_owned "$path" "$uid" "$gid"
    done

    HOME="$(getent passwd "$APP_USER" | cut -d: -f6)"
    export HOME
    exec setpriv --reuid="$uid" --regid="$gid" --init-groups -- "$@"
fi

exec "$@"
