#!/bin/sh
# Registry container healthcheck (POSIX sh), aware of ${INSTANCE_TYPE}:
#   0, 2, 3  (web + processing, web server, thumbnail generator): the anonymous liveness probe
#            GET /health/live on ${REGISTRY_PORT} answers 200;
#   1        (processing node, no HTTP server): the Registry process is alive.
# Liveness only: no database or ddb checks (those are on the token-guarded /quickhealth and /health).
set -u

case "${INSTANCE_TYPE:-0}" in
    1)
        # PID 1 after entrypoint.sh exec's the app; scanning /proc also works under an init (--init).
        for cmdline in /proc/[0-9]*/cmdline; do
            case "$(tr '\000' ' ' < "$cmdline" 2>/dev/null)" in
                /Registry/Registry.Web*) exit 0 ;;
            esac
        done
        echo "Registry process not running" >&2
        exit 1
        ;;
    *)
        exec curl -fsS --max-time 5 -o /dev/null "http://127.0.0.1:${REGISTRY_PORT:-5000}/health/live"
        ;;
esac
